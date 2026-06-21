using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    Task UpsertPlayerAsync(PlayerSnapshot snapshot, CancellationToken cancellationToken);
    Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    
    // Match Lifecycle
    Task StartMatchAsync(string mapName, string? matchUuid, string? seriesUuid, CancellationToken ct);
    Task EndMatchAsync(string matchUuid, CancellationToken ct);
    Task StartRoundAsync(string matchUuid, int roundNumber, CancellationToken ct);
    Task EndRoundAsync(string matchUuid, int roundNumber, int winnerTeam, int winReason, CancellationToken ct);
}

public sealed class StatsRepository : IStatsRepository
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<StatsRepository> _logger;

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
        var snapshotList = snapshots.ToList();
        if (snapshotList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("UpsertPlayersAsync");
            Instrumentation.DbOperationsCounter.Add(1, new KeyValuePair<string, object?>("operation", "upsert_players"));
            
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

    public Task UpsertPlayerAsync(PlayerSnapshot snapshot, CancellationToken cancellationToken) =>
        UpsertPlayersAsync([snapshot], cancellationToken);

    public async Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var snapshotList = snapshots.Where(s => !string.IsNullOrEmpty(s.MatchUuid)).ToList();
        if (snapshotList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("UpsertMatchSummariesAsync");
            Instrumentation.DbOperationsCounter.Add(1, new KeyValuePair<string, object?>("operation", "upsert_match_summaries"));

            await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            
            var dataTable = new DataTable();
            dataTable.Columns.Add("match_uuid", typeof(string)); // Using UUID for intermediate step
            dataTable.Columns.Add("steam_id", typeof(ulong));
            dataTable.Columns.Add("kills", typeof(int));
            dataTable.Columns.Add("deaths", typeof(int));
            dataTable.Columns.Add("assists", typeof(int));
            dataTable.Columns.Add("headshots", typeof(int));
            dataTable.Columns.Add("damage_dealt", typeof(int));
            dataTable.Columns.Add("mvps", typeof(int));
            dataTable.Columns.Add("score", typeof(int));
            dataTable.Columns.Add("rating2", typeof(decimal));
            dataTable.Columns.Add("adr", typeof(decimal));
            dataTable.Columns.Add("kast", typeof(decimal));
            dataTable.Columns.Add("entry_kills", typeof(int));
            dataTable.Columns.Add("entry_deaths", typeof(int));
            dataTable.Columns.Add("entry_kill_attempts", typeof(int));
            dataTable.Columns.Add("entry_kill_attempt_wins", typeof(int));
            dataTable.Columns.Add("idempotency_key", typeof(string));
            dataTable.Columns.Add("created_at", typeof(DateTime));
            dataTable.Columns.Add("retry_count", typeof(int));

            var now = DateTime.UtcNow;
            foreach (var s in snapshotList)
            {
                dataTable.Rows.Add(
                    s.MatchUuid, s.SteamId, s.Kills, s.Deaths, s.Assists, s.Headshots, 
                    s.DamageDealt, s.Mvps, s.Score, s.HLTVRating, 
                    s.AverageDamagePerRound, s.KASTPercentage,
                    s.EntryKills, s.EntryDeaths, s.EntryKillAttempts, s.EntryKillAttemptWins,
                    $"{s.MatchUuid}_{s.RoundNumber}_{s.SteamId}_Summary",
                    now, 0
                );
            }

            await connection.ExecuteAsync("CREATE TEMPORARY TABLE temp_match_player_stats_uuid (match_uuid VARCHAR(64), steam_id BIGINT UNSIGNED, kills INT, deaths INT, assists INT, headshots INT, damage_dealt INT, mvps INT, score INT, rating2 DECIMAL(5,3), adr DECIMAL(10,2), kast DECIMAL(5,2), entry_kills INT, entry_deaths INT, entry_kill_attempts INT, entry_kill_attempt_wins INT, idempotency_key VARCHAR(255), created_at DATETIME, retry_count INT);").ConfigureAwait(false);
            
            var bulkCopy = new MySqlBulkCopy(connection)
            {
                DestinationTableName = "temp_match_player_stats_uuid"
            };
            AddColumnMappingsByName(bulkCopy, dataTable);

            await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false);

            await connection.ExecuteAsync(
                "INSERT IGNORE INTO players (steam_id) SELECT DISTINCT steam_id FROM temp_match_player_stats_uuid;").ConfigureAwait(false);

            const string mergeSql = """
                INSERT INTO match_player_stats
                (match_id, steam_id, kills, deaths, assists, headshots, damage_dealt, mvps, score, rating2, adr, kast, entry_kills, entry_deaths, entry_kill_attempts, entry_kill_attempt_wins, idempotency_key, created_at, retry_count)
                SELECT m.id, t.steam_id, t.kills, t.deaths, t.assists, t.headshots, t.damage_dealt, t.mvps, t.score, t.rating2, t.adr, t.kast, t.entry_kills, t.entry_deaths, t.entry_kill_attempts, t.entry_kill_attempt_wins, t.idempotency_key, t.created_at, t.retry_count
                FROM temp_match_player_stats_uuid t
                JOIN matches m ON t.match_uuid = m.match_uuid
                ON DUPLICATE KEY UPDATE
                    kills = t.kills, deaths = t.deaths, assists = t.assists,
                    headshots = t.headshots, damage_dealt = t.damage_dealt,
                    mvps = t.mvps, score = t.score, rating2 = t.rating2,
                    adr = t.adr, kast = t.kast,
                    entry_kills = t.entry_kills, entry_deaths = t.entry_deaths,
                    entry_kill_attempts = t.entry_kill_attempts, entry_kill_attempt_wins = t.entry_kill_attempt_wins,
                    retry_count = match_player_stats.retry_count + 1;
                """;

            await connection.ExecuteAsync(mergeSql).ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TEMPORARY TABLE temp_match_player_stats_uuid;").ConfigureAwait(false);
            
            _logger.LogInformation("Bulk upserted {Count} match summaries using MatchUuid resolution", snapshotList.Count);
        }, cancellationToken);
    }

    public async Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var snapshotList = snapshots.Where(s => !string.IsNullOrEmpty(s.MatchUuid)).ToList();
        if (snapshotList.Count == 0) return;

        var weaponData = snapshotList.SelectMany(s => s.WeaponKills.Select(kvp => new
        {
            s.MatchUuid,
            s.SteamId,
            Weapon = kvp.Key,
            Kills = kvp.Value,
            Shots = s.WeaponShots.GetValueOrDefault(kvp.Key, 0),
            Hits = s.WeaponHits.GetValueOrDefault(kvp.Key, 0),
            IdempotencyKey = $"{s.MatchUuid}_{s.RoundNumber}_{s.SteamId}_Weapon_{kvp.Key}"
        })).ToList();

        if (weaponData.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("UpsertMatchWeaponStatsAsync");
            Instrumentation.DbOperationsCounter.Add(1, new KeyValuePair<string, object?>("operation", "upsert_match_weapons"));

            await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            
            var dataTable = new DataTable();
            dataTable.Columns.Add("match_uuid", typeof(string));
            dataTable.Columns.Add("steam_id", typeof(ulong));
            dataTable.Columns.Add("weapon_name", typeof(string));
            dataTable.Columns.Add("kills", typeof(int));
            dataTable.Columns.Add("shots", typeof(int));
            dataTable.Columns.Add("hits", typeof(int));
            dataTable.Columns.Add("idempotency_key", typeof(string));
            dataTable.Columns.Add("created_at", typeof(DateTime));
            dataTable.Columns.Add("retry_count", typeof(int));

            var now = DateTime.UtcNow;
            foreach (var w in weaponData)
            {
                dataTable.Rows.Add(w.MatchUuid, w.SteamId, w.Weapon, w.Kills, w.Shots, w.Hits, w.IdempotencyKey, now, 0);
            }

            await connection.ExecuteAsync("CREATE TEMPORARY TABLE temp_match_weapon_stats_uuid (match_uuid VARCHAR(64), steam_id BIGINT UNSIGNED, weapon_name VARCHAR(64), kills INT, shots INT, hits INT, idempotency_key VARCHAR(255), created_at DATETIME, retry_count INT);").ConfigureAwait(false);
            
            var bulkCopy = new MySqlBulkCopy(connection)
            {
                DestinationTableName = "temp_match_weapon_stats_uuid"
            };
            AddColumnMappingsByName(bulkCopy, dataTable);

            await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false);

            await connection.ExecuteAsync(
                "INSERT IGNORE INTO players (steam_id) SELECT DISTINCT steam_id FROM temp_match_weapon_stats_uuid;").ConfigureAwait(false);

            const string mergeSql = """
                INSERT INTO match_weapon_stats
                (match_id, steam_id, weapon_name, kills, shots, hits, idempotency_key, created_at, retry_count)
                SELECT m.id, t.steam_id, t.weapon_name, t.kills, t.shots, t.hits, t.idempotency_key, t.created_at, t.retry_count
                FROM temp_match_weapon_stats_uuid t
                JOIN matches m ON t.match_uuid = m.match_uuid
                ON DUPLICATE KEY UPDATE
                    kills = t.kills, shots = t.shots, hits = t.hits,
                    retry_count = match_weapon_stats.retry_count + 1;
                """;

            await connection.ExecuteAsync(mergeSql).ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TEMPORARY TABLE temp_match_weapon_stats_uuid;").ConfigureAwait(false);
            
            _logger.LogInformation("Bulk upserted {Count} weapon records using MatchUuid resolution", weaponData.Count);
        }, cancellationToken);
    }

    public async Task StartMatchAsync(string mapName, string? matchUuid, string? seriesUuid, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        const string sql = "INSERT INTO matches (match_uuid, series_uuid, map_name, status) VALUES (@MatchUuid, @SeriesUuid, @MapName, 'IN_PROGRESS') ON DUPLICATE KEY UPDATE status = 'IN_PROGRESS';";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { MatchUuid = matchUuid, SeriesUuid = seriesUuid, MapName = mapName }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task EndMatchAsync(string matchUuid, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        const string sql = "UPDATE matches SET end_time = CURRENT_TIMESTAMP, status = 'COMPLETED' WHERE match_uuid = @MatchUuid";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { MatchUuid = matchUuid }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task StartRoundAsync(string matchUuid, int roundNumber, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        const string sql = """
            INSERT INTO rounds (match_id, round_number) 
            SELECT id, @RoundNumber FROM matches WHERE match_uuid = @MatchUuid
            ON DUPLICATE KEY UPDATE start_time = CURRENT_TIMESTAMP;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new { MatchUuid = matchUuid, RoundNumber = roundNumber }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task EndRoundAsync(string matchUuid, int roundNumber, int winnerTeam, int winReason, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE rounds r
            JOIN matches m ON r.match_id = m.id
            SET r.end_time = CURRENT_TIMESTAMP, r.winner_team = @Winner, r.win_type = @WinType 
            WHERE m.match_uuid = @MatchUuid AND r.round_number = @RoundNumber;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new { MatchUuid = matchUuid, RoundNumber = roundNumber, Winner = winnerTeam, WinType = winReason }, cancellationToken: ct)).ConfigureAwait(false);
    }

    // MySqlBulkCopy maps DataTable columns to the destination by ordinal unless told otherwise.
    // Mapping each source column to the destination column of the same name removes any dependency
    // on the DataTable's column order matching the table definition.
    private static void AddColumnMappingsByName(MySqlBulkCopy bulkCopy, DataTable dataTable)
    {
        for (var i = 0; i < dataTable.Columns.Count; i++)
        {
            bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, dataTable.Columns[i].ColumnName));
        }
    }

    private async Task UpsertPlayersCoreAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("UpsertPlayersCoreAsync");
        var snapshotList = snapshots.ToList();
        activity?.SetTag("db.snapshot_count", snapshotList.Count);
        
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
        dataTable.Columns.Add("tactical_grenades_thrown", typeof(int));
        dataTable.Columns.Add("players_blinded", typeof(int));
        dataTable.Columns.Add("times_blinded", typeof(int));
        dataTable.Columns.Add("flash_assists", typeof(int));
        dataTable.Columns.Add("total_blind_time", typeof(decimal));
        dataTable.Columns.Add("total_blind_time_inflicted", typeof(decimal));
        dataTable.Columns.Add("utility_damage_dealt", typeof(int));
        dataTable.Columns.Add("utility_damage_taken", typeof(int));
        dataTable.Columns.Add("bomb_plants", typeof(int));
        dataTable.Columns.Add("bomb_defuses", typeof(int));
        dataTable.Columns.Add("bomb_plant_attempts", typeof(int));
        dataTable.Columns.Add("bomb_plant_aborts", typeof(int));
        dataTable.Columns.Add("bomb_defuse_attempts", typeof(int));
        dataTable.Columns.Add("bomb_defuse_aborts", typeof(int));
        dataTable.Columns.Add("bomb_defuse_with_kit", typeof(int));
        dataTable.Columns.Add("bomb_defuse_without_kit", typeof(int));
        dataTable.Columns.Add("bomb_drops", typeof(int));
        dataTable.Columns.Add("bomb_pickups", typeof(int));
        dataTable.Columns.Add("defuser_drops", typeof(int));
        dataTable.Columns.Add("defuser_pickups", typeof(int));
        dataTable.Columns.Add("clutch_defuses", typeof(int));
        dataTable.Columns.Add("total_plant_time", typeof(int));
        dataTable.Columns.Add("total_defuse_time", typeof(int));
        dataTable.Columns.Add("bomb_kills", typeof(int));
        dataTable.Columns.Add("bomb_deaths", typeof(int));
        dataTable.Columns.Add("hostages_rescued", typeof(int));
        dataTable.Columns.Add("jumps", typeof(int));
        dataTable.Columns.Add("money_spent", typeof(int));
        dataTable.Columns.Add("equipment_value", typeof(int));
        dataTable.Columns.Add("items_purchased", typeof(int));
        dataTable.Columns.Add("items_picked_up", typeof(int));
        dataTable.Columns.Add("items_dropped", typeof(int));
        dataTable.Columns.Add("cash_earned", typeof(int));
        dataTable.Columns.Add("cash_spent", typeof(int));
        dataTable.Columns.Add("loss_bonus", typeof(int));
        dataTable.Columns.Add("round_start_money", typeof(int));
        dataTable.Columns.Add("round_end_money", typeof(int));
        dataTable.Columns.Add("equipment_value_start", typeof(int));
        dataTable.Columns.Add("equipment_value_end", typeof(int));
        dataTable.Columns.Add("enemies_flashed", typeof(int));
        dataTable.Columns.Add("teammates_flashed", typeof(int));
        dataTable.Columns.Add("effective_flashes", typeof(int));
        dataTable.Columns.Add("effective_smokes", typeof(int));
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
        dataTable.Columns.Add("trade_opportunities", typeof(int));
        dataTable.Columns.Add("trade_windows_missed", typeof(int));
        dataTable.Columns.Add("multi_kills", typeof(int));
        dataTable.Columns.Add("opening_duels_won", typeof(int));
        dataTable.Columns.Add("opening_duels_lost", typeof(int));
        dataTable.Columns.Add("revenges", typeof(int));
        dataTable.Columns.Add("clutches_won", typeof(int));
        dataTable.Columns.Add("clutches_lost", typeof(int));
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

        foreach (var s in snapshotList)
        {
            dataTable.Rows.Add(
                s.SteamId, s.Name, s.Kills, s.Deaths, s.Assists, s.Headshots, s.DamageDealt, s.DamageTaken, s.DamageArmor,
                s.ShotsFired, s.ShotsHit, s.Mvps, s.Score, s.RoundsPlayed, s.RoundsWon, s.TotalSpawns, s.PlaytimeSeconds,
                s.CtRounds, s.TRounds, s.GrenadesThrown, s.FlashesThrown, s.SmokesThrown, s.MolotovsThrown,
                s.HeGrenadesThrown, s.DecoysThrown, s.TacticalGrenadesThrown, s.PlayersBlinded, s.TimesBlinded,
                s.FlashAssists, s.TotalBlindTime, s.TotalBlindTimeInflicted, s.UtilityDamageDealt, s.UtilityDamageTaken,
                s.BombPlants, s.BombDefuses, s.BombPlantAttempts, s.BombPlantAborts, s.BombDefuseAttempts,
                s.BombDefuseAborts, s.BombDefuseWithKit, s.BombDefuseWithoutKit, s.BombDrops, s.BombPickups,
                s.DefuserDrops, s.DefuserPickups, s.ClutchDefuses, s.TotalPlantTime, s.TotalDefuseTime,
                s.BombKills, s.BombDeaths, s.HostagesRescued, s.Jumps,
                s.MoneySpent, s.EquipmentValue, s.ItemsPurchased, s.ItemsPickedUp, s.ItemsDropped, s.CashEarned, s.CashSpent,
                s.LossBonus, s.RoundStartMoney, s.RoundEndMoney, s.EquipmentValueStart, s.EquipmentValueEnd,
                s.EnemiesFlashed, s.TeammatesFlashed, s.EffectiveFlashes, s.EffectiveSmokes, s.EffectiveHEGrenades,
                s.EffectiveMolotovs, s.FlashWaste, s.MultiKillNades, s.NadeKills,
                s.EntryKills, s.EntryDeaths, s.EntryKillAttempts, s.EntryKillAttemptWins,
                s.TradeKills, s.TradedDeaths, s.HighImpactKills, s.LowImpactKills, s.TradeOpportunities, s.TradeWindowsMissed, s.MultiKills, s.OpeningDuelsWon, s.OpeningDuelsLost,
                s.Revenges, s.ClutchesWon, s.ClutchesLost, s.ClutchPoints, s.MvpsEliminations, s.MvpsBomb, s.MvpsHostage,
                s.HeadshotsHit, s.ChestHits, s.StomachHits, s.ArmHits, s.LegHits,
                s.KDRatio, s.HeadshotPercentage, s.AccuracyPercentage, s.KASTPercentage,
                s.AverageDamagePerRound, s.HLTVRating, s.ImpactRating, s.SurvivalRating, s.UtilityScore,
                s.NoscopeKills, s.ThruSmokeKills, s.AttackerBlindKills, s.FlashAssistedKills, s.WallbangKills,
                s.Pings, s.Footsteps
            );
        }

        await connection.ExecuteAsync("CREATE TEMPORARY TABLE temp_player_stats LIKE player_stats;").ConfigureAwait(false);

        var bulkCopy = new MySqlBulkCopy(connection)
        {
            DestinationTableName = "temp_player_stats"
        };
        // Map by destination column name so DataTable column order can never silently misalign the copy.
        AddColumnMappingsByName(bulkCopy, dataTable);

        await bulkCopy.WriteToServerAsync(dataTable, cancellationToken).ConfigureAwait(false);

        // Parent rows must exist before the FK-constrained merge into player_stats.
        await connection.ExecuteAsync(
            "INSERT INTO players (steam_id, name) SELECT steam_id, name FROM temp_player_stats AS src " +
            "ON DUPLICATE KEY UPDATE name = src.name, last_seen = CURRENT_TIMESTAMP;").ConfigureAwait(false);

        const string mergeSql = """
            INSERT INTO player_stats
            SELECT * FROM temp_player_stats AS src
            ON DUPLICATE KEY UPDATE
                name = src.name, kills = src.kills, deaths = src.deaths, assists = src.assists,
                headshots = src.headshots, damage_dealt = src.damage_dealt, damage_taken = src.damage_taken,
                damage_armor = src.damage_armor, shots_fired = src.shots_fired, shots_hit = src.shots_hit,
                mvps = src.mvps, score = src.score, rounds_played = src.rounds_played,
                rounds_won = src.rounds_won, total_spawns = src.total_spawns, playtime_seconds = src.playtime_seconds,
                ct_rounds = src.ct_rounds, t_rounds = src.t_rounds, 
                grenades_thrown = src.grenades_thrown,
                flashes_thrown = src.flashes_thrown, smokes_thrown = src.smokes_thrown, molotovs_thrown = src.molotovs_thrown,
                he_grenades_thrown = src.he_grenades_thrown, decoys_thrown = src.decoys_thrown,
                tactical_grenades_thrown = src.tactical_grenades_thrown, players_blinded = src.players_blinded,
                times_blinded = src.times_blinded, flash_assists = src.flash_assists,
                total_blind_time = src.total_blind_time, total_blind_time_inflicted = src.total_blind_time_inflicted,
                utility_damage_dealt = src.utility_damage_dealt, utility_damage_taken = src.utility_damage_taken,
                bomb_plants = src.bomb_plants, bomb_defuses = src.bomb_defuses,
                bomb_plant_attempts = src.bomb_plant_attempts, bomb_plant_aborts = src.bomb_plant_aborts,
                bomb_defuse_attempts = src.bomb_defuse_attempts, bomb_defuse_aborts = src.bomb_defuse_aborts,
                bomb_defuse_with_kit = src.bomb_defuse_with_kit, bomb_defuse_without_kit = src.bomb_defuse_without_kit,
                bomb_drops = src.bomb_drops, bomb_pickups = src.bomb_pickups,
                defuser_drops = src.defuser_drops, defuser_pickups = src.defuser_pickups,
                clutch_defuses = src.clutch_defuses, total_plant_time = src.total_plant_time,
                total_defuse_time = src.total_defuse_time, bomb_kills = src.bomb_kills, bomb_deaths = src.bomb_deaths,
                hostages_rescued = src.hostages_rescued, jumps = src.jumps,
                money_spent = src.money_spent, equipment_value = src.equipment_value,
                items_purchased = src.items_purchased, items_picked_up = src.items_picked_up,
                items_dropped = src.items_dropped, cash_earned = src.cash_earned, cash_spent = src.cash_spent,
                loss_bonus = src.loss_bonus, round_start_money = src.round_start_money, round_end_money = src.round_end_money,
                equipment_value_start = src.equipment_value_start, equipment_value_end = src.equipment_value_end,
                enemies_flashed = src.enemies_flashed, teammates_flashed = src.teammates_flashed,
                effective_flashes = src.effective_flashes, effective_smokes = src.effective_smokes,
                effective_he_grenades = src.effective_he_grenades, effective_molotovs = src.effective_molotovs,
                flash_waste = src.flash_waste, multi_kill_nades = src.multi_kill_nades, nade_kills = src.nade_kills,
                entry_kills = src.entry_kills, entry_deaths = src.entry_deaths,
                entry_kill_attempts = src.entry_kill_attempts, entry_kill_attempt_wins = src.entry_kill_attempt_wins,
                trade_kills = src.trade_kills, traded_deaths = src.traded_deaths,
                high_impact_kills = src.high_impact_kills, low_impact_kills = src.low_impact_kills, trade_opportunities = src.trade_opportunities,
                trade_windows_missed = src.trade_windows_missed, multi_kills = src.multi_kills, opening_duels_won = src.opening_duels_won,
                opening_duels_lost = src.opening_duels_lost, revenges = src.revenges,
                clutches_won = src.clutches_won, clutches_lost = src.clutches_lost, clutch_points = src.clutch_points,
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
                wallbang_kills = src.wallbang_kills, pings = src.pings, footsteps = src.footsteps;
            """;

        await connection.ExecuteAsync(mergeSql).ConfigureAwait(false);
        await connection.ExecuteAsync("DROP TEMPORARY TABLE temp_player_stats;").ConfigureAwait(false);

        _logger.LogInformation("Successfully completed high-performance bulk update for {Count} players using MySqlBulkCopy", snapshotList.Count);
    }
}
