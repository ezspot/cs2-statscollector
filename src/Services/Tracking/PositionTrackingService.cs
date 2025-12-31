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
        int? matchId,
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

    Task BulkTrackKillPositionsAsync(int? matchId, IEnumerable<KillPositionEvent> events, CancellationToken cancellationToken = default);

    Task TrackDeathPositionAsync(
        int? matchId,
        ulong steamId,
        Vector position,
        string causeOfDeath,
        bool isHeadshot,
        int team,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default);

    Task BulkTrackDeathPositionsAsync(int? matchId, IEnumerable<DeathPositionEvent> events, CancellationToken cancellationToken = default);

    Task TrackUtilityPositionAsync(
        int? matchId,
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

    Task BulkTrackUtilityPositionsAsync(int? matchId, IEnumerable<UtilityPositionEvent> events, CancellationToken cancellationToken = default);
}

public sealed class PositionTrackingService : IPositionTrackingService
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<PositionTrackingService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly ITaskTracker _taskTracker;
    private int _tickCount;
    private const int TrackIntervalTicks = 128; // Track every 1 second at 128 tick

    public PositionTrackingService(
        IConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<PositionTrackingService> logger,
        IPlayerSessionService playerSessions,
        IPositionPersistenceService positionPersistence,
        ITaskTracker taskTracker)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("database");
        _playerSessions = playerSessions;
        _positionPersistence = positionPersistence;
        _taskTracker = taskTracker;
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
                _taskTracker.Track("PositionTickEnqueue", _positionPersistence.EnqueueAsync(new PositionTickEvent(CounterStrikeSharp.API.Server.MapName, batch), CancellationToken.None));
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
        int? matchId,
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
                VALUES 
                (@MatchId, @KS, @VS, POINT(@KX, @KY), @KZ, POINT(@VX, @VY), @VZ, @W, @HS, @WB, @D, @KT, @VT, @M, @RN, @RT)
                """;

            var distance = CalculateDistance(killerPos, victimPos);
            var parameters = new {
                MatchId = matchId,
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

        public async Task BulkTrackKillPositionsAsync(int? matchId, IEnumerable<KillPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackKillPositionsAsync");
            _logger.LogDebug("Starting bulk track for {Count} kill positions (MatchId: {MatchId})", eventList.Count, matchId);
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(@"INSERT INTO kill_positions 
                    (match_id, killer_steam_id, victim_steam_id, killer_pos, killer_z, 
                     victim_pos, victim_z, weapon_used, is_headshot, is_wallbang,
                     distance, killer_team, victim_team, map_name, round_number, round_time_seconds)
                    VALUES ");

                var parameters = new DynamicParameters();
                for (int i = 0; i < eventList.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var e = eventList[i];
                    sb.Append($"(@MatchId{i}, @KS{i}, @VS{i}, POINT(@KX{i}, @KY{i}), @KZ{i}, POINT(@VX{i}, @VY{i}), @VZ{i}, @W{i}, @HS{i}, @WB{i}, @D{i}, @KT{i}, @VT{i}, @M{i}, @RN{i}, @RT{i})");
                    
                    parameters.Add($"MatchId{i}", matchId);
                    parameters.Add($"KS{i}", e.KillerSteamId);
                    parameters.Add($"VS{i}", e.VictimSteamId);
                    parameters.Add($"KX{i}", e.KillerX);
                    parameters.Add($"KY{i}", e.KillerY);
                    parameters.Add($"KZ{i}", e.KillerZ);
                    parameters.Add($"VX{i}", e.VictimX);
                    parameters.Add($"VY{i}", e.VictimY);
                    parameters.Add($"VZ{i}", e.VictimZ);
                    parameters.Add($"W{i}", e.Weapon);
                    parameters.Add($"HS{i}", e.IsHeadshot);
                    parameters.Add($"WB{i}", e.IsWallbang);
                    parameters.Add($"D{i}", e.Distance);
                    parameters.Add($"KT{i}", e.KillerTeam);
                    parameters.Add($"VT{i}", e.VictimTeam);
                    parameters.Add($"M{i}", e.MapName);
                    parameters.Add($"RN{i}", e.RoundNumber);
                    parameters.Add($"RT{i}", e.RoundTime);
                }

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sb.ToString(), parameters, cancellationToken: ct)).ConfigureAwait(false);
                _logger.LogDebug("Successfully tracked {Count} kill positions using bulk insert", count);
                Instrumentation.PositionEventsTrackedCounter.Add(count, new KeyValuePair<string, object?>("type", "kill"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk track kill positions for {Count} events", eventList.Count);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task TrackDeathPositionAsync(
        int? matchId,
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
                VALUES 
                (@MatchId, @S, POINT(@X, @Y), @Z, @C, @HS, @T, @M, @RN, @RT)
                """;

            var parameters = new {
                MatchId = matchId, S = steamId,
                X = position.X, Y = position.Y, Z = position.Z,
                C = causeOfDeath, HS = isHeadshot, T = team,
                M = mapName, RN = roundNumber, RT = roundTime
            };

            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackDeathPositionsAsync(int? matchId, IEnumerable<DeathPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackDeathPositionsAsync");
            _logger.LogDebug("Starting bulk track for {Count} death positions (MatchId: {MatchId})", eventList.Count, matchId);
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(@"INSERT INTO death_positions 
                    (match_id, steam_id, pos, z, cause_of_death, is_headshot, team, map_name, round_number, round_time_seconds)
                    VALUES ");

                var parameters = new DynamicParameters();
                for (int i = 0; i < eventList.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var e = eventList[i];
                    sb.Append($"(@MatchId{i}, @S{i}, POINT(@X{i}, @Y{i}), @Z{i}, @C{i}, @HS{i}, @T{i}, @M{i}, @RN{i}, @RT{i})");
                    
                    parameters.Add($"MatchId{i}", matchId);
                    parameters.Add($"S{i}", e.SteamId);
                    parameters.Add($"X{i}", e.X);
                    parameters.Add($"Y{i}", e.Y);
                    parameters.Add($"Z{i}", e.Z);
                    parameters.Add($"C{i}", e.CauseOfDeath);
                    parameters.Add($"HS{i}", e.IsHeadshot);
                    parameters.Add($"T{i}", e.Team);
                    parameters.Add($"M{i}", e.MapName);
                    parameters.Add($"RN{i}", e.RoundNumber);
                    parameters.Add($"RT{i}", e.RoundTime);
                }

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sb.ToString(), parameters, cancellationToken: ct)).ConfigureAwait(false);
                _logger.LogDebug("Successfully tracked {Count} death positions using bulk insert", count);
                Instrumentation.PositionEventsTrackedCounter.Add(count, new KeyValuePair<string, object?>("type", "death"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk track death positions for {Count} events", eventList.Count);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task TrackUtilityPositionAsync(
        int? matchId,
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
                VALUES 
                (@MatchId, @SteamId, POINT(@ThrowX, @ThrowY), @ThrowZ, POINT(@LandX, @LandY), @LandZ,
                 @UtilityType, @OpponentsAffected, @TeammatesAffected, @Damage,
                 @MapName, @RoundNumber, @RoundTime)
                """;

            var parameters = new {
                MatchId = matchId, SteamId = steamId,
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

    public async Task BulkTrackUtilityPositionsAsync(int? matchId, IEnumerable<UtilityPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackUtilityPositionsAsync");
            _logger.LogDebug("Starting bulk track for {Count} utility positions (MatchId: {MatchId})", eventList.Count, matchId);
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(@"INSERT INTO utility_positions 
                    (match_id, steam_id, throw_pos, throw_z, land_pos, land_z,
                     utility_type, opponents_affected, teammates_affected, damage,
                     map_name, round_number, round_time_seconds)
                    VALUES ");

                var parameters = new DynamicParameters();
                for (int i = 0; i < eventList.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var e = eventList[i];
                    sb.Append($"(@MatchId{i}, @SteamId{i}, POINT(@ThrowX{i}, @ThrowY{i}), @ThrowZ{i}, POINT(@LandX{i}, @LandY{i}), @LandZ{i}, @UtilityType{i}, @OpponentsAffected{i}, @TeammatesAffected{i}, @Damage{i}, @MapName{i}, @RoundNumber{i}, @RoundTime{i})");
                    
                    parameters.Add($"MatchId{i}", matchId);
                    parameters.Add($"SteamId{i}", e.SteamId);
                    parameters.Add($"ThrowX{i}", e.ThrowX);
                    parameters.Add($"ThrowY{i}", e.ThrowY);
                    parameters.Add($"ThrowZ{i}", e.ThrowZ);
                    parameters.Add($"LandX{i}", e.LandX);
                    parameters.Add($"LandY{i}", e.LandY);
                    parameters.Add($"LandZ{i}", e.LandZ);
                    parameters.Add($"UtilityType{i}", e.UtilityType);
                    parameters.Add($"OpponentsAffected{i}", e.OpponentsAffected);
                    parameters.Add($"TeammatesAffected{i}", e.TeammatesAffected);
                    parameters.Add($"Damage{i}", e.Damage);
                    parameters.Add($"MapName{i}", e.MapName);
                    parameters.Add($"RoundNumber{i}", e.RoundNumber);
                    parameters.Add($"RoundTime{i}", e.RoundTime);
                }

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sb.ToString(), parameters, cancellationToken: ct)).ConfigureAwait(false);
                _logger.LogDebug("Successfully tracked {Count} utility positions", count);
                Instrumentation.PositionEventsTrackedCounter.Add(count, new KeyValuePair<string, object?>("type", "utility"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk track utility positions for {Count} events", eventList.Count);
                throw;
            }
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
