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
using Polly.Registry;

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
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<StatsRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("database");
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
            try
            {
                await UpsertPlayersCoreAsync(snapshotList, ct).ConfigureAwait(false);
                _logger.LogInformation("Successfully completed bulk upsert for {Count} players", snapshotList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk upsert players failed for {Count} players", snapshotList.Count);
                throw;
            }
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

    // A column of a per-match table: its name, the DataTable (CLR) type, the MySQL type of the staging table, and the
    // value it takes from a row. One list per table keeps the staging table, the bulk copy and the merge aligned.
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
            await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

            var dataTable = new DataTable();
            foreach (var column in columns) dataTable.Columns.Add(column.Name, column.ClrType);
            foreach (var row in rows) dataTable.Rows.Add(columns.Select(column => column.Value(row)).ToArray());

            var staging = $"temp_{table}_uuid";
            await ExecuteAsync(connection, $"CREATE TEMPORARY TABLE {staging} (row_id INT AUTO_INCREMENT PRIMARY KEY, {string.Join(", ", columns.Select(c => $"{c.Name} {c.SqlType}"))});", ct).ConfigureAwait(false);

            var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = staging };
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
            ON DUPLICATE KEY UPDATE {string.Join(", ", updates)};
            """;
    }

    public async Task UpsertPlayerAnalyticsAsync(IEnumerable<PlayerAnalyticsSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var list = snapshots.Where(s => !string.IsNullOrEmpty(s.MatchUuid)).ToList();
        if (list.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

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
                     flash_assisted_kills, wallbang_kills, idempotency_key)
                SELECT m.id, @SteamId, CURRENT_TIMESTAMP, @Name, @Rating2, @KillsPerRound, @DeathsPerRound, @ImpactScore,
                     @KastPercentage, @AverageDamagePerRound, @UtilityImpactScore, @ClutchSuccessRate, @TradeSuccessRate,
                     @FlashWaste, @EntryKills, @EntryDeaths, @EntryKillAttempts, @EntryKillAttemptWins, @EntrySuccessRate,
                     @RoundsPlayed, @KdRatio, @HeadshotPercentage, @OpeningKillRatio, @TradeKillRatio,
                     @GrenadeEffectivenessRate, @FlashEffectivenessRate, @UtilityUsagePerRound, @AverageMoneySpentPerRound,
                     @PerformanceScore, @TopWeaponByKills, @SurvivalRating, @UtilityScore, @ClutchPoints,
                     @FlashAssistedKills, @WallbangKills, @IdempotencyKey
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
                    utility_score = @UtilityScore, clutch_points = @ClutchPoints, flash_assisted_kills = @FlashAssistedKills,
                    wallbang_kills = @WallbangKills;
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

    private async Task UpsertPlayersCoreAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var snapshotList = snapshots.ToList();

        if (snapshotList.Count == 0) return;

        await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        // High-performance pattern for player stats (many columns):
        // 1. Create a DataTable matching the full schema
        // 2. Bulk copy to a temporary table
        // 3. Perform a server-side merge into the main tables

        var dataTable = new DataTable();
        // Add all columns
        dataTable.Columns.Add("steam_id", typeof(ulong));
        dataTable.Columns.Add("name", typeof(string));
        dataTable.Columns.Add("kills", typeof(int));
        dataTable.Columns.Add("deaths", typeof(int));
        dataTable.Columns.Add("assists", typeof(int));
        dataTable.Columns.Add("headshots", typeof(int));
        dataTable.Columns.Add("damage_dealt", typeof(int));
        dataTable.Columns.Add("damage_taken", typeof(int));
        dataTable.Columns.Add("damage_armor", typeof(int));
        dataTable.Columns.Add("shots_fired", typeof(int));
        dataTable.Columns.Add("shots_hit", typeof(int));
        dataTable.Columns.Add("mvps", typeof(int));
        dataTable.Columns.Add("score", typeof(int));
        dataTable.Columns.Add("rounds_played", typeof(int));
        dataTable.Columns.Add("rounds_won", typeof(int));
        dataTable.Columns.Add("kast_rounds", typeof(int));
        dataTable.Columns.Add("total_spawns", typeof(int));
        dataTable.Columns.Add("playtime_seconds", typeof(int));
        dataTable.Columns.Add("ct_rounds", typeof(int));
        dataTable.Columns.Add("t_rounds", typeof(int));
        dataTable.Columns.Add("grenades_thrown", typeof(int));
        dataTable.Columns.Add("flashes_thrown", typeof(int));
        dataTable.Columns.Add("smokes_thrown", typeof(int));
        dataTable.Columns.Add("molotovs_thrown", typeof(int));
        dataTable.Columns.Add("he_grenades_thrown", typeof(int));
        dataTable.Columns.Add("decoys_thrown", typeof(int));
        dataTable.Columns.Add("players_blinded", typeof(int));
        dataTable.Columns.Add("times_blinded", typeof(int));
        dataTable.Columns.Add("flash_assists", typeof(int));
        dataTable.Columns.Add("total_blind_time", typeof(decimal));
        dataTable.Columns.Add("total_blind_time_inflicted", typeof(decimal));
        dataTable.Columns.Add("utility_damage_dealt", typeof(int));
        dataTable.Columns.Add("utility_damage_taken", typeof(int));
        dataTable.Columns.Add("bomb_plants", typeof(int));
        dataTable.Columns.Add("bomb_defuses", typeof(int));
        dataTable.Columns.Add("bomb_explosions", typeof(int));
        dataTable.Columns.Add("bomb_plant_attempts", typeof(int));
        dataTable.Columns.Add("bomb_plant_aborts", typeof(int));
        dataTable.Columns.Add("bomb_defuse_attempts", typeof(int));
        dataTable.Columns.Add("bomb_defuse_aborts", typeof(int));
        dataTable.Columns.Add("bomb_defuse_with_kit", typeof(int));
        dataTable.Columns.Add("bomb_defuse_without_kit", typeof(int));
        dataTable.Columns.Add("bomb_drops", typeof(int));
        dataTable.Columns.Add("bomb_pickups", typeof(int));
        dataTable.Columns.Add("defuser_pickups", typeof(int));
        dataTable.Columns.Add("clutch_defuses", typeof(int));
        dataTable.Columns.Add("total_plant_time", typeof(int));
        dataTable.Columns.Add("total_defuse_time", typeof(int));
        dataTable.Columns.Add("bomb_kills", typeof(int));
        dataTable.Columns.Add("bomb_deaths", typeof(int));
        dataTable.Columns.Add("jumps", typeof(int));
        dataTable.Columns.Add("money_spent", typeof(int));
        dataTable.Columns.Add("equipment_value", typeof(int));
        dataTable.Columns.Add("items_purchased", typeof(int));
        dataTable.Columns.Add("items_picked_up", typeof(int));
        dataTable.Columns.Add("round_start_money", typeof(int));
        dataTable.Columns.Add("round_end_money", typeof(int));
        dataTable.Columns.Add("equipment_value_start", typeof(int));
        dataTable.Columns.Add("equipment_value_end", typeof(int));
        dataTable.Columns.Add("enemies_flashed", typeof(int));
        dataTable.Columns.Add("teammates_flashed", typeof(int));
        dataTable.Columns.Add("effective_flashes", typeof(int));
        dataTable.Columns.Add("effective_he_grenades", typeof(int));
        dataTable.Columns.Add("effective_molotovs", typeof(int));
        dataTable.Columns.Add("flash_waste", typeof(int));
        dataTable.Columns.Add("multi_kill_nades", typeof(int));
        dataTable.Columns.Add("nade_kills", typeof(int));
        dataTable.Columns.Add("entry_kills", typeof(int));
        dataTable.Columns.Add("entry_deaths", typeof(int));
        dataTable.Columns.Add("entry_kill_attempts", typeof(int));
        dataTable.Columns.Add("entry_kill_attempt_wins", typeof(int));
        dataTable.Columns.Add("trade_kills", typeof(int));
        dataTable.Columns.Add("traded_deaths", typeof(int));
        dataTable.Columns.Add("high_impact_kills", typeof(int));
        dataTable.Columns.Add("low_impact_kills", typeof(int));
        dataTable.Columns.Add("multi_kills", typeof(int));
        dataTable.Columns.Add("multi_kill_2", typeof(int));
        dataTable.Columns.Add("multi_kill_3", typeof(int));
        dataTable.Columns.Add("multi_kill_4", typeof(int));
        dataTable.Columns.Add("multi_kill_5", typeof(int));
        dataTable.Columns.Add("opening_duels_won", typeof(int));
        dataTable.Columns.Add("opening_duels_lost", typeof(int));
        dataTable.Columns.Add("revenges", typeof(int));
        dataTable.Columns.Add("clutches_won", typeof(int));
        dataTable.Columns.Add("clutches_lost", typeof(int));
        dataTable.Columns.Add("clutch_kills", typeof(int));
        dataTable.Columns.Add("clutch_points", typeof(decimal));
        dataTable.Columns.Add("mvps_eliminations", typeof(int));
        dataTable.Columns.Add("mvps_bomb", typeof(int));
        dataTable.Columns.Add("mvps_hostage", typeof(int));
        dataTable.Columns.Add("headshots_hit", typeof(int));
        dataTable.Columns.Add("chest_hits", typeof(int));
        dataTable.Columns.Add("stomach_hits", typeof(int));
        dataTable.Columns.Add("arm_hits", typeof(int));
        dataTable.Columns.Add("leg_hits", typeof(int));
        dataTable.Columns.Add("kd_ratio", typeof(decimal));
        dataTable.Columns.Add("headshot_percentage", typeof(decimal));
        dataTable.Columns.Add("accuracy_percentage", typeof(decimal));
        dataTable.Columns.Add("kast_percentage", typeof(decimal));
        dataTable.Columns.Add("average_damage_per_round", typeof(decimal));
        dataTable.Columns.Add("hltv_rating", typeof(decimal));
        dataTable.Columns.Add("impact_rating", typeof(decimal));
        dataTable.Columns.Add("survival_rating", typeof(decimal));
        dataTable.Columns.Add("utility_score", typeof(decimal));
        dataTable.Columns.Add("noscope_kills", typeof(int));
        dataTable.Columns.Add("thru_smoke_kills", typeof(int));
        dataTable.Columns.Add("attacker_blind_kills", typeof(int));
        dataTable.Columns.Add("flash_assisted_kills", typeof(int));
        dataTable.Columns.Add("wallbang_kills", typeof(int));
        dataTable.Columns.Add("pings", typeof(int));
        dataTable.Columns.Add("footsteps", typeof(int));
        dataTable.Columns.Add("round_swing", typeof(decimal));
        dataTable.Columns.Add("team_kills", typeof(int));
        dataTable.Columns.Add("suicides", typeof(int));
        dataTable.Columns.Add("clutch_1v1_attempts", typeof(int));
        dataTable.Columns.Add("clutch_1v1_wins", typeof(int));
        dataTable.Columns.Add("clutch_1v2_attempts", typeof(int));
        dataTable.Columns.Add("clutch_1v2_wins", typeof(int));
        dataTable.Columns.Add("clutch_1v3_attempts", typeof(int));
        dataTable.Columns.Add("clutch_1v3_wins", typeof(int));
        dataTable.Columns.Add("clutch_1v4_attempts", typeof(int));
        dataTable.Columns.Add("clutch_1v4_wins", typeof(int));
        dataTable.Columns.Add("clutch_1v5_attempts", typeof(int));
        dataTable.Columns.Add("clutch_1v5_wins", typeof(int));
        dataTable.Columns.Add("kill_reward", typeof(int));
        dataTable.Columns.Add("cash_earned", typeof(int));
        dataTable.Columns.Add("money_saved", typeof(int));
        dataTable.Columns.Add("live_time", typeof(int));
        dataTable.Columns.Add("team_damage", typeof(int));
        dataTable.Columns.Add("multi_kill_1", typeof(int));
        dataTable.Columns.Add("ct_kills", typeof(int));
        dataTable.Columns.Add("t_kills", typeof(int));
        dataTable.Columns.Add("ct_deaths", typeof(int));
        dataTable.Columns.Add("t_deaths", typeof(int));
        dataTable.Columns.Add("ct_damage", typeof(int));
        dataTable.Columns.Add("t_damage", typeof(int));
        dataTable.Columns.Add("ct_first_kills", typeof(int));
        dataTable.Columns.Add("t_first_kills", typeof(int));
        dataTable.Columns.Add("ct_first_deaths", typeof(int));
        dataTable.Columns.Add("t_first_deaths", typeof(int));
        dataTable.Columns.Add("rounds_survived", typeof(int));
        dataTable.Columns.Add("rounds_traded", typeof(int));
        dataTable.Columns.Add("ct_rounds_won", typeof(int));
        dataTable.Columns.Add("t_rounds_won", typeof(int));
        dataTable.Columns.Add("pistol_rounds_played", typeof(int));
        dataTable.Columns.Add("pistol_rounds_won", typeof(int));
        dataTable.Columns.Add("pistol_round_kills", typeof(int));
        dataTable.Columns.Add("kill_distance_total", typeof(decimal));
        dataTable.Columns.Add("longest_kill", typeof(decimal));

        foreach (var s in snapshotList)
        {
            dataTable.Rows.Add(
                s.SteamId, s.Name, s.Kills, s.Deaths, s.Assists, s.Headshots, s.DamageDealt, s.DamageTaken, s.DamageArmor,
                s.ShotsFired, s.ShotsHit, s.Mvps, s.Score, s.RoundsPlayed, s.RoundsWon, s.KASTRounds, s.TotalSpawns, s.PlaytimeSeconds,
                s.CtRounds, s.TRounds, s.GrenadesThrown, s.FlashesThrown, s.SmokesThrown, s.MolotovsThrown,
                s.HeGrenadesThrown, s.DecoysThrown, s.PlayersBlinded, s.TimesBlinded,
                s.FlashAssists, s.TotalBlindTime, s.TotalBlindTimeInflicted, s.UtilityDamageDealt, s.UtilityDamageTaken,
                s.BombPlants, s.BombDefuses, s.BombExplosions, s.BombPlantAttempts, s.BombPlantAborts, s.BombDefuseAttempts,
                s.BombDefuseAborts, s.BombDefuseWithKit, s.BombDefuseWithoutKit, s.BombDrops, s.BombPickups,
                s.DefuserPickups, s.ClutchDefuses, s.TotalPlantTime, s.TotalDefuseTime,
                s.BombKills, s.BombDeaths, s.Jumps,
                s.MoneySpent, s.EquipmentValue, s.ItemsPurchased, s.ItemsPickedUp,
                s.RoundStartMoney, s.RoundEndMoney, s.EquipmentValueStart, s.EquipmentValueEnd,
                s.EnemiesFlashed, s.TeammatesFlashed, s.EffectiveFlashes, s.EffectiveHEGrenades,
                s.EffectiveMolotovs, s.FlashWaste, s.MultiKillNades, s.NadeKills,
                s.EntryKills, s.EntryDeaths, s.EntryKillAttempts, s.EntryKillAttemptWins,
                s.TradeKills, s.TradedDeaths, s.HighImpactKills, s.LowImpactKills, s.MultiKills,
                s.MultiKill2, s.MultiKill3, s.MultiKill4, s.MultiKill5, s.OpeningDuelsWon, s.OpeningDuelsLost,
                s.Revenges, s.ClutchesWon, s.ClutchesLost, s.ClutchKills, s.ClutchPoints, s.MvpsEliminations, s.MvpsBomb, s.MvpsHostage,
                s.HeadshotsHit, s.ChestHits, s.StomachHits, s.ArmHits, s.LegHits,
                s.KDRatio, s.HeadshotPercentage, s.AccuracyPercentage, s.KASTPercentage,
                s.AverageDamagePerRound, s.HLTVRating, s.ImpactRating, s.SurvivalRating, s.UtilityScore,
                s.NoscopeKills, s.ThruSmokeKills, s.AttackerBlindKills, s.FlashAssistedKills, s.WallbangKills,
                s.Pings, s.Footsteps, s.RoundSwing,
                s.TeamKills, s.Suicides,
                s.Clutch1v1Attempts, s.Clutch1v1Wins, s.Clutch1v2Attempts, s.Clutch1v2Wins, s.Clutch1v3Attempts, s.Clutch1v3Wins,
                s.Clutch1v4Attempts, s.Clutch1v4Wins, s.Clutch1v5Attempts, s.Clutch1v5Wins,
                s.KillReward, s.CashEarned, s.MoneySaved, s.LiveTime,
                s.TeamDamage, s.MultiKill1, s.CtKills, s.TKills, s.CtDeaths, s.TDeaths, s.CtDamage, s.TDamage,
                s.CtFirstKills, s.TFirstKills, s.CtFirstDeaths, s.TFirstDeaths,
                s.RoundsSurvived, s.RoundsTraded, s.CtRoundsWon, s.TRoundsWon,
                s.PistolRoundsPlayed, s.PistolRoundsWon, s.PistolRoundKills, s.KillDistanceTotal, s.LongestKill
            );
        }

        await ExecuteAsync(connection, "CREATE TEMPORARY TABLE temp_player_stats LIKE player_stats;", cancellationToken).ConfigureAwait(false);

        var bulkCopy = new MySqlBulkCopy(connection)
        {
            DestinationTableName = "temp_player_stats"
        };
        // Map by destination column name so DataTable column order can never silently misalign the copy.
        AddColumnMappingsByName(bulkCopy, dataTable);

        EnsureBulkCopySucceeded(await bulkCopy.WriteToServerAsync(dataTable, cancellationToken).ConfigureAwait(false));

        // Parent rows must exist before the FK-constrained merge into player_stats.
        await ExecuteAsync(connection,
            "INSERT INTO players (steam_id, name) SELECT steam_id, name FROM temp_player_stats AS src " +
            "ON DUPLICATE KEY UPDATE name = src.name, last_seen = CURRENT_TIMESTAMP;", cancellationToken).ConfigureAwait(false);

        const string mergeSql = """
            INSERT INTO player_stats
            SELECT * FROM temp_player_stats AS src
            ON DUPLICATE KEY UPDATE
                name = src.name, kills = src.kills, deaths = src.deaths, assists = src.assists,
                headshots = src.headshots, damage_dealt = src.damage_dealt, damage_taken = src.damage_taken,
                damage_armor = src.damage_armor, shots_fired = src.shots_fired, shots_hit = src.shots_hit,
                mvps = src.mvps, score = src.score, rounds_played = src.rounds_played,
                rounds_won = src.rounds_won, kast_rounds = src.kast_rounds,
                total_spawns = src.total_spawns, playtime_seconds = src.playtime_seconds,
                ct_rounds = src.ct_rounds, t_rounds = src.t_rounds, 
                grenades_thrown = src.grenades_thrown,
                flashes_thrown = src.flashes_thrown, smokes_thrown = src.smokes_thrown, molotovs_thrown = src.molotovs_thrown,
                he_grenades_thrown = src.he_grenades_thrown, decoys_thrown = src.decoys_thrown,
                players_blinded = src.players_blinded,
                times_blinded = src.times_blinded, flash_assists = src.flash_assists,
                total_blind_time = src.total_blind_time, total_blind_time_inflicted = src.total_blind_time_inflicted,
                utility_damage_dealt = src.utility_damage_dealt, utility_damage_taken = src.utility_damage_taken,
                bomb_plants = src.bomb_plants, bomb_defuses = src.bomb_defuses, bomb_explosions = src.bomb_explosions,
                bomb_plant_attempts = src.bomb_plant_attempts, bomb_plant_aborts = src.bomb_plant_aborts,
                bomb_defuse_attempts = src.bomb_defuse_attempts, bomb_defuse_aborts = src.bomb_defuse_aborts,
                bomb_defuse_with_kit = src.bomb_defuse_with_kit, bomb_defuse_without_kit = src.bomb_defuse_without_kit,
                bomb_drops = src.bomb_drops, bomb_pickups = src.bomb_pickups,
                defuser_pickups = src.defuser_pickups,
                clutch_defuses = src.clutch_defuses, total_plant_time = src.total_plant_time,
                total_defuse_time = src.total_defuse_time, bomb_kills = src.bomb_kills, bomb_deaths = src.bomb_deaths,
                jumps = src.jumps,
                money_spent = src.money_spent, equipment_value = src.equipment_value,
                items_purchased = src.items_purchased, items_picked_up = src.items_picked_up,
                round_start_money = src.round_start_money, round_end_money = src.round_end_money,
                equipment_value_start = src.equipment_value_start, equipment_value_end = src.equipment_value_end,
                enemies_flashed = src.enemies_flashed, teammates_flashed = src.teammates_flashed,
                effective_flashes = src.effective_flashes,
                effective_he_grenades = src.effective_he_grenades, effective_molotovs = src.effective_molotovs,
                flash_waste = src.flash_waste, multi_kill_nades = src.multi_kill_nades, nade_kills = src.nade_kills,
                entry_kills = src.entry_kills, entry_deaths = src.entry_deaths,
                entry_kill_attempts = src.entry_kill_attempts, entry_kill_attempt_wins = src.entry_kill_attempt_wins,
                trade_kills = src.trade_kills, traded_deaths = src.traded_deaths,
                high_impact_kills = src.high_impact_kills, low_impact_kills = src.low_impact_kills,
                multi_kills = src.multi_kills, multi_kill_2 = src.multi_kill_2, multi_kill_3 = src.multi_kill_3,
                multi_kill_4 = src.multi_kill_4, multi_kill_5 = src.multi_kill_5,
                opening_duels_won = src.opening_duels_won,
                opening_duels_lost = src.opening_duels_lost, revenges = src.revenges,
                clutches_won = src.clutches_won, clutches_lost = src.clutches_lost,
                clutch_kills = src.clutch_kills, clutch_points = src.clutch_points,
                mvps_eliminations = src.mvps_eliminations, mvps_bomb = src.mvps_bomb, mvps_hostage = src.mvps_hostage,
                headshots_hit = src.headshots_hit, chest_hits = src.chest_hits, stomach_hits = src.stomach_hits,
                arm_hits = src.arm_hits, leg_hits = src.leg_hits,
                kd_ratio = src.kd_ratio, headshot_percentage = src.headshot_percentage,
                accuracy_percentage = src.accuracy_percentage, kast_percentage = src.kast_percentage,
                average_damage_per_round = src.average_damage_per_round, hltv_rating = src.hltv_rating,
                impact_rating = src.impact_rating, survival_rating = src.survival_rating,
                utility_score = src.utility_score,
                noscope_kills = src.noscope_kills, thru_smoke_kills = src.thru_smoke_kills,
                attacker_blind_kills = src.attacker_blind_kills, flash_assisted_kills = src.flash_assisted_kills,
                wallbang_kills = src.wallbang_kills, pings = src.pings, footsteps = src.footsteps,
                round_swing = src.round_swing,
                team_kills = src.team_kills, suicides = src.suicides,
                clutch_1v1_attempts = src.clutch_1v1_attempts, clutch_1v1_wins = src.clutch_1v1_wins,
                clutch_1v2_attempts = src.clutch_1v2_attempts, clutch_1v2_wins = src.clutch_1v2_wins,
                clutch_1v3_attempts = src.clutch_1v3_attempts, clutch_1v3_wins = src.clutch_1v3_wins,
                clutch_1v4_attempts = src.clutch_1v4_attempts, clutch_1v4_wins = src.clutch_1v4_wins,
                clutch_1v5_attempts = src.clutch_1v5_attempts, clutch_1v5_wins = src.clutch_1v5_wins,
                kill_reward = src.kill_reward, cash_earned = src.cash_earned, money_saved = src.money_saved,
                live_time = src.live_time,
                team_damage = src.team_damage, multi_kill_1 = src.multi_kill_1,
                ct_kills = src.ct_kills, t_kills = src.t_kills, ct_deaths = src.ct_deaths, t_deaths = src.t_deaths,
                ct_damage = src.ct_damage, t_damage = src.t_damage,
                ct_first_kills = src.ct_first_kills, t_first_kills = src.t_first_kills,
                ct_first_deaths = src.ct_first_deaths, t_first_deaths = src.t_first_deaths,
                rounds_survived = src.rounds_survived, rounds_traded = src.rounds_traded,
                ct_rounds_won = src.ct_rounds_won, t_rounds_won = src.t_rounds_won,
                pistol_rounds_played = src.pistol_rounds_played, pistol_rounds_won = src.pistol_rounds_won,
                pistol_round_kills = src.pistol_round_kills,
                kill_distance_total = src.kill_distance_total, longest_kill = src.longest_kill;
            """;

        await ExecuteAsync(connection, mergeSql, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Successfully completed high-performance bulk update for {Count} players using MySqlBulkCopy", snapshotList.Count);
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
