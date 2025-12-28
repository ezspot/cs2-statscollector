using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IEconomyEventProcessor
{
    void HandleItemPurchase(EventItemPurchase @event);
    void HandleItemPickup(EventItemPickup @event);
    void HandleItemEquip(EventItemEquip @event);
}

public sealed class EconomyEventProcessor(
    IPlayerSessionService playerSessions,
    ILogger<EconomyEventProcessor> logger) : IEconomyEventProcessor
{
    private static readonly Dictionary<string, int> WeaponCostMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pistols
        ["glock"] = 200,
        ["usp_silencer"] = 200,
        ["hkp2000"] = 200,
        ["p250"] = 300,
        ["fiveseven"] = 500,
        ["tec9"] = 500,
        ["cz75a"] = 500,
        ["deagle"] = 700,
        ["revolver"] = 700,

        // SMGs
        ["mac10"] = 1250,
        ["mp9"] = 1250,
        ["mp7"] = 1500,
        ["mp5sd"] = 1500,
        ["ump45"] = 1200,
        ["p90"] = 2350,
        ["bizon"] = 1400,

        // Rifles
        ["famas"] = 2250,
        ["galilar"] = 2250,
        ["m4a4"] = 2700,
        ["m4a1_silencer"] = 2700,
        ["ak47"] = 2700,
        ["aug"] = 3300,
        ["sg556"] = 3300,

        // Sniper Rifles
        ["ssg08"] = 1700,
        ["awp"] = 4750,
        ["scar20"] = 5000,
        ["g3sg1"] = 5000,

        // Heavy
        ["nova"] = 1050,
        ["xm1014"] = 2000,
        ["sawedoff"] = 1300,
        ["mag7"] = 1300,
        ["m249"] = 5200,
        ["negev"] = 1700,

        // Equipment
        ["vest"] = 650,
        ["vesthelm"] = 1000,
        ["defuser"] = 400,
        ["taser"] = 200,

        // Grenades
        ["flashbang"] = 200,
        ["hegrenade"] = 300,
        ["smokegrenade"] = 300,
        ["molotov"] = 400,
        ["incgrenade"] = 400,
        ["decoy"] = 50
    };

    public void HandleItemPurchase(EventItemPurchase @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is not { IsBot: false, IsValid: true }) return;

            var weapon = @event.GetStringValue("weapon", string.Empty) ?? string.Empty;
            var cost = GetWeaponCost(weapon);
            
            playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.ItemsPurchased++;
                stats.MoneySpent += cost;
                stats.CashSpent += cost;
                
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Player {SteamId} purchased {Weapon} for ${Cost}", 
                        player.SteamID, weapon, cost);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing item purchase event");
        }
    }

    public void HandleItemPickup(EventItemPickup @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is not { IsBot: false, IsValid: true }) return;

            var item = @event.GetStringValue("item", string.Empty);
            
            playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.ItemsPickedUp++;
                
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Player {SteamId} picked up {Item}", 
                        player.SteamID, item);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing item pickup event");
        }
    }

    public void HandleItemEquip(EventItemEquip @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is not { IsBot: false, IsValid: true }) return;

            var item = @event.GetStringValue("item", string.Empty) ?? string.Empty;
            var hasHelmet = @event.GetBoolValue("hashelmet", false);
            var hasDefuser = @event.GetBoolValue("hasdefuser", false);
            
            playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                // Track current equipment value
                var value = GetItemValue(item);
                if (hasHelmet) value += 350;
                if (hasDefuser) value += 400;
                
                stats.EquipmentValue = value;
                
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Player {SteamId} equipped {Item} (Value: ${Value})", 
                        player.SteamID, item, value);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing item equip event");
        }
    }

    /// <summary>
    /// Get weapon cost based on weapon name
    /// </summary>
    private static int GetWeaponCost(string weapon)
    {
        return WeaponCostMap.TryGetValue(weapon, out var cost) ? cost : 0;
    }

    /// <summary>
    /// Get item value for equipment tracking
    /// </summary>
    private static int GetItemValue(string item)
    {
        return WeaponCostMap.TryGetValue(item, out var value) ? value : 0;
    }
}
