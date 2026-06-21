using System;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IStatsRepository _statsRepository;
    private readonly IMatchStatsService _matchStats;

    public PlayerLifecycleHandler(
        ILogger<PlayerLifecycleHandler> logger,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IMatchTrackingService matchTracker,
        IPersistenceChannel persistenceChannel,
        IStatsRepository statsRepository,
        IMatchStatsService matchStats)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _matchTracker = matchTracker;
        _persistenceChannel = persistenceChannel;
        _statsRepository = statsRepository;
        _matchStats = matchStats;

        // Seed every newly-created session from the database (covers connect, spawn, and hot-reload).
        _playerSessions.PlayerSessionCreated += OnSessionCreated;
    }

    // Loads the player's persisted lifetime totals and seeds the in-memory session off the game thread,
    // then captures a per-match baseline in case they joined mid-match. Until this completes the session
    // is not eligible for persistence, so it can never overwrite the career row with an empty session.
    private void OnSessionCreated(ulong steamId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var lifetime = await _statsRepository.GetLifetimePlayerStatsAsync(steamId, CancellationToken.None);
                _playerSessions.HydrateLifetime(steamId, lifetime); // null => first-time player
                _matchStats.EnsureBaseline(steamId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed lifetime stats for {SteamId}", steamId);
            }
        });
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
            
            // Capture state and initialize session. Creating the session raises PlayerSessionCreated,
            // which triggers the off-thread lifetime seed (see OnSessionCreated).
            var state = PlayerControllerState.From(player);
            _playerSessions.EnsurePlayer(state);

            // Late-joiner into a live match; their spawn is counted by OnPlayerSpawn, so don't
            // increment TotalSpawns here (doing so double-counted the join).
            if (_scrimManager.CurrentState is ScrimState.Live)
            {
                _logger.LogInformation("Late-joiner detected: {Name}.", player.PlayerName);
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
            // Persist a final snapshot only if the session was seeded (TryGetSnapshot returns false
            // otherwise, so a player who left before seeding never overwrites their career row). Always
            // remove the session afterwards so an unseeded disconnect can't leak it.
            if (_playerSessions.TryGetSnapshot(steamId, out var snapshot, match?.MatchUuid))
            {
                _persistenceChannel.TryWrite(new StatsUpdate(UpdateType.PlayerStats, snapshot, match?.MatchUuid ?? "none", snapshot.RoundNumber, steamId));
            }
            _playerSessions.TryRemovePlayer(steamId, out _);
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
