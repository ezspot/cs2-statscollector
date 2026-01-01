using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IBombEventProcessor : IEventProcessor
{
    void ResetBombState();
}

public sealed class BombEventProcessor : IBombEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ICombatEventProcessor _combatProcessor;
    private readonly ILogger<BombEventProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IGameScheduler _scheduler;

    private DateTime? _bombPlantTime;
    private ulong? _planterSteamId;
    private DateTime? _bombDefuseStartTime;
    private ulong? _defuserSteamId;

    public BombEventProcessor(
        IPlayerSessionService playerSessions,
        ICombatEventProcessor combatProcessor,
        ILogger<BombEventProcessor> logger,
        TimeProvider timeProvider,
        IPersistenceChannel persistenceChannel,
        IGameScheduler scheduler)
    {
        _playerSessions = playerSessions;
        _combatProcessor = combatProcessor;
        _logger = logger;
        _timeProvider = timeProvider;
        _persistenceChannel = persistenceChannel;
        _scheduler = scheduler;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventBombBeginplant>((e, i) => { HandleBombBeginplant(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombAbortplant>((e, i) => { HandleBombAbortplant(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombPlanted>((e, i) => { HandleBombPlanted(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombDefused>((e, i) => { HandleBombDefused(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombExploded>((e, i) => { HandleBombExploded(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombDropped>((e, i) => { HandleBombDropped(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombPickup>((e, i) => { HandleBombPickup(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombBegindefuse>((e, i) => { HandleBombBegindefuse(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBombAbortdefuse>((e, i) => { HandleBombAbortdefuse(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventDefuserDropped>((e, i) => { HandleDefuserDropped(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventDefuserPickup>((e, i) => { HandleDefuserPickup(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerDeath>((e, i) => { HandlePlayerDeath(e); return HookResult.Continue; });
    }

    private void HandleBombBeginplant(EventBombBeginplant @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandleBombBeginplant");
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                _bombPlantTime = _timeProvider.GetUtcNow().UtcDateTime;
                _planterSteamId = playerState.SteamId;
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombPlantAttempts++;
                });
                _logger.LogDebug("Player {SteamId} started planting bomb at site {Site}", playerState.SteamId, @event.GetIntValue("site", 0));
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb beginplant event"); }
    }

    private void HandleBombAbortplant(EventBombAbortplant @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                _bombPlantTime = null;
                _planterSteamId = null;
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombPlantAborts++;
                });
                _logger.LogDebug("Player {SteamId} aborted bomb plant", playerState.SteamId);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb abortplant event"); }
    }

    private void HandleBombPlanted(EventBombPlanted @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandleBombPlanted");
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                var plantDuration = _bombPlantTime.HasValue 
                    ? (_timeProvider.GetUtcNow().UtcDateTime - _bombPlantTime.Value).TotalSeconds 
                    : 0;

                Instrumentation.BombPlantsCounter.Add(1, new KeyValuePair<string, object?>("site", @event.GetIntValue("site", 0)));
                Instrumentation.BombPlantDurationsRecorder.Record(plantDuration, new KeyValuePair<string, object?>("site", @event.GetIntValue("site", 0)));
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombPlants++;
                    stats.Bomb.TotalPlantTime += (int)(plantDuration * 1000);
                });

                _logger.LogInformation("Bomb planted by {SteamId} at site {Site}", playerState.SteamId, @event.GetIntValue("site", 0));
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb planted event"); }
    }

    private void HandleBombDefused(EventBombDefused @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandleBombDefused");
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                var defuseDuration = _bombDefuseStartTime.HasValue 
                    ? (_timeProvider.GetUtcNow().UtcDateTime - _bombDefuseStartTime.Value).TotalSeconds 
                    : 0;

                var (ctAlive, tAlive) = _combatProcessor.GetAliveCounts();
                var isClutchDefuse = ctAlive == 1 && tAlive >= 1;

                Instrumentation.BombDefusesCounter.Add(1);
                Instrumentation.BombDefuseDurationsRecorder.Record(defuseDuration);
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombDefuses++;
                    stats.Bomb.TotalDefuseTime += (int)(defuseDuration * 1000);
                    if (isClutchDefuse) stats.Bomb.ClutchDefuses++;
                });

                _logger.LogInformation("Bomb defused by {SteamId} (Clutch: {Clutch})", playerState.SteamId, isClutchDefuse);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb defused event"); }
        finally { ResetBombState(); }
    }

    private void HandleBombExploded(EventBombExploded @event)
    {
        Instrumentation.BombExplosionsCounter.Add(1);
        if (_bombPlantTime.HasValue)
        {
            Instrumentation.BombExplosionDurationsRecorder.Record((_timeProvider.GetUtcNow().UtcDateTime - _bombPlantTime.Value).TotalSeconds);
        }
        _logger.LogInformation("Bomb exploded");
        ResetBombState();
    }

    private void HandleBombDropped(EventBombDropped @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (playerState.IsValid && !playerState.IsBot)
            {
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombDrops++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb dropped event"); }
    }

    private void HandleBombPickup(EventBombPickup @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (playerState.IsValid && !playerState.IsBot)
            {
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombPickups++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb pickup event"); }
    }

    private void HandleBombBegindefuse(EventBombBegindefuse @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (playerState.IsValid && !playerState.IsBot)
            {
                _bombDefuseStartTime = _timeProvider.GetUtcNow().UtcDateTime;
                _defuserSteamId = playerState.SteamId;
                var hasKit = @event.GetBoolValue("haskit", false);
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombDefuseAttempts++;
                    if (hasKit) stats.Bomb.BombDefuseWithKit++;
                    else stats.Bomb.BombDefuseWithoutKit++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb begindefuse event"); }
    }

    private void HandleBombAbortdefuse(EventBombAbortdefuse @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (playerState.IsValid && !playerState.IsBot)
            {
                _bombDefuseStartTime = null;
                _defuserSteamId = null;
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombDefuseAborts++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb abortdefuse event"); }
    }

    private void HandleDefuserDropped(EventDefuserDropped @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (playerState.IsValid && !playerState.IsBot)
            {
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.DefuserDrops++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling defuser dropped event"); }
    }

    private void HandleDefuserPickup(EventDefuserPickup @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (playerState.IsValid && !playerState.IsBot)
            {
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.DefuserPickups++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling defuser pickup event"); }
    }

    private void HandleBombBeep(EventBombBeep @event) { }

    private void HandlePlayerDeath(EventPlayerDeath @event)
    {
        try
        {
            var weapon = @event.GetStringValue("weapon", "unknown");
            if (weapon != "planted_c4") return;

            var victim = @event.GetPlayerOrDefault("userid");
            var victimState = PlayerControllerState.From(victim);
            
            if (victimState.IsValid && !victimState.IsBot)
            {
                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Bomb.BombDeaths++;
                });
            }

            var attacker = @event.GetPlayerOrDefault("attacker");
            var attackerState = PlayerControllerState.From(attacker);
            
            if (attackerState.IsValid && !attackerState.IsBot && attackerState.SteamId != victimState.SteamId)
            {
                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    stats.Bomb.BombKills++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player death by bomb event"); }
    }

    public void OnRoundStart(RoundContext context)
    {
        ResetBombState();
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
        ResetBombState();
    }

    public void ResetBombState() { _bombPlantTime = null; _planterSteamId = null; _bombDefuseStartTime = null; _defuserSteamId = null; }
}
