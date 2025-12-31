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

public interface IUtilityEventProcessor : IEventProcessor
{
}

public sealed class UtilityEventProcessor : IUtilityEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<UtilityEventProcessor> _logger;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IMapDataService _mapData;
    private readonly ITaskTracker _taskTracker;

    private DateTime _roundStartUtc;
    private int _currentRoundNumber;

    public UtilityEventProcessor(
        IPlayerSessionService playerSessions, 
        ILogger<UtilityEventProcessor> logger,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        IMapDataService mapData,
        ITaskTracker taskTracker)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _mapData = mapData;
        _taskTracker = taskTracker;
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _roundStartUtc = context.RoundStartUtc;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventPlayerBlind>((e, i) => { HandlePlayerBlind(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventHegrenadeDetonate>((e, i) => { HandleHegrenadeDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventFlashbangDetonate>((e, i) => { HandleFlashbangDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventSmokegrenadeDetonate>((e, i) => { HandleSmokegrenadeDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventMolotovDetonate>((e, i) => { HandleMolotovDetonate(e); return HookResult.Continue; });
    }

    private void HandlePlayerBlind(EventPlayerBlind @event)
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
                    stats.Utility.BlindDuration += (int)blindDurationMs;
                });
            }

            if (attacker is { IsBot: false } && attacker != player)
            {
                Instrumentation.BlindDurationCounter.Add(blindDurationMs, 
                    new KeyValuePair<string, object?>("attacker", attacker.SteamID),
                    new KeyValuePair<string, object?>("map", CounterStrikeSharp.API.Server.MapName));
                
                _playerSessions.MutatePlayer(attacker.SteamID, stats =>
                {
                    stats.Utility.EnemiesBlinded++;
                    stats.Utility.BlindDuration += (int)blindDurationMs; // TotalBlindTimeInflicted
                    
                    if (player != null)
                    {
                        if (attacker.TeamNum != player.TeamNum)
                        {
                            if (player.PlayerPawn.Value?.FlashDuration > 1.0f) 
                            {
                                stats.Utility.UtilityWasteCount++;
                                Instrumentation.FlashWasteCounter.Add(1);
                            }
                            if (blindDuration > 1.5f) stats.Utility.UtilitySuccessCount++;
                        }
                        else
                        {
                            stats.Utility.TeammatesBlinded++;
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

    private void HandleHegrenadeDetonate(EventHegrenadeDetonate @event) => TrackDetonation(@event, UtilityType.HeGrenade, "hegrenade");
    private void HandleFlashbangDetonate(EventFlashbangDetonate @event) => TrackDetonation(@event, UtilityType.Flash, "flashbang");
    private void HandleSmokegrenadeDetonate(EventSmokegrenadeDetonate @event) => TrackDetonation(@event, UtilityType.Smoke, "smokegrenade");
    private void HandleMolotovDetonate(EventMolotovDetonate @event) => TrackDetonation(@event, UtilityType.Molotov, "molotov");

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
                    _taskTracker.Track("UtilityPositionEnqueue", _positionPersistence.EnqueueAsync(new UtilityPositionEvent(
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
                    ), CancellationToken.None));
                }

                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    lock (stats.SyncRoot)
                    {
                        switch (type)
                        {
                            case UtilityType.HeGrenade: stats.EffectiveHEGrenades++; break;
                            case UtilityType.Smoke: stats.EffectiveSmokes++; break;
                            case UtilityType.Molotov: stats.EffectiveMolotovs++; break;
                        }
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
