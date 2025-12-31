using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Services;

namespace statsCollector.Services.Handlers;

public sealed class PlayerLifecycleHandler : IGameHandler
{
    private readonly ILogger<PlayerLifecycleHandler> _logger;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IScrimManager _scrimManager;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IStatsRepository _statsRepository;
    private readonly ITaskTracker _taskTracker;

    public PlayerLifecycleHandler(
        ILogger<PlayerLifecycleHandler> logger,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IMatchTrackingService matchTracker,
        IStatsRepository statsRepository,
        ITaskTracker taskTracker)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _matchTracker = matchTracker;
        _statsRepository = statsRepository;
        _taskTracker = taskTracker;
    }

    public void Register(BasePlugin plugin)
    {
        plugin.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
    }

    private HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null && !player.IsBot && player.SteamID != 0)
        {
            var state = PlayerControllerState.From(player);
            _playerSessions.EnsurePlayer(state);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null && !player.IsBot && player.SteamID != 0)
        {
            _logger.LogInformation("Player {Name} fully connected. SteamID: {SteamID}", player.PlayerName, player.SteamID);
            var state = PlayerControllerState.From(player);
            _playerSessions.EnsurePlayer(state);
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
            _taskTracker.Track("SaveStatsOnDisconnect", SavePlayerStatsOnDisconnectAsync(steamId));
        }
        return HookResult.Continue;
    }

    private async Task SavePlayerStatsOnDisconnectAsync(ulong steamId)
    {
        var match = _matchTracker.CurrentMatch;
        if (_playerSessions.TryGetSnapshot(steamId, out var snapshot, match?.MatchId, match?.MatchUuid))
        {
            await _statsRepository.UpsertPlayerAsync(snapshot, default);
            _playerSessions.TryRemovePlayer(steamId, out _);
        }
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
