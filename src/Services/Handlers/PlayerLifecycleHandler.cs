using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;
using statsCollector.Services;

namespace statsCollector.Services.Handlers;

public sealed class PlayerLifecycleHandler : IGameHandler
{
    private readonly ILogger<PlayerLifecycleHandler> _logger;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IScrimManager _scrimManager;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IPersistenceChannel _persistenceChannel;

    public PlayerLifecycleHandler(
        ILogger<PlayerLifecycleHandler> logger,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IMatchTrackingService matchTracker,
        IPersistenceChannel persistenceChannel)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _matchTracker = matchTracker;
        _persistenceChannel = persistenceChannel;
    }

    public void Register(BasePlugin plugin)
    {
        // Use player_connect_full instead of player_connect for reliable SteamID resolution
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null && !player.IsBot && player.SteamID != 0)
        {
            _logger.LogInformation("Player {Name} fully connected. SteamID: {SteamID}", player.PlayerName, player.SteamID);
            
            // Capture state and initialize session
            var state = PlayerControllerState.From(player);
            _playerSessions.EnsurePlayer(state);

            // If a match is in progress, ensure they are initialized for the current round
            if (_scrimManager.CurrentState is ScrimState.Live)
            {
                _logger.LogInformation("Late-joiner detected: {Name}. Initializing for active match.", player.PlayerName);
                _playerSessions.MutatePlayer(player.SteamID, stats => 
                {
                    stats.Round.TotalSpawns++;
                    // Additional initialization if needed
                });
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null && !player.IsBot)
        {
            var steamId = player.SteamID;
            _scrimManager.HandleDisconnect(steamId);
            
            var match = _matchTracker.CurrentMatch;
            if (_playerSessions.TryGetSnapshot(steamId, out var snapshot, match?.MatchId, match?.MatchUuid))
            {
                _persistenceChannel.TryWrite(new StatsUpdate(UpdateType.PlayerStats, snapshot, match?.MatchUuid ?? "none", snapshot.RoundNumber, steamId));
                _playerSessions.TryRemovePlayer(steamId, out _);
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            var state = PlayerControllerState.From(player);
            _playerSessions.EnsurePlayer(state);
            _playerSessions.MutatePlayer(state.SteamId, stats =>
            {
                stats.Round.TotalSpawns++;
                stats.Round.PlaytimeSeconds = (int)(DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
            });
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            var team = @event.GetIntValue("team", 0);
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                if (team == 2) stats.Round.TRounds++;
                else if (team == 3) stats.Round.CtRounds++;
            });
        }
        return HookResult.Continue;
    }
}
