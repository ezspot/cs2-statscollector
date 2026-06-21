using System;
using System.Collections.Generic;
using System.Threading;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
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
    private readonly IOptionsMonitor<PluginConfig> _config;

    public MovementEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<MovementEventProcessor> logger,
        IOptionsMonitor<PluginConfig> config)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _config = config;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        // Footsteps fire at very high frequency; only subscribe when movement tracking is enabled.
        if (!_config.CurrentValue.EnableMovementTracking) return;

        dispatcher.Subscribe<EventPlayerFootstep>((e, i) => { HandlePlayerFootstep(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerJump>((e, i) => { HandlePlayerJump(e); return HookResult.Continue; });
    }

    private void HandlePlayerFootstep(EventPlayerFootstep @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player == null || !player.IsValid || player.SteamID == 0 || player.IsBot) return;

        _playerSessions.MutatePlayer(player.SteamID, stats =>
        {
            stats.Round.Footsteps++;
        });
    }

    private void HandlePlayerJump(EventPlayerJump @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player == null || !player.IsValid || player.SteamID == 0 || player.IsBot) return;

        _playerSessions.MutatePlayer(player.SteamID, stats =>
        {
            stats.Round.Jumps++;
        });
    }
}
