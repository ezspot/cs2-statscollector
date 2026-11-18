using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public sealed class EconomyEventProcessor : IEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;

    public EconomyEventProcessor(IPlayerSessionService playerSessions)
    {
        _playerSessions = playerSessions;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventItemPurchase>((e, i) => { HandleItemPurchase(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventItemPickup>((e, i) => { HandleItemPickup(e); return HookResult.Continue; });
    }

    private void HandleItemPurchase(EventItemPurchase @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player == null) return;

        var playerState = PlayerControllerState.From(player);
        if (!playerState.IsValid || playerState.IsBot) return;

        _playerSessions.MutatePlayer(playerState.SteamId, stats => stats.Economy.ItemsPurchased++);
    }

    private void HandleItemPickup(EventItemPickup @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        var playerState = PlayerControllerState.From(player);

        if (!playerState.IsValid || playerState.IsBot) return;

        _playerSessions.MutatePlayer(playerState.SteamId, stats =>
        {
            stats.Economy.ItemsPickedUp++;
        });
    }

}
