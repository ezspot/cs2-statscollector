using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure;
using statsCollector.Infrastructure.Database;
using Polly;
using Polly.Registry;

namespace statsCollector.Services;

public record PlayerPositionSnapshot(
    ulong SteamId,
    float X, float Y, float Z,
    float Yaw, float Pitch, float Roll,
    int Team
);

public interface IPositionTrackingService
{
    void OnTick();
    Task TrackKillPositionAsync(
        string? matchUuid,
        ulong killerSteamId, 
        ulong victimSteamId, 
        Vector killerPos, 
        Vector victimPos, 
        string weapon, 
        bool isHeadshot, 
        bool isWallbang,
        int killerTeam,
        int victimTeam,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default);

    Task BulkTrackKillPositionsAsync(IEnumerable<KillPositionEvent> events, CancellationToken cancellationToken = default);

    Task TrackDeathPositionAsync(
        string? matchUuid,
        ulong steamId,
        Vector position,
        string causeOfDeath,
        bool isHeadshot,
        int team,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default);

    Task BulkTrackDeathPositionsAsync(IEnumerable<DeathPositionEvent> events, CancellationToken cancellationToken = default);

    Task TrackUtilityPositionAsync(
        string? matchUuid,
        ulong steamId,
        Vector throwPos,
        Vector landPos,
        int utilityType,
        int opponentsAffected,
        int teammatesAffected,
        int damage,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default);

    Task BulkTrackUtilityPositionsAsync(IEnumerable<UtilityPositionEvent> events, CancellationToken cancellationToken = default);
}

public sealed class PositionTrackingService : IPositionTrackingService
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<PositionTrackingService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IGameScheduler _scheduler;
    private readonly IMatchTrackingService _matchTracker;
    private int _tickCount;
    private const int TrackIntervalTicks = 128; // Track every 1 second at 128 tick

    public PositionTrackingService(
        IConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<PositionTrackingService> logger,
        IPlayerSessionService playerSessions,
        IPositionPersistenceService positionPersistence,
        IGameScheduler scheduler,
        IMatchTrackingService matchTracker)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("database");
        _playerSessions = playerSessions;
        _positionPersistence = positionPersistence;
        _scheduler = scheduler;
        _matchTracker = matchTracker;
    }

    public void OnTick()
    {
        _tickCount++;
        if (_tickCount % TrackIntervalTicks != 0) return;

        var steamIds = _playerSessions.GetActiveSteamIds();
        if (steamIds.Count == 0) return;

        var positions = ArrayPool<PlayerPositionSnapshot>.Shared.Rent(steamIds.Count);
        int actualCount = 0;

        try
        {
            foreach (var steamId in steamIds)
            {
                var player = CounterStrikeSharp.API.Utilities.GetPlayerFromSteamId(steamId);
                if (player is { IsValid: true, PlayerPawn.Value: not null })
                {
                    var pawn = player.PlayerPawn.Value;
                    var pos = pawn.AbsOrigin;
                    var ang = pawn.EyeAngles;

                    if (pos != null && ang != null)
                    {
                        positions[actualCount++] = new PlayerPositionSnapshot(
                            steamId,
                            pos.X, pos.Y, pos.Z,
                            ang.X, ang.Y, ang.Z,
                            (int)player.TeamNum
                        );
                    }
                }
            }

            if (actualCount > 0)
            {
                var batch = new PlayerPositionSnapshot[actualCount];
                Array.Copy(positions, batch, actualCount);
                var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
                _ = _positionPersistence.EnqueueAsync(new PositionTickEvent(CounterStrikeSharp.API.Server.MapName, matchUuid, batch), CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during position tracking tick");
        }
        finally
        {
            ArrayPool<PlayerPositionSnapshot>.Shared.Return(positions);
        }
    }

    public async Task TrackKillPositionAsync(
        string? matchUuid,
        ulong killerSteamId,
        ulong victimSteamId,
        Vector killerPos,
        Vector victimPos,
        string weapon,
        bool isHeadshot,
        bool isWallbang,
        int killerTeam,
        int victimTeam,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default)
    {
        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("TrackKillPositionAsync");
            const string sql = """
                INSERT INTO kill_positions 
                (match_id, killer_steam_id, victim_steam_id, killer_pos, killer_z, 
                 victim_pos, victim_z, weapon_used, is_headshot, is_wallbang,
                 distance, killer_team, victim_team, map_name, round_number, round_time_seconds)
                SELECT m.id, @KS, @VS, POINT(@KX, @KY), @KZ, POINT(@VX, @VY), @VZ, @W, @HS, @WB, @D, @KT, @VT, @M, @RN, @RT
                FROM matches m WHERE m.match_uuid = @MatchUuid
                """;

            var distance = CalculateDistance(killerPos, victimPos);
            var parameters = new {
                MatchUuid = matchUuid,
                KS = killerSteamId, VS = victimSteamId,
                KX = killerPos.X, KY = killerPos.Y, KZ = killerPos.Z,
                VX = victimPos.X, VY = victimPos.Y, VZ = victimPos.Z,
                W = weapon, HS = isHeadshot, WB = isWallbang,
                D = distance, KT = killerTeam, VT = victimTeam,
                M = mapName, RN = roundNumber, RT = roundTime
            };

            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackKillPositionsAsync(IEnumerable<KillPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackKillPositionsAsync");
            
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

            await connection.ExecuteAsync("CREATE TEMPORARY TABLE temp_kill_positions_uuid LIKE kill_positions;").ConfigureAwait(false);
            // Wait, kill_positions table might not have match_uuid column. 
            // The standard pattern we've used is to JOIN with matches table.
            // Let's adjust to use a temp table that can resolve match_id.
            
            await connection.ExecuteAsync("ALTER TABLE temp_kill_positions_uuid ADD COLUMN match_uuid VARCHAR(64);").ConfigureAwait(false);
            
            using var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = "temp_kill_positions_uuid" };
            await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false);

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

            await connection.ExecuteAsync(mergeSql).ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TEMPORARY TABLE temp_kill_positions_uuid;").ConfigureAwait(false);
            
            Instrumentation.PositionEventsTrackedCounter.Add(eventList.Count, new KeyValuePair<string, object?>("type", "kill"));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task TrackDeathPositionAsync(
        string? matchUuid,
        ulong steamId,
        Vector position,
        string causeOfDeath,
        bool isHeadshot,
        int team,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default)
    {
        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("TrackDeathPositionAsync");
            const string sql = """
                INSERT INTO death_positions 
                (match_id, steam_id, pos, z, cause_of_death, is_headshot, team, map_name, round_number, round_time_seconds)
                SELECT m.id, @S, POINT(@X, @Y), @Z, @C, @HS, @T, @M, @RN, @RT
                FROM matches m WHERE m.match_uuid = @MatchUuid
                """;

            var parameters = new {
                MatchUuid = matchUuid, S = steamId,
                X = position.X, Y = position.Y, Z = position.Z,
                C = causeOfDeath, HS = isHeadshot, T = team,
                M = mapName, RN = roundNumber, RT = roundTime
            };

            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackDeathPositionsAsync(IEnumerable<DeathPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackDeathPositionsAsync");
            
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

            await connection.ExecuteAsync("CREATE TEMPORARY TABLE temp_death_positions_uuid (match_uuid VARCHAR(64), steam_id BIGINT UNSIGNED, x FLOAT, y FLOAT, z FLOAT, cause_of_death VARCHAR(64), is_headshot TINYINT(1), team INT, map_name VARCHAR(64), round_number INT, round_time_seconds INT);").ConfigureAwait(false);
            
            using var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = "temp_death_positions_uuid" };
            await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false);

            const string mergeSql = """
                INSERT INTO death_positions 
                (match_id, steam_id, pos, z, cause_of_death, is_headshot, team, map_name, round_number, round_time_seconds)
                SELECT m.id, t.steam_id, POINT(t.x, t.y), t.z, t.cause_of_death, t.is_headshot, t.team, t.map_name, t.round_number, t.round_time_seconds
                FROM temp_death_positions_uuid t
                JOIN matches m ON t.match_uuid = m.match_uuid;
                """;

            await connection.ExecuteAsync(mergeSql).ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TEMPORARY TABLE temp_death_positions_uuid;").ConfigureAwait(false);
            
            Instrumentation.PositionEventsTrackedCounter.Add(eventList.Count, new KeyValuePair<string, object?>("type", "death"));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task TrackUtilityPositionAsync(
        string? matchUuid,
        ulong steamId,
        Vector throwPos,
        Vector landPos,
        int utilityType,
        int opponentsAffected,
        int teammatesAffected,
        int damage,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default)
    {
        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("TrackUtilityPositionAsync");
            const string sql = """
                INSERT INTO utility_positions 
                (match_id, steam_id, throw_pos, throw_z, land_pos, land_z,
                 utility_type, opponents_affected, teammates_affected, damage,
                 map_name, round_number, round_time_seconds)
                SELECT m.id, @SteamId, POINT(@ThrowX, @ThrowY), @ThrowZ, POINT(@LandX, @LandY), @LandZ,
                       @UtilityType, @OpponentsAffected, @TeammatesAffected, @Damage,
                       @MapName, @RoundNumber, @RoundTime
                FROM matches m WHERE m.match_uuid = @MatchUuid
                """;

            var parameters = new {
                MatchUuid = matchUuid, SteamId = steamId,
                ThrowX = throwPos.X, ThrowY = throwPos.Y, ThrowZ = throwPos.Z,
                LandX = landPos.X, LandY = landPos.Y, LandZ = landPos.Z,
                UtilityType = utilityType, OpponentsAffected = opponentsAffected,
                TeammatesAffected = teammatesAffected, Damage = damage,
                MapName = mapName, RoundNumber = roundNumber, RoundTime = roundTime
            };

            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackUtilityPositionsAsync(IEnumerable<UtilityPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackUtilityPositionsAsync");
            
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
                    e.UtilityType, e.OpponentsAffected, e.TeammatesAffected, e.Damage,
                    e.MapName, e.RoundNumber, e.RoundTime
                );
            }

            await connection.ExecuteAsync("CREATE TEMPORARY TABLE temp_utility_positions_uuid (match_uuid VARCHAR(64), steam_id BIGINT UNSIGNED, throw_x FLOAT, throw_y FLOAT, throw_z FLOAT, land_x FLOAT, land_y FLOAT, land_z FLOAT, utility_type INT, opponents_affected INT, teammates_affected INT, damage INT, map_name VARCHAR(64), round_number INT, round_time_seconds INT);").ConfigureAwait(false);
            
            using var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = "temp_utility_positions_uuid" };
            await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false);

            const string mergeSql = """
                INSERT INTO utility_positions 
                (match_id, steam_id, throw_pos, throw_z, land_pos, land_z,
                 utility_type, opponents_affected, teammates_affected, damage,
                 map_name, round_number, round_time_seconds)
                SELECT m.id, t.steam_id, POINT(t.throw_x, t.throw_y), t.throw_z, POINT(t.land_x, t.land_y), t.land_z,
                       t.utility_type, t.opponents_affected, t.teammates_affected, t.damage,
                       t.map_name, t.round_number, t.round_time_seconds
                FROM temp_utility_positions_uuid t
                JOIN matches m ON t.match_uuid = m.match_uuid;
                """;

            await connection.ExecuteAsync(mergeSql).ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TEMPORARY TABLE temp_utility_positions_uuid;").ConfigureAwait(false);
            
            Instrumentation.PositionEventsTrackedCounter.Add(eventList.Count, new KeyValuePair<string, object?>("type", "utility"));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static float CalculateDistance(Vector pos1, Vector pos2)
    {
        var dx = pos2.X - pos1.X;
        var dy = pos2.Y - pos1.Y;
        var dz = pos2.Z - pos1.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
