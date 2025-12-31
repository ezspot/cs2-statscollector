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
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IGameScheduler _scheduler;

    private readonly ConcurrentDictionary<string, PendingGrenade> _pendingGrenades = new();
    private DateTime _roundStartUtc;
    private int _currentRoundNumber;

    public UtilityEventProcessor(
        IPlayerSessionService playerSessions, 
        ILogger<UtilityEventProcessor> logger,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        IMapDataService mapData,
        IPersistenceChannel persistenceChannel,
        IGameScheduler scheduler)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _mapData = mapData;
        _persistenceChannel = persistenceChannel;
        _scheduler = scheduler;
    }

    private record PendingGrenade(ulong OwnerSteamId, UtilityType Type, DateTime DetonationTime)
    {
        public bool HasEffect { get; set; }
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _roundStartUtc = context.RoundStartUtc;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventPlayerBlind>((e, i) => { HandlePlayerBlind(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerHurt>((e, i) => { HandlePlayerHurt(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventHegrenadeDetonate>((e, i) => { HandleHegrenadeDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventFlashbangDetonate>((e, i) => { HandleFlashbangDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventSmokegrenadeDetonate>((e, i) => { HandleSmokegrenadeDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventMolotovDetonate>((e, i) => { HandleMolotovDetonate(e); return HookResult.Continue; });
    }

    private void HandlePlayerHurt(EventPlayerHurt @event)
    {
        var attacker = @event.GetPlayerOrDefault("attacker");
        var attackerState = PlayerControllerState.From(attacker);
        if (!attackerState.IsValid || attackerState.IsBot) return;

        // Mark any pending grenades from this attacker as having an effect if they did damage
        // We check for HE, Molotov, and Incendiary
        var weapon = @event.GetStringValue("weapon", string.Empty);
        if (weapon is "hegrenade" or "molotov" or "incgrenade")
        {
            var pendingKey = $"{weapon}_{attackerState.SteamId}";
            if (_pendingGrenades.TryGetValue(pendingKey, out var pending))
            {
                pending.HasEffect = true;
            }
        }
    }

    private void HandlePlayerBlind(EventPlayerBlind @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandlePlayerBlind");
        try
        {
            var victim = @event.GetPlayerOrDefault("userid");
            var attacker = @event.GetPlayerOrDefault("attacker");
            
            var victimState = PlayerControllerState.From(victim);
            var attackerState = PlayerControllerState.From(attacker);
            
            var blindDuration = @event.GetFloatValue("blind_duration", 0f);
            var blindDurationMs = (int)(blindDuration * 1000);

            if (attackerState.IsValid && !attackerState.IsBot)
            {
                // Mark any pending flashes from this attacker as having an effect if they blinded an enemy
                if (victimState.IsValid && attackerState.Team != victimState.Team)
                {
                    var pendingKey = $"flash_{attackerState.SteamId}";
                    if (_pendingGrenades.TryGetValue(pendingKey, out var pending))
                    {
                        pending.HasEffect = true;
                    }
                }
            }

            if (victimState.IsValid && !victimState.IsBot)
            {
                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Utility.TotalBlindTime += blindDuration;
                });
            }

            if (attackerState.IsValid && !attackerState.IsBot && attackerState.SteamId != victimState.SteamId)
            {
                Instrumentation.BlindDurationCounter.Add(blindDurationMs, 
                    new KeyValuePair<string, object?>("attacker", attackerState.SteamId),
                    new KeyValuePair<string, object?>("map", CounterStrikeSharp.API.Server.MapName));
                
                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    if (victimState.IsValid)
                    {
                        if (attackerState.Team != victimState.Team)
                        {
                            stats.Utility.EnemiesBlinded++;
                            stats.Utility.TotalBlindTimeInflicted += blindDuration;
                            
                            if (blindDuration > 1.5f) stats.Utility.UtilitySuccessCount++;
                        }
                        else
                        {
                            stats.Utility.TeammatesBlinded++;
                            stats.Utility.TeamFlashDuration += blindDurationMs;
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
            var playerState = PlayerControllerState.From(player);
            
            if (playerState.IsValid && !playerState.IsBot)
            {
                var now = DateTime.UtcNow;
                var pendingKey = $"{typeName}_{playerState.SteamId}";
                var pending = new PendingGrenade(playerState.SteamId, type, now);
                _pendingGrenades[pendingKey] = pending;

                // Schedule a check for waste after 500ms (to allow all hurt/blind events to arrive)
                _ = Task.Run(async () => 
                {
                    await Task.Delay(500);
                    if (_pendingGrenades.TryRemove(pendingKey, out var p) && !p.HasEffect)
                    {
                        _playerSessions.MutatePlayer(p.OwnerSteamId, stats => 
                        {
                            stats.Utility.UtilityWasteCount++;
                            if (p.Type == UtilityType.Flash)
                            {
                                stats.Utility.WastedFlashes++;
                            }
                        });
                        Instrumentation.FlashWasteCounter.Add(1, new KeyValuePair<string, object?>("player", p.OwnerSteamId), new KeyValuePair<string, object?>("type", p.Type.ToString()));
                    }
                });

                Instrumentation.GrenadesDetonatedCounter.Add(1, 
                    new KeyValuePair<string, object?>("type", typeName),
                    new KeyValuePair<string, object?>("map", CounterStrikeSharp.API.Server.MapName));

                if (playerState.PawnHandle != 0)
                {
                    var matchUuid = _matchTracker?.CurrentMatch?.MatchUuid;
                    _ = _positionPersistence.EnqueueAsync(new UtilityPositionEvent(
                        matchUuid,
                        playerState.SteamId,
                        playerState.Position.X,
                        playerState.Position.Y,
                        playerState.Position.Z,
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

                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    switch (type)
                    {
                        case UtilityType.HeGrenade: stats.Utility.EffectiveHEGrenades++; break;
                        case UtilityType.Smoke: stats.Utility.EffectiveSmokes++; break;
                        case UtilityType.Molotov: stats.Utility.EffectiveMolotovs++; break;
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
