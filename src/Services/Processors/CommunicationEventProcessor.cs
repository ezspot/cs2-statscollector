using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface ICommunicationEventProcessor : IEventProcessor
{
}

public sealed class CommunicationEventProcessor : ICommunicationEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<CommunicationEventProcessor> _logger;

    public CommunicationEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<CommunicationEventProcessor> logger)
    {
        _playerSessions = playerSessions;
        _logger = logger;
    }

    public void OnRoundStart(RoundContext context)
    {
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        // Using dynamic subscription since EventPlayerPing might not be in all versions of CSS, 
        // but for late 2025 it should be standard.
        dispatcher.Subscribe<EventPlayerPing>((e, i) => { HandlePlayerPing(e); return HookResult.Continue; });
    }

    private void HandlePlayerPing(EventPlayerPing @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        var playerState = PlayerControllerState.From(player);
        if (!playerState.IsValid || playerState.IsBot) return;

        _playerSessions.MutatePlayer(playerState.SteamId, stats =>
        {
            stats.Round.Pings++;
        });

        _logger.LogTrace("Player {SteamId} pinged at ({X}, {Y}, {Z})", playerState.SteamId, @event.X, @event.Y, @event.Z);
    }
}
