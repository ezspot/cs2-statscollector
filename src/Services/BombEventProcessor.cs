using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IBombEventProcessor
{
    void HandleBombBeginplant(EventBombBeginplant @event);
    void HandleBombAbortplant(EventBombAbortplant @event);
    void HandleBombPlanted(EventBombPlanted @event);
    void HandleBombDefused(EventBombDefused @event);
    void HandleBombExploded(EventBombExploded @event);
    void HandleBombDropped(EventBombDropped @event);
    void HandleBombPickup(EventBombPickup @event);
    void HandleBombBegindefuse(EventBombBegindefuse @event);
    void HandleBombAbortdefuse(EventBombAbortdefuse @event);
    void HandleDefuserDropped(EventDefuserDropped @event);
    void HandleDefuserPickup(EventDefuserPickup @event);
    void HandleBombBeep(EventBombBeep @event);
    void HandlePlayerDeath(EventPlayerDeath @event);
    void HandleRoundEnd(EventRoundEnd @event);
    void ResetBombState();
}

public sealed class BombEventProcessor(
    IPlayerSessionService playerSessions,
    ICombatEventProcessor combatProcessor,
    ILogger<BombEventProcessor> logger) : IBombEventProcessor
{
    private DateTime? _bombPlantTime;
    private ulong? _planterSteamId;
    private DateTime? _bombDefuseStartTime;
    private ulong? _defuserSteamId;

    public void HandleBombBeginplant(EventBombBeginplant @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var site = @event.GetIntValue("site", 0);

            if (player is { IsBot: false })
            {
                _bombPlantTime = DateTime.UtcNow;
                _planterSteamId = player.SteamID;

                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombPlantAttempts++;
                });

                logger.LogDebug("Player {SteamId} started planting bomb at site {Site}", player.SteamID, site);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb beginplant event");
        }
    }

    public void HandleBombAbortplant(EventBombAbortplant @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");

            if (player is { IsBot: false })
            {
                // Reset bomb plant state
                _bombPlantTime = null;
                _planterSteamId = null;

                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombPlantAborts++;
                });

                logger.LogDebug("Player {SteamId} aborted bomb plant", player.SteamID);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb abortplant event");
        }
    }

    public void HandleBombPlanted(EventBombPlanted @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var site = @event.GetIntValue("site", 0);

            if (player is { IsBot: false })
            {
                var plantTime = _bombPlantTime.HasValue 
                    ? (DateTime.UtcNow - _bombPlantTime.Value).TotalSeconds 
                    : 0;

                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombPlants++;
                    stats.TotalPlantTime += (int)(plantTime * 1000); // Convert to milliseconds
                });

                logger.LogDebug("Player {SteamId} planted bomb at site {Site} in {Time}s", 
                    player.SteamID, site, plantTime);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb planted event");
        }
    }

    public void HandleBombDefused(EventBombDefused @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");

            if (player is { IsBot: false })
            {
                var defuseTime = _bombDefuseStartTime.HasValue 
                    ? (DateTime.UtcNow - _bombDefuseStartTime.Value).TotalSeconds 
                    : 0;

                // Check if this was a clutch defuse using alive counts at the moment
                var (ctAlive, tAlive) = combatProcessor.GetAliveCounts();
                var isClutchDefuse = ctAlive == 1 && tAlive >= 2;

                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombDefuses++;
                    stats.TotalDefuseTime += (int)(defuseTime * 1000); // Convert to milliseconds
                    
                    if (isClutchDefuse)
                    {
                        stats.ClutchDefuses++;
                    }
                });

                logger.LogDebug("Player {SteamId} defused bomb in {Time}s (Clutch: {Clutch})", 
                    player.SteamID, defuseTime, isClutchDefuse);
            }

            // Reset bomb state
            _bombDefuseStartTime = null;
            _defuserSteamId = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb defused event");
        }

        ResetBombState();
    }

    public void HandleBombExploded(EventBombExploded @event)
    {
        try
        {
            // Reset bomb state when bomb explodes
            ResetBombState();

            logger.LogDebug("Bomb exploded");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb exploded event");
        }
    }

    public void HandleBombDropped(EventBombDropped @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");

            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombDrops++;
                });

                logger.LogDebug("Player {SteamId} dropped bomb", player.SteamID);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb dropped event");
        }
    }

    public void HandleBombPickup(EventBombPickup @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");

            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombPickups++;
                });

                logger.LogDebug("Player {SteamId} picked up bomb", player.SteamID);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb pickup event");
        }
    }

    public void HandleBombBegindefuse(EventBombBegindefuse @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var hasKit = @event.GetBoolValue("haskit", false);

            if (player is { IsBot: false })
            {
                _bombDefuseStartTime = DateTime.UtcNow;
                _defuserSteamId = player.SteamID;

                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombDefuseAttempts++;
                    
                    if (hasKit)
                    {
                        stats.BombDefuseWithKit++;
                    }
                    else
                    {
                        stats.BombDefuseWithoutKit++;
                    }
                });

                logger.LogDebug("Player {SteamId} started defusing bomb (Kit: {HasKit})", player.SteamID, hasKit);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb begindefuse event");
        }
    }

    public void HandleBombAbortdefuse(EventBombAbortdefuse @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");

            if (player is { IsBot: false })
            {
                // Reset defuse state
                _bombDefuseStartTime = null;
                _defuserSteamId = null;

                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.BombDefuseAborts++;
                });

                logger.LogDebug("Player {SteamId} aborted bomb defuse", player.SteamID);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb abortdefuse event");
        }
    }

    public void HandleDefuserDropped(EventDefuserDropped @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");

            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.DefuserDrops++;
                });

                logger.LogDebug("Player {SteamId} dropped defuser", player.SteamID);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling defuser dropped event");
        }
    }

    public void HandleDefuserPickup(EventDefuserPickup @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");

            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.DefuserPickups++;
                });

                logger.LogDebug("Player {SteamId} picked up defuser", player.SteamID);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling defuser pickup event");
        }
    }

    public void HandleBombBeep(EventBombBeep @event)
    {
        try
        {
            // Track bomb beeps for timing analysis
            logger.LogTrace("Bomb beep event");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bomb beep event");
        }
    }

    public void HandlePlayerDeath(EventPlayerDeath @event)
    {
        try
        {
            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            
            // Track bomb-related deaths
            if (weapon.Contains("bomb"))
            {
                var victim = @event.GetPlayerOrDefault("userid");
                var attacker = @event.GetPlayerOrDefault("attacker");
                
                if (victim is { IsBot: false })
                {
                    playerSessions.MutatePlayer(victim.SteamID, stats =>
                    {
                        stats.BombDeaths++;
                    });
                }

                if (attacker is { IsBot: false } && attacker != victim)
                {
                    playerSessions.MutatePlayer(attacker.SteamID, stats =>
                    {
                        stats.BombKills++;
                    });
                }

                logger.LogDebug("Bomb-related death: {Weapon}", weapon);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling player death event in bomb processor");
        }
    }

    public void HandleRoundEnd(EventRoundEnd @event)
    {
        try
        {
            var reason = @event.GetIntValue("reason", 0);
            ResetBombState();
            
            // Track round outcomes related to bomb
            // Reason 0 = Target Bombed (T win by bomb)
            // Reason 1 = Target Saved (CT win by defuse)
            // Reason 7 = Bomb Defused (CT win by defuse)
            
            switch (reason)
            {
                case 0: // Target Bombed
                    logger.LogDebug("Round ended: Target Bombed (T win)");
                    break;
                case 1: // Target Saved
                case 7: // Bomb Defused
                    logger.LogDebug("Round ended: Bomb Defused (CT win)");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling round end event in bomb processor");
        }
    }

    /// <summary>
    /// Reset bomb state (called at round start)
    /// </summary>
    public void ResetBombState()
    {
        try
        {
            _bombPlantTime = null;
            _planterSteamId = null;
            _bombDefuseStartTime = null;
            _defuserSteamId = null;

            logger.LogDebug("Bomb state reset");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resetting bomb state");
        }
    }
}
