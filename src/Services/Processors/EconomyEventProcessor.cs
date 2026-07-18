using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public sealed class EconomyEventProcessor : IEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<EconomyEventProcessor> _logger;

    public EconomyEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<EconomyEventProcessor> logger)
    {
        _playerSessions = playerSessions;
        _logger = logger;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventItemPurchase>((e, i) => { HandleItemPurchase(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventItemPickup>((e, i) => { HandleItemPickup(e); return HookResult.Continue; });
    }

    private void HandleItemPurchase(EventItemPurchase @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandleItemPurchase");
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player == null) return;

            var playerState = PlayerControllerState.From(player);
            if (!playerState.IsValid || playerState.IsBot) return;

            var weapon = @event.GetStringValue("weapon", string.Empty) ?? string.Empty;
            activity?.SetTag("player.steamid", playerState.SteamId);
            activity?.SetTag("weapon", weapon);

            _playerSessions.MutatePlayer(playerState.SteamId, stats => stats.Economy.ItemsPurchased++);

            _logger.LogTrace("Player {SteamId} purchased {Weapon}", playerState.SteamId, weapon);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error processing item purchase event"); }
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

}
