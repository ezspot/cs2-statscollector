using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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

public interface IPositionTrackingService
{
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

    public PositionTrackingService(
        IConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<PositionTrackingService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("database");
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
        var distance = CalculateDistance(killerPos, victimPos);
        await BulkTrackKillPositionsAsync(matchId, [new KillPositionEvent(
            matchId,
            killerSteamId, victimSteamId, 
            killerPos.X, killerPos.Y, killerPos.Z,
            victimPos.X, victimPos.Y, victimPos.Z,
            weapon, isHeadshot, isWallbang,
            distance, killerTeam, victimTeam,
            mapName, roundNumber, roundTime)], cancellationToken).ConfigureAwait(false);
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
                const string sql = """
                    INSERT INTO kill_positions 
                    (match_id, killer_steam_id, victim_steam_id, killer_x, killer_y, killer_z, 
                     victim_x, victim_y, victim_z, weapon_used, is_headshot, is_wallbang,
                     distance, killer_team, victim_team, map_name, round_number, round_time_seconds)
                    VALUES 
                    (@MatchId, @KillerSteamId, @VictimSteamId, @KillerX, @KillerY, @KillerZ,
                     @VictimX, @VictimY, @VictimZ, @Weapon, @IsHeadshot, @IsWallbang,
                     @Distance, @KillerTeam, @VictimTeam, @MapName, @RoundNumber, @RoundTime)
                    """;

                var parameters = eventList.Select(e => new {
                    MatchId = matchId,
                    e.KillerSteamId, e.VictimSteamId, e.KillerX, e.KillerY, e.KillerZ,
                    e.VictimX, e.VictimY, e.VictimZ, e.Weapon, e.IsHeadshot, e.IsWallbang,
                    e.Distance, e.KillerTeam, e.VictimTeam, e.MapName, e.RoundNumber, e.RoundTime
                });

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
                _logger.LogDebug("Successfully tracked {Count} kill positions", count);
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
        await BulkTrackDeathPositionsAsync(matchId, [new DeathPositionEvent(
            matchId,
            steamId, position.X, position.Y, position.Z,
            causeOfDeath, isHeadshot, team,
            mapName, roundNumber, roundTime)], cancellationToken).ConfigureAwait(false);
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
                const string sql = """
                    INSERT INTO death_positions 
                    (match_id, steam_id, x, y, z, cause_of_death, is_headshot, team, map_name, round_number, round_time_seconds)
                    VALUES 
                    (@MatchId, @SteamId, @X, @Y, @Z, @CauseOfDeath, @IsHeadshot, @Team, @MapName, @RoundNumber, @RoundTime)
                    """;

                var parameters = eventList.Select(e => new {
                    MatchId = matchId,
                    e.SteamId, e.X, e.Y, e.Z, e.CauseOfDeath, e.IsHeadshot, e.Team,
                    e.MapName, e.RoundNumber, e.RoundTime
                });

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
                _logger.LogDebug("Successfully tracked {Count} death positions", count);
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
        await BulkTrackUtilityPositionsAsync(matchId, [new UtilityPositionEvent(
            matchId,
            steamId, throwPos.X, throwPos.Y, throwPos.Z,
            landPos.X, landPos.Y, landPos.Z,
            utilityType, opponentsAffected, teammatesAffected,
            damage, mapName, roundNumber, roundTime)], cancellationToken).ConfigureAwait(false);
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
                const string sql = """
                    INSERT INTO utility_positions 
                    (match_id, steam_id, throw_x, throw_y, throw_z, land_x, land_y, land_z,
                     utility_type, opponents_affected, teammates_affected, damage,
                     map_name, round_number, round_time_seconds)
                    VALUES 
                    (@MatchId, @SteamId, @ThrowX, @ThrowY, @ThrowZ, @LandX, @LandY, @LandZ,
                     @UtilityType, @OpponentsAffected, @TeammatesAffected, @Damage,
                     @MapName, @RoundNumber, @RoundTime)
                    """;

                var parameters = eventList.Select(e => new {
                    MatchId = matchId,
                    e.SteamId, e.ThrowX, e.ThrowY, e.ThrowZ, e.LandX, e.LandY, e.LandZ,
                    e.UtilityType, e.OpponentsAffected, e.TeammatesAffected, e.Damage,
                    e.MapName, e.RoundNumber, e.RoundTime
                });

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
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
