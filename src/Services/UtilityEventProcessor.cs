using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IUtilityEventProcessor
{
    void SetRoundContext(int roundNumber, DateTime roundStartUtc);
    void HandlePlayerBlind(EventPlayerBlind @event);
    void HandleHegrenadeDetonate(EventHegrenadeDetonate @event);
    void HandleFlashbangDetonate(EventFlashbangDetonate @event);
    void HandleSmokegrenadeDetonate(EventSmokegrenadeDetonate @event);
    void HandleMolotovDetonate(EventMolotovDetonate @event);
}

public sealed class UtilityEventProcessor : IUtilityEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<UtilityEventProcessor> _logger;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;

    private DateTime _roundStartUtc;
    private int _currentRoundNumber;

    public UtilityEventProcessor(
        IPlayerSessionService playerSessions, 
        ILogger<UtilityEventProcessor> logger,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
    }

    public void SetRoundContext(int roundNumber, DateTime roundStartUtc)
    {
        _currentRoundNumber = roundNumber;
        _roundStartUtc = roundStartUtc;
    }

    public void HandlePlayerBlind(EventPlayerBlind @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandlePlayerBlind");
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var attacker = @event.GetPlayerOrDefault("attacker");
            var blindDuration = @event.GetFloatValue("blind_duration", 0f);
            var blindDurationMs = (long)(blindDuration * 1000);

            if (player is { IsBot: false })
            {
                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.TimesBlinded++;
                    stats.TotalBlindTime += (int)blindDurationMs;
                });
            }

            if (attacker is { IsBot: false } && attacker != player)
            {
                Instrumentation.BlindDurationCounter.Add(blindDurationMs, 
                    new KeyValuePair<string, object?>("attacker", attacker.SteamID),
                    new KeyValuePair<string, object?>("map", CounterStrikeSharp.API.Server.MapName));
                
                _playerSessions.MutatePlayer(attacker.SteamID, stats =>
                {
                    stats.PlayersBlinded++;
                    stats.TotalBlindTimeInflicted += (int)blindDurationMs;
                    
                    if (player != null)
                    {
                        if (attacker.TeamNum != player.TeamNum)
                        {
                            stats.EnemiesFlashed++;
                            if (player.PlayerPawn.Value?.FlashDuration > 1.0f) 
                            {
                                stats.FlashWaste++;
                                Instrumentation.FlashWasteCounter.Add(1);
                            }
                            if (blindDuration > 1.5f) stats.EffectiveFlashes++;
                        }
                        else
                        {
                            stats.TeammatesFlashed++;
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling player blind event");
        }
    }

    public void HandleHegrenadeDetonate(EventHegrenadeDetonate @event) => TrackDetonation(@event, UtilityType.HeGrenade, "hegrenade");
    public void HandleFlashbangDetonate(EventFlashbangDetonate @event) => TrackDetonation(@event, UtilityType.Flash, "flashbang");
    public void HandleSmokegrenadeDetonate(EventSmokegrenadeDetonate @event) => TrackDetonation(@event, UtilityType.Smoke, "smokegrenade");
    public void HandleMolotovDetonate(EventMolotovDetonate @event) => TrackDetonation(@event, UtilityType.Molotov, "molotov");

    private void TrackDetonation(GameEvent @event, UtilityType type, string typeName)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity($"Handle{typeName}Detonate");
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                Instrumentation.GrenadesDetonatedCounter.Add(1, 
                    new KeyValuePair<string, object?>("type", typeName),
                    new KeyValuePair<string, object?>("map", CounterStrikeSharp.API.Server.MapName));

                if (player.PlayerPawn.Value != null)
                {
                    var matchId = _matchTracker?.CurrentMatch?.MatchId;
                    _ = _positionPersistence.EnqueueAsync(new UtilityPositionEvent(
                        matchId,
                        player.SteamID,
                        player.PlayerPawn.Value.AbsOrigin?.X ?? 0,
                        player.PlayerPawn.Value.AbsOrigin?.Y ?? 0,
                        player.PlayerPawn.Value.AbsOrigin?.Z ?? 0,
                        @event.GetFloatValue("x", 0),
                        @event.GetFloatValue("y", 0),
                        @event.GetFloatValue("z", 0),
                        (int)type,
                        0, 0, 0,
                        CounterStrikeSharp.API.Server.MapName,
                        _currentRoundNumber,
                        (int)(DateTime.UtcNow - _roundStartUtc).TotalSeconds
                    ), CancellationToken.None);
                }

                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    switch (type)
                    {
                        case UtilityType.HeGrenade: stats.EffectiveHEGrenades++; break;
                        case UtilityType.Smoke: stats.EffectiveSmokes++; break;
                        case UtilityType.Molotov: stats.EffectiveMolotovs++; break;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Type} detonate event", typeName);
        }
    }
}
