using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface ICommunicationEventProcessor : IEventProcessor
{
}

public sealed class CommunicationEventProcessor : ICommunicationEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<CommunicationEventProcessor> _logger;
    private readonly IOptionsMonitor<PluginConfig> _config;

    public CommunicationEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<CommunicationEventProcessor> logger,
        IOptionsMonitor<PluginConfig> config)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _config = config;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        if (!_config.CurrentValue.EnableMovementTracking) return;

        dispatcher.Subscribe<EventPlayerPing>((e, i) => { HandlePlayerPing(e); return HookResult.Continue; });
    }

    private void HandlePlayerPing(EventPlayerPing @event)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player == null || !player.IsValid || player.SteamID == 0 || player.IsBot) return;

        _playerSessions.MutatePlayer(player.SteamID, stats =>
        {
            stats.Round.Pings++;
        });

        _logger.LogTrace("Player {SteamId} pinged at ({X}, {Y}, {Z})", player.SteamID, @event.X, @event.Y, @event.Z);
    }
}
