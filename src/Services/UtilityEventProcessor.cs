using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IUtilityEventProcessor
{
    void HandlePlayerBlind(EventPlayerBlind @event);
    void HandleHegrenadeDetonate(EventHegrenadeDetonate @event);
    void HandleFlashbangDetonate(EventFlashbangDetonate @event);
    void HandleSmokegrenadeDetonate(EventSmokegrenadeDetonate @event);
    void HandleMolotovDetonate(EventMolotovDetonate @event);
}

public sealed class UtilityEventProcessor(IPlayerSessionService playerSessions, ILogger<UtilityEventProcessor> logger) : IUtilityEventProcessor
{
    public void HandlePlayerBlind(EventPlayerBlind @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var attacker = @event.GetPlayerOrDefault("attacker");
            var blindDuration = @event.GetFloatValue("blind_duration", 0f);

            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.TimesBlinded++;
                    stats.TotalBlindTime += (int)(blindDuration * 1000);
                });
            }

            if (attacker is { IsBot: false } && attacker != player)
            {
                playerSessions.MutatePlayer(attacker.SteamID, stats =>
                {
                    stats.PlayersBlinded++;
                    stats.TotalBlindTimeInflicted += (int)(blindDuration * 1000);
                    
                    if (player != null && attacker.Team != player.Team)
                    {
                        stats.EnemiesFlashed++;
                        if (blindDuration > 1.5f)
                        {
                            stats.EffectiveFlashes++;
                        }
                    }
                    else if (player != null && attacker.Team == player.Team)
                    {
                        stats.TeammatesFlashed++;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling player blind event");
        }
    }

    public void HandleHegrenadeDetonate(EventHegrenadeDetonate @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.EffectiveHEGrenades++;
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling HE grenade detonate event");
        }
    }

    public void HandleFlashbangDetonate(EventFlashbangDetonate @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    logger.LogTrace("Flashbang detonated by {SteamId}", player.SteamID);
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling flashbang detonate event");
        }
    }

    public void HandleSmokegrenadeDetonate(EventSmokegrenadeDetonate @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.EffectiveSmokes++;
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling smoke grenade detonate event");
        }
    }

    public void HandleMolotovDetonate(EventMolotovDetonate @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.EffectiveMolotovs++;
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling molotov detonate event");
        }
    }
}
