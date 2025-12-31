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

    private DateTime? _bombPlantTime;
    private ulong? _planterSteamId;
    private DateTime? _bombDefuseStartTime;
    private ulong? _defuserSteamId;

    public BombEventProcessor(
        IPlayerSessionService playerSessions,
        ICombatEventProcessor combatProcessor,
        ILogger<BombEventProcessor> logger,
        TimeProvider timeProvider)
    {
        _playerSessions = playerSessions;
        _combatProcessor = combatProcessor;
        _logger = logger;
        _timeProvider = timeProvider;
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
            if (player is { IsBot: false })
            {
                _bombPlantTime = _timeProvider.GetUtcNow().UtcDateTime;
                _planterSteamId = player.SteamID;
                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.Bomb.BombPlantAttempts++;
                });
                _logger.LogDebug("Player {SteamId} started planting bomb at site {Site}", player.SteamID, @event.GetIntValue("site", 0));
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb beginplant event"); }
    }

    private void HandleBombAbortplant(EventBombAbortplant @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                _bombPlantTime = null;
                _planterSteamId = null;
                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.Bomb.BombPlantAborts++;
                });
                _logger.LogDebug("Player {SteamId} aborted bomb plant", player.SteamID);
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
            if (player is { IsBot: false })
            {
                var plantDuration = _bombPlantTime.HasValue 
                    ? (_timeProvider.GetUtcNow().UtcDateTime - _bombPlantTime.Value).TotalSeconds 
                    : 0;

                Instrumentation.BombPlantsCounter.Add(1, new KeyValuePair<string, object?>("site", @event.GetIntValue("site", 0)));
                Instrumentation.BombPlantDurationsRecorder.Record(plantDuration, new KeyValuePair<string, object?>("site", @event.GetIntValue("site", 0)));
                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.Bomb.BombPlants++;
                    stats.Bomb.TotalPlantTime += (int)(plantDuration * 1000);
                });

                _logger.LogInformation("Bomb planted by {SteamId} at site {Site}", player.SteamID, @event.GetIntValue("site", 0));
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
            if (player is { IsBot: false })
            {
                var defuseDuration = _bombDefuseStartTime.HasValue 
                    ? (_timeProvider.GetUtcNow().UtcDateTime - _bombDefuseStartTime.Value).TotalSeconds 
                    : 0;

                var (ctAlive, tAlive) = _combatProcessor.GetAliveCounts();
                var isClutchDefuse = ctAlive == 1 && tAlive >= 1;

                Instrumentation.BombDefusesCounter.Add(1);
                Instrumentation.BombDefuseDurationsRecorder.Record(defuseDuration);
                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.Bomb.BombDefuses++;
                    stats.Bomb.TotalDefuseTime += (int)(defuseDuration * 1000);
                    if (isClutchDefuse) stats.Bomb.ClutchDefuses++;
                });

                _logger.LogInformation("Bomb defused by {SteamId} (Clutch: {Clutch})", player.SteamID, isClutchDefuse);
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
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.Bomb.BombDrops++;
            });
        }
    }

    private void HandleBombPickup(EventBombPickup @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.Bomb.BombPickups++;
            });
        }
    }

    private void HandleBombBegindefuse(EventBombBegindefuse @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            _bombDefuseStartTime = _timeProvider.GetUtcNow().UtcDateTime;
            _defuserSteamId = player.SteamID;
            var hasKit = @event.GetBoolValue("haskit", false);
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.Bomb.BombDefuseAttempts++;
                if (hasKit) stats.Bomb.BombDefuseWithKit++;
                else stats.Bomb.BombDefuseWithoutKit++;
            });
        }
    }

    private void HandleBombAbortdefuse(EventBombAbortdefuse @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            _bombDefuseStartTime = null;
            _defuserSteamId = null;
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.Bomb.BombDefuseAborts++;
            });
        }
    }

    private void HandleDefuserDropped(EventDefuserDropped @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.Bomb.DefuserDrops++;
            });
        }
    }

    private void HandleDefuserPickup(EventDefuserPickup @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.Bomb.DefuserPickups++;
            });
        }
    }

    private void HandleBombBeep(EventBombBeep @event) { }

    private void HandlePlayerDeath(EventPlayerDeath @event)
    {
        var weapon = @event.GetStringValue("weapon", "unknown");
        if (weapon != "planted_c4") return;

        var victim = @event.GetPlayerOrDefault("userid");
        if (victim is { IsBot: false })
        {
            _playerSessions.MutatePlayer(victim.SteamID, stats =>
            {
                stats.Bomb.BombDeaths++;
            });
        }

        var attacker = @event.GetPlayerOrDefault("attacker");
        if (attacker is { IsBot: false } && attacker != victim)
        {
            _playerSessions.MutatePlayer(attacker.SteamID, stats =>
            {
                stats.Bomb.BombKills++;
            });
        }
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
