using System;
using System.Collections.Generic;
using System.Threading;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IMovementEventProcessor : IEventProcessor
{
}

public sealed class MovementEventProcessor : IMovementEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<MovementEventProcessor> _logger;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;

    private int _currentRoundNumber;
    private DateTime _currentRoundStartUtc;

    public MovementEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<MovementEventProcessor> logger,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _currentRoundStartUtc = context.RoundStartUtc;
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventPlayerFootstep>((e, i) => { HandlePlayerFootstep(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerJump>((e, i) => { HandlePlayerJump(e); return HookResult.Continue; });
    }

    private void HandlePlayerFootstep(EventPlayerFootstep @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        var playerState = PlayerControllerState.From(player);
        if (!playerState.IsValid || playerState.IsBot) return;

        _playerSessions.MutatePlayer(playerState.SteamId, stats =>
        {
            stats.Round.Footsteps++;
        });
    }

    private void HandlePlayerJump(EventPlayerJump @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        var playerState = PlayerControllerState.From(player);
        if (!playerState.IsValid || playerState.IsBot) return;

        _playerSessions.MutatePlayer(playerState.SteamId, stats =>
        {
            stats.Round.Jumps++;
        });
    }
}
