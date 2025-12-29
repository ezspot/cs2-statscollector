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
        await BulkTrackKillPositionsAsync([new KillPositionEvent(
            killerSteamId, victimSteamId, 
            killerPos.X, killerPos.Y, killerPos.Z,
            victimPos.X, victimPos.Y, victimPos.Z,
            weapon, isHeadshot, isWallbang,
            distance, killerTeam, victimTeam,
            mapName, roundNumber, roundTime)], cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackKillPositionsAsync(IEnumerable<KillPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackKillPositionsAsync");
            _logger.LogDebug("Starting bulk track for {Count} kill positions", eventList.Count);
            try
            {
                const string sql = """
                    INSERT INTO kill_positions 
                    (killer_steam_id, victim_steam_id, killer_x, killer_y, killer_z, 
                     victim_x, victim_y, victim_z, weapon_used, is_headshot, is_wallbang,
                     distance, killer_team, victim_team, map_name, round_number, round_time_seconds)
                    VALUES 
                    (@KillerSteamId, @VictimSteamId, @KillerX, @KillerY, @KillerZ,
                     @VictimX, @VictimY, @VictimZ, @Weapon, @IsHeadshot, @IsWallbang,
                     @Distance, @KillerTeam, @VictimTeam, @MapName, @RoundNumber, @RoundTime)
                    """;

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sql, eventList, cancellationToken: ct)).ConfigureAwait(false);
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
        await BulkTrackDeathPositionsAsync([new DeathPositionEvent(
            steamId, position.X, position.Y, position.Z,
            causeOfDeath, isHeadshot, team,
            mapName, roundNumber, roundTime)], cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackDeathPositionsAsync(IEnumerable<DeathPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackDeathPositionsAsync");
            _logger.LogDebug("Starting bulk track for {Count} death positions", eventList.Count);
            try
            {
                const string sql = """
                    INSERT INTO death_positions 
                    (steam_id, x, y, z, cause_of_death, is_headshot, team, map_name, round_number, round_time_seconds)
                    VALUES 
                    (@SteamId, @X, @Y, @Z, @CauseOfDeath, @IsHeadshot, @Team, @MapName, @RoundNumber, @RoundTime)
                    """;

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sql, eventList, cancellationToken: ct)).ConfigureAwait(false);
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
        await BulkTrackUtilityPositionsAsync([new UtilityPositionEvent(
            steamId, throwPos.X, throwPos.Y, throwPos.Z,
            landPos.X, landPos.Y, landPos.Z,
            utilityType, opponentsAffected, teammatesAffected,
            damage, mapName, roundNumber, roundTime)], cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkTrackUtilityPositionsAsync(IEnumerable<UtilityPositionEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("BulkTrackUtilityPositionsAsync");
            _logger.LogDebug("Starting bulk track for {Count} utility positions", eventList.Count);
            try
            {
                const string sql = """
                    INSERT INTO utility_positions 
                    (steam_id, throw_x, throw_y, throw_z, land_x, land_y, land_z,
                     utility_type, opponents_affected, teammates_affected, damage,
                     map_name, round_number, round_time_seconds)
                    VALUES 
                    (@SteamId, @ThrowX, @ThrowY, @ThrowZ, @LandX, @LandY, @LandZ,
                     @UtilityType, @OpponentsAffected, @TeammatesAffected, @Damage,
                     @MapName, @RoundNumber, @RoundTime)
                    """;

                await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
                var count = await connection.ExecuteAsync(new CommandDefinition(sql, eventList, cancellationToken: ct)).ConfigureAwait(false);
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
