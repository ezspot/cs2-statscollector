using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IBombEventProcessor : IEventProcessor
{
    /// <summary>The bomb in the current round: its site, plant time and the time left when defused.</summary>
    RoundBombInfo RoundBomb { get; }
}

public sealed class BombEventProcessor : IBombEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ICombatEventProcessor _combatProcessor;
    private readonly ILogger<BombEventProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    private DateTime? _bombPlantTime;
    private DateTime? _bombDefuseStartTime;
    private bool _defuseHasKit;
    private bool _defuseStartedAsClutch;
    // Freeze end to round end: the engine hands a T the bomb at round start, which fires bomb_pickup too.
    private bool _roundLive;
    private DateTime _roundStartUtc;
    private DateTime? _bombPlantedAt;
    private RoundBombInfo _roundBomb = RoundBombInfo.None;

    public RoundBombInfo RoundBomb => _roundBomb;

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
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            _bombPlantedAt = now;
            _roundBomb = _roundBomb with
            {
                Site = PlantedBombSite(),
                PlantTimeSeconds = _roundStartUtc == default ? null : Math.Round((decimal)(now - _roundStartUtc).TotalSeconds, 1)
            };

            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                var plantDuration = _bombPlantTime.HasValue
                    ? (_timeProvider.GetUtcNow().UtcDateTime - _bombPlantTime.Value).TotalSeconds
                    : 0;

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
            _roundBomb = _roundBomb with { DefuseTimeLeftSeconds = DefuseTimeLeft() };

            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                var defuseDuration = _bombDefuseStartTime.HasValue
                    ? (_timeProvider.GetUtcNow().UtcDateTime - _bombDefuseStartTime.Value).TotalSeconds
                    : 0;

                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombDefuses++;
                    stats.Bomb.TotalDefuseTime += (int)(defuseDuration * 1000);
                    if (_defuseHasKit) stats.Bomb.BombDefuseWithKit++;
                    else stats.Bomb.BombDefuseWithoutKit++;
                    if (_defuseStartedAsClutch) stats.Bomb.ClutchDefuses++;
                });

                _logger.LogInformation("Bomb defused by {SteamId} (Clutch: {Clutch})", playerState.SteamId, _defuseStartedAsClutch);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb defused event"); }
        finally { ResetBombState(); }
    }

    private void HandleBombExploded(EventBombExploded @event)
    {
        try
        {
            var playerState = PlayerControllerState.From(@event.GetPlayerOrDefault("userid"));
            if (playerState.IsValid && !playerState.IsBot)
            {
                _playerSessions.MutatePlayer(playerState.SteamId, stats => stats.Bomb.BombExplosions++);
                _logger.LogInformation("Bomb planted by {SteamId} exploded", playerState.SteamId);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb exploded event"); }
        finally { ResetBombState(); }
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
            if (!_roundLive) return;
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
                _defuseHasKit = @event.GetBoolValue("haskit", false);
                var (ctAlive, tAlive) = _combatProcessor.GetAliveCounts();
                _defuseStartedAsClutch = ctAlive == 1 && tAlive >= 1;
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombDefuseAttempts++;
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
                _defuseHasKit = false;
                _defuseStartedAsClutch = false;
                _playerSessions.MutatePlayer(playerState.SteamId, stats =>
                {
                    stats.Bomb.BombDefuseAborts++;
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling bomb abortdefuse event"); }
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
        _roundLive = true;
        _roundStartUtc = context.RoundStartUtc;
        _bombPlantedAt = null;
        // Kept until the next round starts: the round end reads it after the processors' round end ran.
        _roundBomb = RoundBombInfo.None;
    }

    // The planted bomb's site index: 0 is A, 1 is B.
    private static string? PlantedBombSite() =>
        Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault()?.BombSite switch { 0 => "A", 1 => "B", _ => null };

    // Seconds left on the timer: the bomb's own detonation time against the server clock.
    private decimal? DefuseTimeLeft()
    {
        var bomb = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
        if (bomb is { IsValid: true }) return Math.Round(Math.Max(0m, (decimal)(bomb.C4Blow - Server.CurrentTime)), 2);
        // ponytail: without the entity, the timer cvar against the plant time (wall clock, so a pause is not deducted).
        if (_bombPlantedAt is { } plantedAt &&
            decimal.TryParse(GameRulesState.ConVarValue("mp_c4timer"), NumberStyles.Number, CultureInfo.InvariantCulture, out var timer))
            return Math.Round(Math.Max(0m, timer - (decimal)(_timeProvider.GetUtcNow().UtcDateTime - plantedAt).TotalSeconds), 2);
        return null;
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
        ResetBombState();
        _roundLive = false;
    }

    private void ResetBombState()
    {
        _bombPlantTime = null;
        _bombDefuseStartTime = null;
        _defuseHasKit = false;
        _defuseStartedAsClutch = false;
    }
}
