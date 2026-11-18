using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using statsCollector.Domain;
using statsCollector.Infrastructure.Database;
using statsCollector.Infrastructure;
using Polly;

namespace statsCollector.Services;

public interface IStatsRepository
{
    Task UpsertPlayersAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task<LifetimePlayerStatsRow?> GetLifetimePlayerStatsAsync(ulong steamId, CancellationToken cancellationToken);
    Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task UpsertPlayerAnalyticsAsync(IEnumerable<PlayerAnalyticsSnapshot> snapshots, CancellationToken cancellationToken);
    /// <summary>One row per player per round, from the change of their totals over the round.</summary>
    Task UpsertPlayerRoundStatsAsync(IEnumerable<PlayerSnapshot> roundDeltas, CancellationToken cancellationToken);

    // Match Lifecycle
    Task StartMatchAsync(string mapName, string matchUuid, CancellationToken ct);
    Task EndMatchAsync(string matchUuid, CancellationToken ct);
    Task StartRoundAsync(string matchUuid, int roundNumber, CancellationToken ct);
    Task EndRoundAsync(string matchUuid, int roundNumber, int winnerTeam, int winReason, RoundBombInfo bomb, CancellationToken ct);
    Task UpdateRoundEconomyAsync(string matchUuid, int roundNumber, RoundEconomy economy, CancellationToken ct);
    Task OfficiallyEndRoundAsync(string matchUuid, int roundNumber, CancellationToken ct);
    Task VoidRoundsAsync(string matchUuid, int fromRound, CancellationToken ct);
    /// <summary>Logs Critical when the server has local_infile=OFF, which makes every bulk write fail.</summary>
    Task CheckLocalInfileAsync(CancellationToken ct);
}

public sealed class StatsRepository : IStatsRepository
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<StatsRepository> _logger;

    static StatsRepository()
    {
        // Let Dapper map snake_case columns (e.g. damage_dealt) onto PascalCase DTO properties
        // (DamageDealt) when reading the lifetime row back. All write paths use explicit column lists,
        // so this is safe globally.
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public StatsRepository(
        IConnectionFactory connectionFactory,
        ResiliencePipeline resiliencePipeline,
        ILogger<StatsRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _resiliencePipeline = resiliencePipeline;
    }

    public async Task UpsertPlayersAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        // One row per player: temp_player_stats inherits the primary key, and LOAD DATA would keep the first
        // (oldest) duplicate and fail the copy on every retry. Snapshots are cumulative, so the last one wins.
        var snapshotList = snapshots.GroupBy(s => s.SteamId).Select(g => g.Last()).ToList();
        if (snapshotList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            _logger.LogDebug("Starting bulk upsert for {Count} players", snapshotList.Count);
            await UpsertPlayersCoreAsync(snapshotList, ct).ConfigureAwait(false);
            _logger.LogInformation("Successfully completed bulk upsert for {Count} players", snapshotList.Count);
        }, cancellationToken);
    }

    // Every bulk write is a LOAD DATA LOCAL; with local_infile=OFF (MySQL's default) the server rejects each one with
    // error 3948 and nothing is ever stored. Said once here at startup; the writes themselves are dropped as they fail.
    public async Task CheckLocalInfileAsync(CancellationToken cancellationToken)
    {
        var enabled = await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT @@GLOBAL.local_infile;", cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken);
        if (enabled == 0)
        {
            _logger.LogCritical("The database server has local_infile=OFF: every bulk write (LOAD DATA LOCAL) fails with error 3948 and no statistics are stored. " +
                                "Run SET GLOBAL local_infile = 1; (or set local_infile=1 under [mysqld] in my.cnf) and reload the plugin.");
        }
    }

    public async Task<LifetimePlayerStatsRow?> GetLifetimePlayerStatsAsync(ulong steamId, CancellationToken cancellationToken)
    {
        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            const string sql = "SELECT * FROM player_stats WHERE steam_id = @SteamId";
            return await connection.QueryFirstOrDefaultAsync<LifetimePlayerStatsRow>(
                new CommandDefinition(sql, new { SteamId = steamId }, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken) =>
        MergeByMatchUuidAsync("match_player_stats", MatchPlayerColumns, snapshots.Where(s => !string.IsNullOrEmpty(s.MatchUuid)).ToList(),
            ["steam_id"], cancellationToken);

    public Task UpsertPlayerRoundStatsAsync(IEnumerable<PlayerSnapshot> roundDeltas, CancellationToken cancellationToken) =>
        MergeByMatchUuidAsync("player_round_stats", PlayerRoundColumns, roundDeltas.Where(s => !string.IsNullOrEmpty(s.MatchUuid)).ToList(),
            ["round_number", "steam_id"], cancellationToken);

    public Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var rows = snapshots.Where(s => !string.IsNullOrEmpty(s.MatchUuid)).SelectMany(s =>
            s.WeaponKills.Keys.Concat(s.WeaponShots.Keys).Concat(s.WeaponHits.Keys).Concat(s.WeaponDamage.Keys).Concat(s.WeaponDeaths.Keys)
                .Distinct()
                .Select(weapon => new WeaponRow(s.MatchUuid!, s.SteamId, weapon,
                    s.WeaponKills.GetValueOrDefault(weapon), s.WeaponHeadshots.GetValueOrDefault(weapon),
                    s.WeaponShots.GetValueOrDefault(weapon), s.WeaponHits.GetValueOrDefault(weapon),
                    s.WeaponDamage.GetValueOrDefault(weapon), s.WeaponDeaths.GetValueOrDefault(weapon)))).ToList();
        return MergeByMatchUuidAsync("match_weapon_stats", MatchWeaponColumns, rows, ["steam_id", "weapon_name"], cancellationToken);
    }

    internal sealed record WeaponRow(string MatchUuid, ulong SteamId, string Weapon, int Kills, int Headshots, int Shots, int Hits, int Damage, int Deaths);

    // A table column: its name, the DataTable (CLR) type, the MySQL type in the staging table (empty for the lifetime
    // list, whose staging table is a copy of player_stats), and the value it takes from a row. One list per table
    // keeps the staging table, the bulk copy and the merge aligned.
    internal sealed record Column<T>(string Name, Type ClrType, string SqlType, Func<T, object?> Value);

    private static Column<T> Int<T>(string name, Func<T, int> value) => new(name, typeof(int), "INT", row => value(row));
    private static Column<T> Dec<T>(string name, string sqlType, Func<T, decimal> value) => new(name, typeof(decimal), sqlType, row => value(row));
    private static Column<T> Str<T>(string name, string sqlType, Func<T, string?> value) => new(name, typeof(string), sqlType, row => value(row));
    private static Column<T> Bool<T>(string name, Func<T, bool> value) => new(name, typeof(int), "TINYINT", row => value(row) ? 1 : 0);

    // The player's totals over the match (MatchStatsService.Delta), with its rating, ADR and KAST.
    internal static readonly Column<PlayerSnapshot>[] MatchPlayerColumns =
    [
        Str<PlayerSnapshot>("match_uuid", "VARCHAR(64)", s => s.MatchUuid),
        new("steam_id", typeof(ulong), "BIGINT UNSIGNED", s => s.SteamId),
        Int<PlayerSnapshot>("kills", s => s.Kills), Int<PlayerSnapshot>("deaths", s => s.Deaths),
        Int<PlayerSnapshot>("assists", s => s.Assists), Int<PlayerSnapshot>("headshots", s => s.Headshots),
        Int<PlayerSnapshot>("damage_dealt", s => s.DamageDealt), Int<PlayerSnapshot>("mvps", s => s.Mvps),
        Int<PlayerSnapshot>("score", s => s.Score),
        Dec<PlayerSnapshot>("rating2", "DECIMAL(5,3)", s => s.HLTVRating), Dec<PlayerSnapshot>("adr", "DECIMAL(6,2)", s => s.AverageDamagePerRound),
        Dec<PlayerSnapshot>("kast", "DECIMAL(5,2)", s => s.KASTPercentage),
        Int<PlayerSnapshot>("entry_kills", s => s.EntryKills), Int<PlayerSnapshot>("entry_deaths", s => s.EntryDeaths),
        Int<PlayerSnapshot>("entry_kill_attempts", s => s.EntryKillAttempts), Int<PlayerSnapshot>("entry_kill_attempt_wins", s => s.EntryKillAttemptWins),
        Int<PlayerSnapshot>("rounds_played", s => s.RoundsPlayed), Int<PlayerSnapshot>("rounds_won", s => s.RoundsWon),
        Int<PlayerSnapshot>("ct_rounds", s => s.CtRounds), Int<PlayerSnapshot>("t_rounds", s => s.TRounds),
        Int<PlayerSnapshot>("ct_rounds_won", s => s.CtRoundsWon), Int<PlayerSnapshot>("t_rounds_won", s => s.TRoundsWon),
        Int<PlayerSnapshot>("kast_rounds", s => s.KASTRounds), Int<PlayerSnapshot>("rounds_survived", s => s.RoundsSurvived),
        Int<PlayerSnapshot>("rounds_traded", s => s.RoundsTraded),
        Int<PlayerSnapshot>("damage_taken", s => s.DamageTaken), Int<PlayerSnapshot>("team_damage", s => s.TeamDamage),
        Int<PlayerSnapshot>("team_kills", s => s.TeamKills), Int<PlayerSnapshot>("suicides", s => s.Suicides),
        Int<PlayerSnapshot>("utility_damage", s => s.UtilityDamageDealt), Int<PlayerSnapshot>("enemies_flashed", s => s.EnemiesFlashed),
        Int<PlayerSnapshot>("flash_assists", s => s.FlashAssists),
        Int<PlayerSnapshot>("flashes_thrown", s => s.FlashesThrown), Int<PlayerSnapshot>("smokes_thrown", s => s.SmokesThrown),
        Int<PlayerSnapshot>("he_grenades_thrown", s => s.HeGrenadesThrown), Int<PlayerSnapshot>("molotovs_thrown", s => s.MolotovsThrown),
        Int<PlayerSnapshot>("trade_kills", s => s.TradeKills), Int<PlayerSnapshot>("traded_deaths", s => s.TradedDeaths),
        Int<PlayerSnapshot>("first_kills", s => s.OpeningDuelsWon), Int<PlayerSnapshot>("first_deaths", s => s.OpeningDuelsLost),
        Int<PlayerSnapshot>("multi_kill_1", s => s.MultiKill1), Int<PlayerSnapshot>("multi_kill_2", s => s.MultiKill2),
        Int<PlayerSnapshot>("multi_kill_3", s => s.MultiKill3), Int<PlayerSnapshot>("multi_kill_4", s => s.MultiKill4),
        Int<PlayerSnapshot>("multi_kill_5", s => s.MultiKill5),
        Int<PlayerSnapshot>("clutches_won", s => s.ClutchesWon), Int<PlayerSnapshot>("clutches_lost", s => s.ClutchesLost),
        Int<PlayerSnapshot>("clutch_1v1_attempts", s => s.Clutch1v1Attempts), Int<PlayerSnapshot>("clutch_1v1_wins", s => s.Clutch1v1Wins),
        Int<PlayerSnapshot>("clutch_1v2_attempts", s => s.Clutch1v2Attempts), Int<PlayerSnapshot>("clutch_1v2_wins", s => s.Clutch1v2Wins),
        Int<PlayerSnapshot>("clutch_1v3_attempts", s => s.Clutch1v3Attempts), Int<PlayerSnapshot>("clutch_1v3_wins", s => s.Clutch1v3Wins),
        Int<PlayerSnapshot>("clutch_1v4_attempts", s => s.Clutch1v4Attempts), Int<PlayerSnapshot>("clutch_1v4_wins", s => s.Clutch1v4Wins),
        Int<PlayerSnapshot>("clutch_1v5_attempts", s => s.Clutch1v5Attempts), Int<PlayerSnapshot>("clutch_1v5_wins", s => s.Clutch1v5Wins),
        Int<PlayerSnapshot>("bomb_plants", s => s.BombPlants), Int<PlayerSnapshot>("bomb_defuses", s => s.BombDefuses),
        Int<PlayerSnapshot>("shots_fired", s => s.ShotsFired), Int<PlayerSnapshot>("shots_hit", s => s.ShotsHit),
        Int<PlayerSnapshot>("wallbang_kills", s => s.WallbangKills), Int<PlayerSnapshot>("noscope_kills", s => s.NoscopeKills),
        Int<PlayerSnapshot>("thru_smoke_kills", s => s.ThruSmokeKills), Int<PlayerSnapshot>("attacker_blind_kills", s => s.AttackerBlindKills),
        Int<PlayerSnapshot>("money_spent", s => s.MoneySpent), Int<PlayerSnapshot>("kill_reward", s => s.KillReward),
        Int<PlayerSnapshot>("cash_earned", s => s.CashEarned),
        Int<PlayerSnapshot>("ct_kills", s => s.CtKills), Int<PlayerSnapshot>("t_kills", s => s.TKills),
        Int<PlayerSnapshot>("ct_deaths", s => s.CtDeaths), Int<PlayerSnapshot>("t_deaths", s => s.TDeaths),
        Int<PlayerSnapshot>("ct_damage", s => s.CtDamage), Int<PlayerSnapshot>("t_damage", s => s.TDamage),
        Int<PlayerSnapshot>("pistol_rounds_played", s => s.PistolRoundsPlayed), Int<PlayerSnapshot>("pistol_rounds_won", s => s.PistolRoundsWon),
        Int<PlayerSnapshot>("pistol_round_kills", s => s.PistolRoundKills),
        Dec<PlayerSnapshot>("kill_distance_total", "DECIMAL(12,2)", s => s.KillDistanceTotal),
        Dec<PlayerSnapshot>("round_swing", "DECIMAL(10,3)", s => s.RoundSwing)
    ];

    internal static readonly Column<WeaponRow>[] MatchWeaponColumns =
    [
        Str<WeaponRow>("match_uuid", "VARCHAR(64)", w => w.MatchUuid),
        new("steam_id", typeof(ulong), "BIGINT UNSIGNED", w => w.SteamId),
        Str<WeaponRow>("weapon_name", "VARCHAR(100)", w => w.Weapon),
        Int<WeaponRow>("kills", w => w.Kills), Int<WeaponRow>("headshots", w => w.Headshots),
        Int<WeaponRow>("shots", w => w.Shots), Int<WeaponRow>("hits", w => w.Hits),
        Int<WeaponRow>("damage", w => w.Damage), Int<WeaponRow>("deaths", w => w.Deaths)
    ];

    // The player's totals over one round (MatchStatsService.Delta against the round start), plus the round flags and
    // the values stamped during the round (start money, equipment at freeze end).
    internal static readonly Column<PlayerSnapshot>[] PlayerRoundColumns =
    [
        Str<PlayerSnapshot>("match_uuid", "VARCHAR(64)", s => s.MatchUuid),
        Int<PlayerSnapshot>("round_number", s => s.RoundNumber),
        new("steam_id", typeof(ulong), "BIGINT UNSIGNED", s => s.SteamId),
        Int<PlayerSnapshot>("team", s => s.CtRounds > 0 ? 3 : 2),
        Bool<PlayerSnapshot>("won", s => s.RoundsWon > 0),
        Bool<PlayerSnapshot>("pistol_round", s => s.PistolRoundsPlayed > 0),
        Int<PlayerSnapshot>("kills", s => s.Kills), Int<PlayerSnapshot>("deaths", s => s.Deaths),
        Int<PlayerSnapshot>("assists", s => s.Assists), Int<PlayerSnapshot>("headshots", s => s.Headshots),
        Int<PlayerSnapshot>("damage_dealt", s => s.DamageDealt), Int<PlayerSnapshot>("damage_taken", s => s.DamageTaken),
        Int<PlayerSnapshot>("utility_damage", s => s.UtilityDamageDealt), Int<PlayerSnapshot>("enemies_flashed", s => s.EnemiesFlashed),
        Int<PlayerSnapshot>("flash_assists", s => s.FlashAssists),
        Int<PlayerSnapshot>("team_damage", s => s.TeamDamage), Int<PlayerSnapshot>("team_kills", s => s.TeamKills),
        Bool<PlayerSnapshot>("first_kill", s => s.OpeningDuelsWon > 0), Bool<PlayerSnapshot>("first_death", s => s.OpeningDuelsLost > 0),
        Int<PlayerSnapshot>("trade_kills", s => s.TradeKills), Bool<PlayerSnapshot>("traded_death", s => s.TradedDeaths > 0),
        Bool<PlayerSnapshot>("survived", s => s.SurvivedThisRound),
        Bool<PlayerSnapshot>("kast", s => s.HadKillThisRound || s.HadAssistThisRound || s.SurvivedThisRound || s.WasTradedThisRound),
        Int<PlayerSnapshot>("clutch_opponents", ClutchOpponents),
        Bool<PlayerSnapshot>("clutch_won", s => s.Clutch1v1Wins + s.Clutch1v2Wins + s.Clutch1v3Wins + s.Clutch1v4Wins + s.Clutch1v5Wins > 0),
        Int<PlayerSnapshot>("bomb_plants", s => s.BombPlants), Int<PlayerSnapshot>("bomb_defuses", s => s.BombDefuses),
        Bool<PlayerSnapshot>("mvp", s => s.Mvps > 0), Int<PlayerSnapshot>("score", s => s.Score),
        Int<PlayerSnapshot>("start_money", s => s.RoundStartMoney), Int<PlayerSnapshot>("end_money", s => s.RoundEndMoney),
        Int<PlayerSnapshot>("equipment_value", s => s.EquipmentValueStart), Int<PlayerSnapshot>("money_spent", s => s.MoneySpent),
        Int<PlayerSnapshot>("kill_reward", s => s.KillReward), Int<PlayerSnapshot>("cash_earned", s => s.CashEarned),
        Int<PlayerSnapshot>("money_saved", s => s.MoneySaved), Int<PlayerSnapshot>("live_time", s => s.LiveTime),
        Dec<PlayerSnapshot>("round_swing", "DECIMAL(10,3)", s => s.RoundSwing)
    ];

    // The 1vX the player attempted this round (0 when none).
    internal static int ClutchOpponents(PlayerSnapshot s) =>
        s.Clutch1v1Attempts > 0 ? 1 : s.Clutch1v2Attempts > 0 ? 2 : s.Clutch1v3Attempts > 0 ? 3 : s.Clutch1v4Attempts > 0 ? 4 : s.Clutch1v5Attempts > 0 ? 5 : 0;

    // Bulk-copies the rows into a staging table keyed by match UUID, makes sure their players rows exist, then inserts
    // them with the match id resolved, replacing the data columns of a row with the same key (the table's UNIQUE KEY on
    // match_id and the key columns).
    private async Task MergeByMatchUuidAsync<T>(string table, IReadOnlyList<Column<T>> columns, IReadOnlyList<T> rows, string[] keyColumns, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

            var dataTable = ToDataTable(columns, rows);
            var staging = $"temp_{table}_uuid";
            await ExecuteAsync(connection, $"CREATE TEMPORARY TABLE {staging} (row_id INT AUTO_INCREMENT PRIMARY KEY, {string.Join(", ", columns.Select(c => $"{c.Name} {c.SqlType}"))});", ct).ConfigureAwait(false);

            // The connection's DefaultCommandTimeout does not reach LOAD DATA: MySqlBulkCopy runs it with
            // CommandTimeout = BulkCopyTimeout, 0 (no timeout) unless set, so a server that stopped answering held the
            // copy until the socket died. Every bulk copy sets it.
            var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = staging, BulkCopyTimeout = (int)ResiliencePolicies.AttemptTimeout.TotalSeconds };
            AddColumnMappingsByName(bulkCopy, dataTable);
            EnsureBulkCopySucceeded(await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false));

            await ExecuteAsync(connection, $"INSERT IGNORE INTO players (steam_id) SELECT DISTINCT steam_id FROM {staging};", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, MergeSql(table, columns.Select(c => c.Name).ToList(), keyColumns), ct).ConfigureAwait(false);

            var matchUuid = columns.First(c => c.Name == "match_uuid");
            var unwritten = await LogUnwrittenAsync(connection, table, rows.Select(row => (string)matchUuid.Value(row)!).ToList(), ct).ConfigureAwait(false);
            _logger.LogInformation("Bulk upserted {Count} rows into {Table}", rows.Count - unwritten, table);
        }, cancellationToken);
    }

    // INSERT ... SELECT ... JOIN matches writes nothing for a row whose match has no matches row, and a retry never
    // will: said here instead of counted as written. Returns how many of the rows that was.
    private async Task<int> LogUnwrittenAsync(MySqlConnection connection, string table, IReadOnlyList<string> matchUuids, CancellationToken cancellationToken)
    {
        var known = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT match_uuid FROM matches WHERE match_uuid IN @Uuids;",
            new { Uuids = matchUuids.Distinct().ToList() }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet();
        var unwritten = matchUuids.Where(uuid => !known.Contains(uuid)).ToList();
        if (unwritten.Count > 0)
        {
            _logger.LogCritical("{Count} rows for {Table} were not written: no matches row for {Matches}.",
                unwritten.Count, table, string.Join(", ", unwritten.Distinct()));
        }
        return unwritten.Count;
    }

    // A batch can hold several versions of one key (a round row is written three times per round, and an outage queues
    // them together). Each later row of a key overwrites the earlier one, so the rows go in the order they were staged
    // (row_id) and the last version is kept, whatever order the join would return them in.
    internal static string MergeSql(string table, IReadOnlyList<string> columns, string[] keyColumns)
    {
        var staging = $"temp_{table}_uuid";
        var data = columns.Where(c => c != "match_uuid").ToList();
        var updates = data.Where(c => !keyColumns.Contains(c)).Select(c => $"{c} = t.{c}").ToList();
        return $"""
            INSERT INTO {table} (match_id, {string.Join(", ", data)})
            SELECT m.id, {string.Join(", ", data.Select(c => $"t.{c}"))}
            FROM {staging} t
            JOIN matches m ON t.match_uuid = m.match_uuid
            ORDER BY t.row_id
            ON DUPLICATE KEY UPDATE {string.Join(", ", updates)};
            """;
    }

    public async Task UpsertPlayerAnalyticsAsync(IEnumerable<PlayerAnalyticsSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var list = snapshots.Where(s => !string.IsNullOrEmpty(s.MatchUuid)).ToList();
        if (list.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

            // Ensure parent rows exist for the steam_id FK before inserting analytics.
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT IGNORE INTO players (steam_id, name) VALUES (@SteamId, @Name);",
                list, cancellationToken: ct)).ConfigureAwait(false);

            // The row is written at a disconnect, at match end and after a round restore: each later write replaces
            // the earlier one (idempotency_key is UNIQUE), and a re-processed buffered batch writes the same values
            // again. match_id is resolved from the match UUID. calculated_at is the server's clock, like the match and
            // round timestamps (a UTC value from here would be read in the session time zone).
            const string sql = """
                INSERT INTO player_advanced_analytics
                    (match_id, steam_id, calculated_at, name, rating2, kills_per_round, deaths_per_round, impact_score,
                     kast_percentage, average_damage_per_round, utility_impact_score, clutch_success_rate, trade_success_rate,
                     flash_waste, entry_kills, entry_deaths, entry_kill_attempts, entry_kill_attempt_wins, entry_success_rate,
                     rounds_played, kd_ratio, headshot_percentage, opening_kill_ratio, trade_kill_ratio,
                     grenade_effectiveness_rate, flash_effectiveness_rate, utility_usage_per_round, average_money_spent_per_round,
                     performance_score, top_weapon_by_kills, survival_rating, utility_score, clutch_points,
                     wallbang_kills, idempotency_key)
                SELECT m.id, @SteamId, CURRENT_TIMESTAMP, @Name, @Rating2, @KillsPerRound, @DeathsPerRound, @ImpactScore,
                     @KastPercentage, @AverageDamagePerRound, @UtilityImpactScore, @ClutchSuccessRate, @TradeSuccessRate,
                     @FlashWaste, @EntryKills, @EntryDeaths, @EntryKillAttempts, @EntryKillAttemptWins, @EntrySuccessRate,
                     @RoundsPlayed, @KdRatio, @HeadshotPercentage, @OpeningKillRatio, @TradeKillRatio,
                     @GrenadeEffectivenessRate, @FlashEffectivenessRate, @UtilityUsagePerRound, @AverageMoneySpentPerRound,
                     @PerformanceScore, @TopWeaponByKills, @SurvivalRating, @UtilityScore, @ClutchPoints,
                     @WallbangKills, @IdempotencyKey
                FROM matches m WHERE m.match_uuid = @MatchUuid
                ON DUPLICATE KEY UPDATE
                    calculated_at = CURRENT_TIMESTAMP, name = @Name, rating2 = @Rating2, kills_per_round = @KillsPerRound,
                    deaths_per_round = @DeathsPerRound, impact_score = @ImpactScore, kast_percentage = @KastPercentage,
                    average_damage_per_round = @AverageDamagePerRound, utility_impact_score = @UtilityImpactScore,
                    clutch_success_rate = @ClutchSuccessRate, trade_success_rate = @TradeSuccessRate, flash_waste = @FlashWaste,
                    entry_kills = @EntryKills, entry_deaths = @EntryDeaths, entry_kill_attempts = @EntryKillAttempts,
                    entry_kill_attempt_wins = @EntryKillAttemptWins, entry_success_rate = @EntrySuccessRate,
                    rounds_played = @RoundsPlayed, kd_ratio = @KdRatio, headshot_percentage = @HeadshotPercentage,
                    opening_kill_ratio = @OpeningKillRatio, trade_kill_ratio = @TradeKillRatio,
                    grenade_effectiveness_rate = @GrenadeEffectivenessRate, flash_effectiveness_rate = @FlashEffectivenessRate,
                    utility_usage_per_round = @UtilityUsagePerRound, average_money_spent_per_round = @AverageMoneySpentPerRound,
                    performance_score = @PerformanceScore, top_weapon_by_kills = @TopWeaponByKills, survival_rating = @SurvivalRating,
                    utility_score = @UtilityScore, clutch_points = @ClutchPoints, wallbang_kills = @WallbangKills;
                """;

            await connection.ExecuteAsync(new CommandDefinition(sql, list, cancellationToken: ct)).ConfigureAwait(false);
            var unwritten = await LogUnwrittenAsync(connection, "player_advanced_analytics", list.Select(s => s.MatchUuid).ToList(), ct).ConfigureAwait(false);
            _logger.LogInformation("Persisted {Count} player analytics snapshots", list.Count - unwritten);
        }, cancellationToken);
    }

    // The position writer may have created the row first (INSERT IGNORE with the map only): fill in what it left
    // empty, keep what is set. Row alias form (MySQL 8.0.19+); the VALUES() function is deprecated.
    public Task StartMatchAsync(string mapName, string matchUuid, CancellationToken ct) =>
        ExecuteResilientAsync("""
            INSERT INTO matches (match_uuid, map_name, status) VALUES (@MatchUuid, @MapName, 'IN_PROGRESS') AS new
            ON DUPLICATE KEY UPDATE status = 'IN_PROGRESS', map_name = COALESCE(NULLIF(matches.map_name, ''), new.map_name);
            """, new { MatchUuid = matchUuid, MapName = mapName }, ct);

    // COALESCE: a retried batch must not move end_time to the retry time.
    public Task EndMatchAsync(string matchUuid, CancellationToken ct) =>
        ExecuteResilientAsync(
            "UPDATE matches SET end_time = COALESCE(end_time, CURRENT_TIMESTAMP), status = 'COMPLETED' WHERE match_uuid = @MatchUuid",
            new { MatchUuid = matchUuid }, ct);

    // Found rows (MySqlConnector's default, UseAffectedRows=false): 0 only when no matches row matched the UUID.
    public async Task StartRoundAsync(string matchUuid, int roundNumber, CancellationToken ct)
    {
        var found = await ExecuteResilientAsync("""
            INSERT INTO rounds (match_id, round_number)
            SELECT id, @RoundNumber FROM matches WHERE match_uuid = @MatchUuid
            ON DUPLICATE KEY UPDATE round_number = rounds.round_number;
            """, new { MatchUuid = matchUuid, RoundNumber = roundNumber }, ct).ConfigureAwait(false);
        if (found == 0) _logger.LogCritical("Round {Round} of match {Match} was not written: no matches row.", roundNumber, matchUuid);
    }

    public Task EndRoundAsync(string matchUuid, int roundNumber, int winnerTeam, int winReason, RoundBombInfo bomb, CancellationToken ct) =>
        ExecuteResilientAsync("""
            UPDATE rounds r
            JOIN matches m ON r.match_id = m.id
            SET r.end_time = COALESCE(r.end_time, CURRENT_TIMESTAMP), r.winner_team = @Winner, r.win_type = @WinType,
                r.bomb_site = @BombSite, r.bomb_plant_time = @BombPlantTime, r.defuse_time_left = @DefuseTimeLeft
            WHERE m.match_uuid = @MatchUuid AND r.round_number = @RoundNumber;
            """, new
            {
                MatchUuid = matchUuid, RoundNumber = roundNumber, Winner = winnerTeam, WinType = winReason,
                BombSite = bomb.Site, BombPlantTime = bomb.PlantTimeSeconds, DefuseTimeLeft = bomb.DefuseTimeLeftSeconds
            }, ct);

    // Both sides' money at round start and equipment at freeze end: the round's buy.
    public Task UpdateRoundEconomyAsync(string matchUuid, int roundNumber, RoundEconomy economy, CancellationToken ct) =>
        ExecuteResilientAsync("""
            UPDATE rounds r
            JOIN matches m ON r.match_id = m.id
            SET r.ct_start_money = @CtStartMoney, r.t_start_money = @TStartMoney,
                r.ct_equipment_value = @CtEquipmentValue, r.t_equipment_value = @TEquipmentValue
            WHERE m.match_uuid = @MatchUuid AND r.round_number = @RoundNumber;
            """, new
            {
                MatchUuid = matchUuid, RoundNumber = roundNumber,
                economy.CtStartMoney, economy.TStartMoney, economy.CtEquipmentValue, economy.TEquipmentValue
            }, ct);

    public Task OfficiallyEndRoundAsync(string matchUuid, int roundNumber, CancellationToken ct) =>
        ExecuteResilientAsync("""
            UPDATE rounds r
            JOIN matches m ON r.match_id = m.id
            SET r.official_end_time = COALESCE(r.official_end_time, CURRENT_TIMESTAMP)
            WHERE m.match_uuid = @MatchUuid AND r.round_number = @RoundNumber;
            """, new { MatchUuid = matchUuid, RoundNumber = roundNumber }, ct);

    // The rounds rows of rounds a restore voided; the replay writes them again. Their position rows are removed by the
    // position writer, in its own queue order.
    public async Task VoidRoundsAsync(string matchUuid, int fromRound, CancellationToken ct)
    {
        try
        {
            foreach (var table in new[] { "player_round_stats", "rounds" })
            {
                await ExecuteResilientAsync(
                    $"DELETE t FROM {table} t JOIN matches m ON t.match_id = m.id WHERE m.match_uuid = @MatchUuid AND t.round_number >= @FromRound;",
                    new { MatchUuid = matchUuid, FromRound = fromRound }, ct).ConfigureAwait(false);
            }
        }
        // Cleanup only: a delete that keeps failing (no DELETE grant) must not hold up every write queued behind it;
        // only an outage is worth a retry.
        catch (Exception ex) when (ex is not OperationCanceledException && !ResiliencePolicies.IsOutage(ex))
        {
            _logger.LogWarning(ex, "Could not remove the rounds {FromRound}+ of match {Match} voided by a restore; they stay next to the replayed rounds.", fromRound, matchUuid);
        }
    }

    private Task<int> ExecuteResilientAsync(string sql, object parameters, CancellationToken cancellationToken) =>
        _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken).AsTask();

    // MySqlBulkCopy maps DataTable columns to the destination by ordinal unless told otherwise.
    // Mapping each source column to the destination column of the same name removes any dependency
    // on the DataTable's column order matching the table definition (and leaves staging keys to auto-increment).
    internal static void AddColumnMappingsByName(MySqlBulkCopy bulkCopy, DataTable dataTable)
    {
        for (var i = 0; i < dataTable.Columns.Count; i++)
        {
            bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, dataTable.Columns[i].ColumnName));
        }
    }

    // The player's lifetime totals and ratios: player_stats, one row per player. The staging table is created LIKE
    // player_stats, so these carry no SQL type; the copy and the merge both come from this list.
    internal static readonly Column<PlayerSnapshot>[] LifetimeColumns =
    [
        new("steam_id", typeof(ulong), "", s => s.SteamId), Str<PlayerSnapshot>("name", "", s => s.Name), Int<PlayerSnapshot>("kills", s => s.Kills),
        Int<PlayerSnapshot>("deaths", s => s.Deaths), Int<PlayerSnapshot>("assists", s => s.Assists),
        Int<PlayerSnapshot>("headshots", s => s.Headshots), Int<PlayerSnapshot>("damage_dealt", s => s.DamageDealt),
        Int<PlayerSnapshot>("damage_taken", s => s.DamageTaken), Int<PlayerSnapshot>("damage_armor", s => s.DamageArmor),
        Int<PlayerSnapshot>("shots_fired", s => s.ShotsFired), Int<PlayerSnapshot>("shots_hit", s => s.ShotsHit),
        Int<PlayerSnapshot>("mvps", s => s.Mvps), Int<PlayerSnapshot>("score", s => s.Score),
        Int<PlayerSnapshot>("rounds_played", s => s.RoundsPlayed), Int<PlayerSnapshot>("rounds_won", s => s.RoundsWon),
        Int<PlayerSnapshot>("kast_rounds", s => s.KASTRounds), Int<PlayerSnapshot>("total_spawns", s => s.TotalSpawns),
        Int<PlayerSnapshot>("playtime_seconds", s => s.PlaytimeSeconds), Int<PlayerSnapshot>("ct_rounds", s => s.CtRounds),
        Int<PlayerSnapshot>("t_rounds", s => s.TRounds), Int<PlayerSnapshot>("grenades_thrown", s => s.GrenadesThrown),
        Int<PlayerSnapshot>("flashes_thrown", s => s.FlashesThrown), Int<PlayerSnapshot>("smokes_thrown", s => s.SmokesThrown),
        Int<PlayerSnapshot>("molotovs_thrown", s => s.MolotovsThrown), Int<PlayerSnapshot>("he_grenades_thrown", s => s.HeGrenadesThrown),
        Int<PlayerSnapshot>("decoys_thrown", s => s.DecoysThrown), Int<PlayerSnapshot>("times_blinded", s => s.TimesBlinded),
        Int<PlayerSnapshot>("flash_assists", s => s.FlashAssists), new("total_blind_time", typeof(decimal), "", s => s.TotalBlindTime),
        new("total_blind_time_inflicted", typeof(decimal), "", s => s.TotalBlindTimeInflicted),
        Int<PlayerSnapshot>("utility_damage_dealt", s => s.UtilityDamageDealt),
        Int<PlayerSnapshot>("utility_damage_taken", s => s.UtilityDamageTaken), Int<PlayerSnapshot>("bomb_plants", s => s.BombPlants),
        Int<PlayerSnapshot>("bomb_defuses", s => s.BombDefuses), Int<PlayerSnapshot>("bomb_explosions", s => s.BombExplosions),
        Int<PlayerSnapshot>("bomb_plant_attempts", s => s.BombPlantAttempts), Int<PlayerSnapshot>("bomb_plant_aborts", s => s.BombPlantAborts),
        Int<PlayerSnapshot>("bomb_defuse_attempts", s => s.BombDefuseAttempts), Int<PlayerSnapshot>("bomb_defuse_aborts", s => s.BombDefuseAborts),
        Int<PlayerSnapshot>("bomb_defuse_with_kit", s => s.BombDefuseWithKit),
        Int<PlayerSnapshot>("bomb_defuse_without_kit", s => s.BombDefuseWithoutKit), Int<PlayerSnapshot>("bomb_drops", s => s.BombDrops),
        Int<PlayerSnapshot>("bomb_pickups", s => s.BombPickups), Int<PlayerSnapshot>("defuser_pickups", s => s.DefuserPickups),
        Int<PlayerSnapshot>("clutch_defuses", s => s.ClutchDefuses), Int<PlayerSnapshot>("total_plant_time", s => s.TotalPlantTime),
        Int<PlayerSnapshot>("total_defuse_time", s => s.TotalDefuseTime), Int<PlayerSnapshot>("bomb_kills", s => s.BombKills),
        Int<PlayerSnapshot>("bomb_deaths", s => s.BombDeaths), Int<PlayerSnapshot>("jumps", s => s.Jumps),
        Int<PlayerSnapshot>("money_spent", s => s.MoneySpent), Int<PlayerSnapshot>("equipment_value", s => s.EquipmentValue),
        Int<PlayerSnapshot>("items_purchased", s => s.ItemsPurchased), Int<PlayerSnapshot>("items_picked_up", s => s.ItemsPickedUp),
        Int<PlayerSnapshot>("round_start_money", s => s.RoundStartMoney), Int<PlayerSnapshot>("round_end_money", s => s.RoundEndMoney),
        Int<PlayerSnapshot>("equipment_value_start", s => s.EquipmentValueStart),
        Int<PlayerSnapshot>("equipment_value_end", s => s.EquipmentValueEnd), Int<PlayerSnapshot>("enemies_flashed", s => s.EnemiesFlashed),
        Int<PlayerSnapshot>("teammates_flashed", s => s.TeammatesFlashed), Int<PlayerSnapshot>("effective_flashes", s => s.EffectiveFlashes),
        Int<PlayerSnapshot>("effective_he_grenades", s => s.EffectiveHEGrenades), Int<PlayerSnapshot>("effective_molotovs", s => s.EffectiveMolotovs),
        Int<PlayerSnapshot>("flash_waste", s => s.FlashWaste), Int<PlayerSnapshot>("multi_kill_nades", s => s.MultiKillNades),
        Int<PlayerSnapshot>("nade_kills", s => s.NadeKills), Int<PlayerSnapshot>("entry_kills", s => s.EntryKills),
        Int<PlayerSnapshot>("entry_deaths", s => s.EntryDeaths), Int<PlayerSnapshot>("entry_kill_attempts", s => s.EntryKillAttempts),
        Int<PlayerSnapshot>("entry_kill_attempt_wins", s => s.EntryKillAttemptWins), Int<PlayerSnapshot>("trade_kills", s => s.TradeKills),
        Int<PlayerSnapshot>("traded_deaths", s => s.TradedDeaths), Int<PlayerSnapshot>("high_impact_kills", s => s.HighImpactKills),
        Int<PlayerSnapshot>("low_impact_kills", s => s.LowImpactKills), Int<PlayerSnapshot>("multi_kills", s => s.MultiKills),
        Int<PlayerSnapshot>("multi_kill_2", s => s.MultiKill2), Int<PlayerSnapshot>("multi_kill_3", s => s.MultiKill3),
        Int<PlayerSnapshot>("multi_kill_4", s => s.MultiKill4), Int<PlayerSnapshot>("multi_kill_5", s => s.MultiKill5),
        Int<PlayerSnapshot>("opening_duels_won", s => s.OpeningDuelsWon), Int<PlayerSnapshot>("opening_duels_lost", s => s.OpeningDuelsLost),
        Int<PlayerSnapshot>("revenges", s => s.Revenges), Int<PlayerSnapshot>("clutches_won", s => s.ClutchesWon),
        Int<PlayerSnapshot>("clutches_lost", s => s.ClutchesLost), Int<PlayerSnapshot>("clutch_kills", s => s.ClutchKills),
        Dec<PlayerSnapshot>("clutch_points", "", s => s.ClutchPoints), Int<PlayerSnapshot>("mvps_eliminations", s => s.MvpsEliminations),
        Int<PlayerSnapshot>("mvps_bomb", s => s.MvpsBomb), Int<PlayerSnapshot>("mvps_hostage", s => s.MvpsHostage),
        Int<PlayerSnapshot>("headshots_hit", s => s.HeadshotsHit), Int<PlayerSnapshot>("chest_hits", s => s.ChestHits),
        Int<PlayerSnapshot>("stomach_hits", s => s.StomachHits), Int<PlayerSnapshot>("arm_hits", s => s.ArmHits),
        Int<PlayerSnapshot>("leg_hits", s => s.LegHits), Dec<PlayerSnapshot>("kd_ratio", "", s => s.KDRatio),
        Dec<PlayerSnapshot>("headshot_percentage", "", s => s.HeadshotPercentage),
        Dec<PlayerSnapshot>("accuracy_percentage", "", s => s.AccuracyPercentage), Dec<PlayerSnapshot>("kast_percentage", "", s => s.KASTPercentage),
        Dec<PlayerSnapshot>("average_damage_per_round", "", s => s.AverageDamagePerRound), Dec<PlayerSnapshot>("hltv_rating", "", s => s.HLTVRating),
        Dec<PlayerSnapshot>("impact_rating", "", s => s.ImpactRating), Dec<PlayerSnapshot>("survival_rating", "", s => s.SurvivalRating),
        Dec<PlayerSnapshot>("utility_score", "", s => s.UtilityScore), Int<PlayerSnapshot>("noscope_kills", s => s.NoscopeKills),
        Int<PlayerSnapshot>("thru_smoke_kills", s => s.ThruSmokeKills), Int<PlayerSnapshot>("attacker_blind_kills", s => s.AttackerBlindKills),
        Int<PlayerSnapshot>("wallbang_kills", s => s.WallbangKills), Int<PlayerSnapshot>("pings", s => s.Pings),
        Int<PlayerSnapshot>("footsteps", s => s.Footsteps), Dec<PlayerSnapshot>("round_swing", "", s => s.RoundSwing),
        Int<PlayerSnapshot>("team_kills", s => s.TeamKills), Int<PlayerSnapshot>("suicides", s => s.Suicides),
        Int<PlayerSnapshot>("clutch_1v1_attempts", s => s.Clutch1v1Attempts), Int<PlayerSnapshot>("clutch_1v1_wins", s => s.Clutch1v1Wins),
        Int<PlayerSnapshot>("clutch_1v2_attempts", s => s.Clutch1v2Attempts), Int<PlayerSnapshot>("clutch_1v2_wins", s => s.Clutch1v2Wins),
        Int<PlayerSnapshot>("clutch_1v3_attempts", s => s.Clutch1v3Attempts), Int<PlayerSnapshot>("clutch_1v3_wins", s => s.Clutch1v3Wins),
        Int<PlayerSnapshot>("clutch_1v4_attempts", s => s.Clutch1v4Attempts), Int<PlayerSnapshot>("clutch_1v4_wins", s => s.Clutch1v4Wins),
        Int<PlayerSnapshot>("clutch_1v5_attempts", s => s.Clutch1v5Attempts), Int<PlayerSnapshot>("clutch_1v5_wins", s => s.Clutch1v5Wins),
        Int<PlayerSnapshot>("kill_reward", s => s.KillReward), Int<PlayerSnapshot>("cash_earned", s => s.CashEarned),
        Int<PlayerSnapshot>("money_saved", s => s.MoneySaved), Int<PlayerSnapshot>("live_time", s => s.LiveTime),
        Int<PlayerSnapshot>("team_damage", s => s.TeamDamage), Int<PlayerSnapshot>("multi_kill_1", s => s.MultiKill1),
        Int<PlayerSnapshot>("ct_kills", s => s.CtKills), Int<PlayerSnapshot>("t_kills", s => s.TKills),
        Int<PlayerSnapshot>("ct_deaths", s => s.CtDeaths), Int<PlayerSnapshot>("t_deaths", s => s.TDeaths),
        Int<PlayerSnapshot>("ct_damage", s => s.CtDamage), Int<PlayerSnapshot>("t_damage", s => s.TDamage),
        Int<PlayerSnapshot>("ct_first_kills", s => s.CtFirstKills), Int<PlayerSnapshot>("t_first_kills", s => s.TFirstKills),
        Int<PlayerSnapshot>("ct_first_deaths", s => s.CtFirstDeaths), Int<PlayerSnapshot>("t_first_deaths", s => s.TFirstDeaths),
        Int<PlayerSnapshot>("rounds_survived", s => s.RoundsSurvived), Int<PlayerSnapshot>("rounds_traded", s => s.RoundsTraded),
        Int<PlayerSnapshot>("ct_rounds_won", s => s.CtRoundsWon), Int<PlayerSnapshot>("t_rounds_won", s => s.TRoundsWon),
        Int<PlayerSnapshot>("pistol_rounds_played", s => s.PistolRoundsPlayed), Int<PlayerSnapshot>("pistol_rounds_won", s => s.PistolRoundsWon),
        Int<PlayerSnapshot>("pistol_round_kills", s => s.PistolRoundKills), Dec<PlayerSnapshot>("kill_distance_total", "", s => s.KillDistanceTotal),
        Dec<PlayerSnapshot>("longest_kill", "", s => s.LongestKill)
    ];

    internal static string LifetimeMergeSql()
    {
        var columns = LifetimeColumns.Select(c => c.Name).ToList();
        return $"""
            INSERT INTO player_stats ({string.Join(", ", columns)})
            SELECT {string.Join(", ", columns)} FROM temp_player_stats AS src
            ON DUPLICATE KEY UPDATE {string.Join(", ", columns.Where(c => c != "steam_id").Select(c => $"{c} = src.{c}"))};
            """;
    }

    private async Task UpsertPlayersCoreAsync(IReadOnlyList<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Bulk copy into a temporary copy of the table, then merge on the server.
        var dataTable = ToDataTable(LifetimeColumns, snapshots);
        await ExecuteAsync(connection, "CREATE TEMPORARY TABLE temp_player_stats LIKE player_stats;", cancellationToken).ConfigureAwait(false);

        var bulkCopy = new MySqlBulkCopy(connection)
        {
            DestinationTableName = "temp_player_stats",
            BulkCopyTimeout = (int)ResiliencePolicies.AttemptTimeout.TotalSeconds
        };
        AddColumnMappingsByName(bulkCopy, dataTable);
        EnsureBulkCopySucceeded(await bulkCopy.WriteToServerAsync(dataTable, cancellationToken).ConfigureAwait(false));

        // Parent rows must exist before the FK-constrained merge into player_stats.
        await ExecuteAsync(connection,
            "INSERT INTO players (steam_id, name) SELECT steam_id, name FROM temp_player_stats AS src " +
            "ON DUPLICATE KEY UPDATE name = src.name, last_seen = CURRENT_TIMESTAMP;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, LifetimeMergeSql(), cancellationToken).ConfigureAwait(false);
    }

    private static DataTable ToDataTable<T>(IReadOnlyList<Column<T>> columns, IReadOnlyList<T> rows)
    {
        var dataTable = new DataTable();
        foreach (var column in columns) dataTable.Columns.Add(column.Name, column.ClrType);
        foreach (var row in rows) dataTable.Rows.Add(columns.Select(column => column.Value(row)).ToArray());
        return dataTable;
    }

    private static Task<int> ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));

    // Notes are DECIMAL rounding to the column scale (kd_ratio 2.41176 -> 2.412) and out-of-range values are
    // derived ratios saturating at the column limit (utility_score after a new player's first round); MySQL
    // stores both and the next save rewrites them. Any other warning means the copy lost or mangled data.
    internal static void EnsureBulkCopySucceeded(MySqlBulkCopyResult result)
    {
        var problems = result.Warnings
            .Where(warning => !string.Equals(warning.Level, "Note", StringComparison.OrdinalIgnoreCase) &&
                              warning.ErrorCode != MySqlErrorCode.WarningDataOutOfRange)
            .Select(warning => warning.Message)
            .ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", problems));
        }
    }
}
