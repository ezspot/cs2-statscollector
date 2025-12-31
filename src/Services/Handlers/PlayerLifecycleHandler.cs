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

    public PlayerLifecycleHandler(
        ILogger<PlayerLifecycleHandler> logger,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IMatchTrackingService matchTracker,
        IStatsRepository statsRepository)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _matchTracker = matchTracker;
        _statsRepository = statsRepository;
    }

    public void Register(BasePlugin plugin)
    {
        plugin.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
    }

    private HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null && !player.IsBot && player.SteamID != 0)
        {
            _playerSessions.EnsurePlayer(player.SteamID, player.PlayerName);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null && !player.IsBot)
        {
            _scrimManager.HandleDisconnect(player.SteamID);
            _ = SavePlayerStatsOnDisconnectAsync(player);
        }
        return HookResult.Continue;
    }

    private async Task SavePlayerStatsOnDisconnectAsync(CCSPlayerController player)
    {
        var matchId = _matchTracker.CurrentMatch?.MatchId;
        if (_playerSessions.TryGetSnapshot(player.SteamID, out var snapshot, matchId))
        {
            await _statsRepository.UpsertPlayerAsync(snapshot, default);
            _playerSessions.TryRemovePlayer(player.SteamID, out _);
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            _playerSessions.EnsurePlayer(player.SteamID, player.PlayerName);
            _playerSessions.MutatePlayer(player.SteamID, stats =>
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
