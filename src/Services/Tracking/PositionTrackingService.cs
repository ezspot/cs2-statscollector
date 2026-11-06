using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using statsCollector.Infrastructure.Database;
using Polly;
using Polly.Registry;

namespace statsCollector.Services;

public interface IPositionTrackingService
{
    Task BulkTrackKillPositionsAsync(IEnumerable<KillPositionEvent> events, CancellationToken cancellationToken = default);
    Task BulkTrackDeathPositionsAsync(IEnumerable<DeathPositionEvent> events, CancellationToken cancellationToken = default);
    Task BulkTrackUtilityPositionsAsync(IEnumerable<UtilityPositionEvent> events, CancellationToken cancellationToken = default);
    Task VoidRoundPositionsAsync(string matchUuid, int fromRound, CancellationToken cancellationToken = default);
}

public sealed class PositionTrackingService : IPositionTrackingService
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _resiliencePipeline;

    public PositionTrackingService(
        IConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _connectionFactory = connectionFactory;
        _resiliencePipeline = pipelineProvider.GetPipeline("database");
    }

    public async Task BulkTrackKillPositionsAsync(IEnumerable<KillPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events as IReadOnlyList<KillPositionEvent> ?? events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

            var dataTable = new DataTable();
            dataTable.Columns.Add("match_uuid", typeof(string));
            dataTable.Columns.Add("killer_steam_id", typeof(ulong));
            dataTable.Columns.Add("victim_steam_id", typeof(ulong));
            dataTable.Columns.Add("killer_x", typeof(float));
            dataTable.Columns.Add("killer_y", typeof(float));
            dataTable.Columns.Add("killer_z", typeof(float));
            dataTable.Columns.Add("victim_x", typeof(float));
            dataTable.Columns.Add("victim_y", typeof(float));
            dataTable.Columns.Add("victim_z", typeof(float));
            dataTable.Columns.Add("weapon_used", typeof(string));
            dataTable.Columns.Add("is_headshot", typeof(bool));
            dataTable.Columns.Add("is_wallbang", typeof(bool));
            dataTable.Columns.Add("distance", typeof(float));
            dataTable.Columns.Add("killer_team", typeof(int));
            dataTable.Columns.Add("victim_team", typeof(int));
            dataTable.Columns.Add("map_name", typeof(string));
            dataTable.Columns.Add("round_number", typeof(int));
            dataTable.Columns.Add("round_time_seconds", typeof(int));

            foreach (var e in eventList)
            {
                dataTable.Rows.Add(
                    e.MatchUuid, e.KillerSteamId, e.VictimSteamId,
                    e.KillerX, e.KillerY, e.KillerZ,
                    e.VictimX, e.VictimY, e.VictimZ,
                    e.Weapon, e.IsHeadshot, e.IsWallbang,
                    e.Distance, e.KillerTeam, e.VictimTeam,
                    e.MapName, e.RoundNumber, e.RoundTime
                );
            }

            // kill_positions stores POINT geometry, so the temp staging table holds raw x/y floats and the merge
            // builds POINTs. Staging tables have a surrogate key: sql_require_primary_key=ON rejects them without one.
            await ExecuteAsync(connection,
                "CREATE TEMPORARY TABLE temp_kill_positions_uuid (row_id INT AUTO_INCREMENT PRIMARY KEY, match_uuid VARCHAR(64), killer_steam_id BIGINT UNSIGNED, victim_steam_id BIGINT UNSIGNED, killer_x FLOAT, killer_y FLOAT, killer_z FLOAT, victim_x FLOAT, victim_y FLOAT, victim_z FLOAT, weapon_used VARCHAR(100), is_headshot TINYINT(1), is_wallbang TINYINT(1), distance FLOAT, killer_team INT, victim_team INT, map_name VARCHAR(100), round_number INT, round_time_seconds INT);",
                ct).ConfigureAwait(false);

            var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = "temp_kill_positions_uuid" };
            StatsRepository.AddColumnMappingsByName(bulkCopy, dataTable);
            StatsRepository.EnsureBulkCopySucceeded(await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false));

            const string mergeSql = """
                INSERT INTO kill_positions
                (match_id, killer_steam_id, victim_steam_id, killer_pos, killer_z,
                 victim_pos, victim_z, weapon_used, is_headshot, is_wallbang,
                 distance, killer_team, victim_team, map_name, round_number, round_time_seconds)
                SELECT m.id, t.killer_steam_id, t.victim_steam_id, POINT(t.killer_x, t.killer_y), t.killer_z,
                       POINT(t.victim_x, t.victim_y), t.victim_z, t.weapon_used, t.is_headshot, t.is_wallbang,
                       t.distance, t.killer_team, t.victim_team, t.map_name, t.round_number, t.round_time_seconds
                FROM temp_kill_positions_uuid t
                JOIN matches m ON t.match_uuid = m.match_uuid;
                """;

            // The match row normally comes from the stats channel; after an outage it can still be queued there.
            await ExecuteAsync(connection,
                "INSERT IGNORE INTO matches (match_uuid, map_name) SELECT DISTINCT match_uuid, map_name FROM temp_kill_positions_uuid;",
                ct).ConfigureAwait(false);
            // MySQL can't reference a TEMPORARY table twice in one statement (ERROR 1137), so one insert per side.
            await ExecuteAsync(connection,
                "INSERT IGNORE INTO players (steam_id) SELECT DISTINCT killer_steam_id FROM temp_kill_positions_uuid;",
                ct).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "INSERT IGNORE INTO players (steam_id) SELECT DISTINCT victim_steam_id FROM temp_kill_positions_uuid;",
                ct).ConfigureAwait(false);
            var inserted = await ExecuteAsync(connection, mergeSql, ct).ConfigureAwait(false);
            if (inserted != eventList.Count) throw new InvalidOperationException("Kill positions could not resolve their match UUID.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackDeathPositionsAsync(IEnumerable<DeathPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events as IReadOnlyList<DeathPositionEvent> ?? events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

            var dataTable = new DataTable();
            dataTable.Columns.Add("match_uuid", typeof(string));
            dataTable.Columns.Add("steam_id", typeof(ulong));
            dataTable.Columns.Add("x", typeof(float));
            dataTable.Columns.Add("y", typeof(float));
            dataTable.Columns.Add("z", typeof(float));
            dataTable.Columns.Add("cause_of_death", typeof(string));
            dataTable.Columns.Add("is_headshot", typeof(bool));
            dataTable.Columns.Add("team", typeof(int));
            dataTable.Columns.Add("map_name", typeof(string));
            dataTable.Columns.Add("round_number", typeof(int));
            dataTable.Columns.Add("round_time_seconds", typeof(int));

            foreach (var e in eventList)
            {
                dataTable.Rows.Add(
                    e.MatchUuid, e.SteamId, e.X, e.Y, e.Z,
                    e.CauseOfDeath, e.IsHeadshot, e.Team,
                    e.MapName, e.RoundNumber, e.RoundTime
                );
            }

            await ExecuteAsync(connection, "CREATE TEMPORARY TABLE temp_death_positions_uuid (row_id INT AUTO_INCREMENT PRIMARY KEY, match_uuid VARCHAR(64), steam_id BIGINT UNSIGNED, x FLOAT, y FLOAT, z FLOAT, cause_of_death VARCHAR(100), is_headshot TINYINT(1), team INT, map_name VARCHAR(100), round_number INT, round_time_seconds INT);", ct).ConfigureAwait(false);

            var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = "temp_death_positions_uuid" };
            StatsRepository.AddColumnMappingsByName(bulkCopy, dataTable);
            StatsRepository.EnsureBulkCopySucceeded(await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false));

            const string mergeSql = """
                INSERT INTO death_positions
                (match_id, steam_id, pos, z, cause_of_death, is_headshot, team, map_name, round_number, round_time_seconds)
                SELECT m.id, t.steam_id, POINT(t.x, t.y), t.z, t.cause_of_death, t.is_headshot, t.team, t.map_name, t.round_number, t.round_time_seconds
                FROM temp_death_positions_uuid t
                JOIN matches m ON t.match_uuid = m.match_uuid;
                """;

            await ExecuteAsync(connection, "INSERT IGNORE INTO matches (match_uuid, map_name) SELECT DISTINCT match_uuid, map_name FROM temp_death_positions_uuid;", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, "INSERT IGNORE INTO players (steam_id) SELECT DISTINCT steam_id FROM temp_death_positions_uuid;", ct).ConfigureAwait(false);
            var inserted = await ExecuteAsync(connection, mergeSql, ct).ConfigureAwait(false);
            if (inserted != eventList.Count) throw new InvalidOperationException("Death positions could not resolve their match UUID.");

        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackUtilityPositionsAsync(IEnumerable<UtilityPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events as IReadOnlyList<UtilityPositionEvent> ?? events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = (MySqlConnection)await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

            var dataTable = new DataTable();
            dataTable.Columns.Add("match_uuid", typeof(string));
            dataTable.Columns.Add("steam_id", typeof(ulong));
            dataTable.Columns.Add("throw_x", typeof(float));
            dataTable.Columns.Add("throw_y", typeof(float));
            dataTable.Columns.Add("throw_z", typeof(float));
            dataTable.Columns.Add("land_x", typeof(float));
            dataTable.Columns.Add("land_y", typeof(float));
            dataTable.Columns.Add("land_z", typeof(float));
            dataTable.Columns.Add("utility_type", typeof(int));
            dataTable.Columns.Add("entity_id", typeof(int));
            dataTable.Columns.Add("lifecycle_start_event", typeof(string));
            dataTable.Columns.Add("lifecycle_end_event", typeof(string));
            dataTable.Columns.Add("lifecycle_duration_ms", typeof(int));
            dataTable.Columns.Add("opponents_affected", typeof(int));
            dataTable.Columns.Add("teammates_affected", typeof(int));
            dataTable.Columns.Add("damage", typeof(int));
            dataTable.Columns.Add("map_name", typeof(string));
            dataTable.Columns.Add("round_number", typeof(int));
            dataTable.Columns.Add("round_time_seconds", typeof(int));

            foreach (var e in eventList)
            {
                dataTable.Rows.Add(
                    e.MatchUuid, e.SteamId,
                    e.ThrowX, e.ThrowY, e.ThrowZ,
                    e.LandX, e.LandY, e.LandZ,
                    e.UtilityType, e.EntityId, e.LifecycleStartEvent, e.LifecycleEndEvent, e.LifecycleDurationMs,
                    e.OpponentsAffected, e.TeammatesAffected, e.Damage,
                    e.MapName, e.RoundNumber, e.RoundTime
                );
            }

            await ExecuteAsync(connection, "CREATE TEMPORARY TABLE temp_utility_positions_uuid (row_id INT AUTO_INCREMENT PRIMARY KEY, match_uuid VARCHAR(64), steam_id BIGINT UNSIGNED, throw_x FLOAT, throw_y FLOAT, throw_z FLOAT, land_x FLOAT, land_y FLOAT, land_z FLOAT, utility_type INT, entity_id INT, lifecycle_start_event VARCHAR(32), lifecycle_end_event VARCHAR(32), lifecycle_duration_ms INT UNSIGNED, opponents_affected INT, teammates_affected INT, damage INT, map_name VARCHAR(100), round_number INT, round_time_seconds INT);", ct).ConfigureAwait(false);

            var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = "temp_utility_positions_uuid" };
            StatsRepository.AddColumnMappingsByName(bulkCopy, dataTable);
            StatsRepository.EnsureBulkCopySucceeded(await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false));

            const string mergeSql = """
                INSERT INTO utility_positions
                (match_id, steam_id, throw_pos, throw_z, land_pos, land_z,
                 utility_type, entity_id, lifecycle_start_event, lifecycle_end_event, lifecycle_duration_ms,
                 opponents_affected, teammates_affected, damage,
                 map_name, round_number, round_time_seconds)
                SELECT m.id, t.steam_id, POINT(t.throw_x, t.throw_y), t.throw_z, POINT(t.land_x, t.land_y), t.land_z,
                       t.utility_type, t.entity_id, t.lifecycle_start_event, t.lifecycle_end_event, t.lifecycle_duration_ms,
                       t.opponents_affected, t.teammates_affected, t.damage,
                       t.map_name, t.round_number, t.round_time_seconds
                FROM temp_utility_positions_uuid t
                JOIN matches m ON t.match_uuid = m.match_uuid;
                """;

            await ExecuteAsync(connection, "INSERT IGNORE INTO matches (match_uuid, map_name) SELECT DISTINCT match_uuid, map_name FROM temp_utility_positions_uuid;", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, "INSERT IGNORE INTO players (steam_id) SELECT DISTINCT steam_id FROM temp_utility_positions_uuid;", ct).ConfigureAwait(false);
            var inserted = await ExecuteAsync(connection, mergeSql, ct).ConfigureAwait(false);
            if (inserted != eventList.Count) throw new InvalidOperationException("Utility positions could not resolve their match UUID.");

        }, cancellationToken).ConfigureAwait(false);
    }

    public Task VoidRoundPositionsAsync(string matchUuid, int fromRound, CancellationToken cancellationToken = default) =>
        _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE t FROM kill_positions t JOIN matches m ON t.match_id = m.id WHERE m.match_uuid = @MatchUuid AND t.round_number >= @FromRound;
                DELETE t FROM death_positions t JOIN matches m ON t.match_id = m.id WHERE m.match_uuid = @MatchUuid AND t.round_number >= @FromRound;
                DELETE t FROM utility_positions t JOIN matches m ON t.match_id = m.id WHERE m.match_uuid = @MatchUuid AND t.round_number >= @FromRound;
                """, new { MatchUuid = matchUuid, FromRound = fromRound }, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken).AsTask();

    private static Task<int> ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
}
