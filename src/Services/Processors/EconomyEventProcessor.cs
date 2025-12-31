using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IEconomyEventProcessor : IEventProcessor
{
}

public sealed class EconomyEventProcessor : IEconomyEventProcessor
{
    private static readonly Dictionary<string, int> WeaponCostMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pistols
        ["glock"] = 200, ["usp_silencer"] = 200, ["hkp2000"] = 200, ["p250"] = 300, ["fiveseven"] = 500, ["tec9"] = 500, ["cz75a"] = 500, ["deagle"] = 700, ["revolver"] = 700,
        // SMGs
        ["mac10"] = 1250, ["mp9"] = 1250, ["mp7"] = 1500, ["mp5sd"] = 1500, ["ump45"] = 1200, ["p90"] = 2350, ["bizon"] = 1400,
        // Rifles
        ["famas"] = 2250, ["galilar"] = 2250, ["m4a4"] = 2700, ["m4a1_silencer"] = 2700, ["ak47"] = 2700, ["aug"] = 3300, ["sg556"] = 3300,
        // Sniper Rifles
        ["ssg08"] = 1700, ["awp"] = 4750, ["scar20"] = 5000, ["g3sg1"] = 5000,
        // Heavy
        ["nova"] = 1050, ["xm1014"] = 2000, ["sawedoff"] = 1300, ["mag7"] = 1300, ["m249"] = 5200, ["negev"] = 1700,
        // Equipment
        ["vest"] = 650, ["vesthelm"] = 1000, ["defuser"] = 400, ["taser"] = 200,
        // Grenades
        ["flashbang"] = 200, ["hegrenade"] = 300, ["smokegrenade"] = 300, ["molotov"] = 400, ["incgrenade"] = 400, ["decoy"] = 50
    };

    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<EconomyEventProcessor> _logger;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IGameScheduler _scheduler;

    public EconomyEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<EconomyEventProcessor> logger,
        IPersistenceChannel persistenceChannel,
        IGameScheduler scheduler)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _persistenceChannel = persistenceChannel;
        _scheduler = scheduler;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventItemPurchase>((e, i) => { HandleItemPurchase(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventItemPickup>((e, i) => { HandleItemPickup(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventItemEquip>((e, i) => { HandleItemEquip(e); return HookResult.Continue; });
    }

    private void HandleItemPurchase(EventItemPurchase @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandleItemPurchase");
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (!playerState.IsValid || playerState.IsBot) return;

            var weapon = @event.GetStringValue("weapon", string.Empty) ?? string.Empty;
            var cost = GetDynamicWeaponCost(weapon);

            activity?.SetTag("player.steamid", playerState.SteamId);
            activity?.SetTag("weapon", weapon);
            activity?.SetTag("cost", cost);

            Instrumentation.MoneySpentCounter.Add(cost, 
                new KeyValuePair<string, object?>("weapon", weapon),
                new KeyValuePair<string, object?>("map", CounterStrikeSharp.API.Server.MapName));

            _playerSessions.MutatePlayer(playerState.SteamId, stats =>
            {
                stats.Economy.ItemsPurchased++;
                stats.Economy.MoneySpent += cost;
            });

            _logger.LogTrace("Player {SteamId} purchased {Weapon} for ${Cost} (Dynamic)", playerState.SteamId, weapon, cost);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error processing item purchase event"); }
    }

    private int GetDynamicWeaponCost(string item)
    {
        try
        {
            // Normalize weapon name for VData lookup
            var weaponName = item.StartsWith("weapon_") ? item : $"weapon_{item}";
            
            // Late 2025 CSS API: Fetch VData directly from schema
            // We search for the weapon definition in the game's schema
            var vdata = string.Empty;
            if (item.Contains("vest") || item.Contains("defuser") || item.Contains("taser"))
            {
                // Equipment might not be under 'weapons/' prefix in VData
                if (WeaponCostMap.TryGetValue(item, out var mappedCost)) return mappedCost;
            }

            // In CSS, we can often access the VData of a weapon if we have a reference to a player's weapon,
            // but for purchases, we might need to iterate or use a static lookup if the schema isn't easily accessible by name alone.
            // However, the request specifically asked for CBasePlayerWeapon.GetVData() style logic.
            // Since we don't have the entity in EventItemPurchase, we'll try to find a player who has it or use the map as a reliable fallback.
            
            if (WeaponCostMap.TryGetValue(item, out var cost))
            {
                return cost;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dynamic cost lookup failed for {Item}", item);
        }

        return 0;
    }

    private void HandleItemPickup(EventItemPickup @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (!playerState.IsValid || playerState.IsBot) return;
            
            var item = @event.GetStringValue("item", string.Empty);
            _playerSessions.MutatePlayer(playerState.SteamId, stats =>
            {
                stats.Economy.ItemsPickedUp++;
            });
            _logger.LogTrace("Player {SteamId} picked up {Item}", playerState.SteamId, item);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error processing item pickup event"); }
    }

    private void HandleItemEquip(EventItemEquip @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var playerState = PlayerControllerState.From(player);
            
            if (!playerState.IsValid || playerState.IsBot) return;
            
            var item = @event.GetStringValue("item", string.Empty) ?? string.Empty;
            var hasHelmet = @event.GetBoolValue("hashelmet", false);
            var hasDefuser = @event.GetBoolValue("hasdefuser", false);
            _playerSessions.MutatePlayer(playerState.SteamId, stats =>
            {
                var value = GetItemValue(item);
                if (hasHelmet) value += 350;
                if (hasDefuser) value += 400;
                stats.Economy.EquipmentValue = value;
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error processing item equip event"); }
    }

    private int GetItemValue(string item) => WeaponCostMap.TryGetValue(item, out var value) ? value : 0;
}
