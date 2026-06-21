using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
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
    private readonly IOptionsMonitor<PluginConfig> _config;

    private readonly ConcurrentDictionary<string, PendingGrenade> _pendingGrenades = new();
    private DateTime _roundStartUtc;
    private int _currentRoundNumber;

    public UtilityEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<UtilityEventProcessor> logger,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        IOptionsMonitor<PluginConfig> config)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _config = config;
    }

    private record PendingGrenade(ulong OwnerSteamId, UtilityType Type, DateTime DetonationTime)
    {
        public bool HasEffect { get; set; }
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _roundStartUtc = context.RoundStartUtc;
        _pendingGrenades.Clear();
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
        // A grenade that produced no hurt/blind effect during the round counts as wasted utility.
        // Evaluating once at round end (rather than a per-grenade timer task) avoids spawning a
        // thread-pool task for every detonation on a busy server.
        foreach (var kvp in _pendingGrenades)
        {
            var p = kvp.Value;
            if (p.HasEffect)
            {
                // A flash that blinded at least one enemy counts as an effective flash.
                if (p.Type == UtilityType.Flash)
                    _playerSessions.MutatePlayer(p.OwnerSteamId, stats => stats.Utility.EffectiveFlashes++);
                continue;
            }

            if (p.Type == UtilityType.Flash)
                _playerSessions.MutatePlayer(p.OwnerSteamId, stats => stats.Utility.WastedFlashes++);
        }
        _pendingGrenades.Clear();
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventGrenadeThrown>((e, i) => { HandleGrenadeThrown(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerBlind>((e, i) => { HandlePlayerBlind(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerHurt>((e, i) => { HandlePlayerHurt(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventHegrenadeDetonate>((e, i) => { HandleHegrenadeDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventFlashbangDetonate>((e, i) => { HandleFlashbangDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventSmokegrenadeDetonate>((e, i) => { HandleSmokegrenadeDetonate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventMolotovDetonate>((e, i) => { HandleMolotovDetonate(e); return HookResult.Continue; });
    }

    private void HandleGrenadeThrown(EventGrenadeThrown @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        var state = PlayerControllerState.From(player);
        if (!state.IsValid || state.IsBot) return;

        // grenade_thrown reports the grenade's designer name; normalize away any "weapon_" prefix so
        // we match whether the engine sends "flashbang" or "weapon_flashbang".
        var weapon = (@event.GetStringValue("weapon", string.Empty) ?? string.Empty)
            .ToLowerInvariant().Replace("weapon_", string.Empty);

        _playerSessions.MutatePlayer(state.SteamId, stats =>
        {
            switch (weapon)
            {
                case "flashbang": stats.Utility.FlashbangsThrown++; break;
                case "smokegrenade": stats.Utility.SmokesThrown++; break;
                case "molotov":
                case "incgrenade": stats.Utility.MolotovsThrown++; break;
                case "hegrenade": stats.Utility.HeGrenadesThrown++; break;
                case "decoy": stats.Utility.DecoysThrown++; break;
            }
        });
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
                    stats.Utility.TimesBlinded++;
                });
            }

            if (attackerState.IsValid && !attackerState.IsBot && attackerState.SteamId != victimState.SteamId)
            {
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
                _pendingGrenades[pendingKey] = new PendingGrenade(playerState.SteamId, type, now);

                if (playerState.PawnHandle != 0 && _config.CurrentValue.EnablePositionTracking)
                {
                    var matchUuid = _matchTracker?.CurrentMatch?.MatchUuid;
                    var posEvent = _positionPersistence.GetUtilityEvent();
                    
                    posEvent.MatchUuid = matchUuid;
                    posEvent.SteamId = playerState.SteamId;
                    posEvent.ThrowX = playerState.Position.X;
                    posEvent.ThrowY = playerState.Position.Y;
                    posEvent.ThrowZ = playerState.Position.Z;
                    posEvent.LandX = @event.GetFloatValue("x", 0);
                    posEvent.LandY = @event.GetFloatValue("y", 0);
                    posEvent.LandZ = @event.GetFloatValue("z", 0);
                    posEvent.UtilityType = (int)type;
                    posEvent.OpponentsAffected = 0;
                    posEvent.TeammatesAffected = 0;
                    posEvent.Damage = 0;
                    posEvent.MapName = CounterStrikeSharp.API.Server.MapName;
                    posEvent.RoundNumber = _currentRoundNumber;
                    posEvent.RoundTime = (int)(DateTime.UtcNow - _roundStartUtc).TotalSeconds;

                    _ = _positionPersistence.EnqueueAsync(posEvent, CancellationToken.None);
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
