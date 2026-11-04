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
    private static readonly TimeSpan SeedRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<PlayerLifecycleHandler> _logger;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IScrimManager _scrimManager;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IStatsRepository _statsRepository;
    private readonly IMatchStatsService _matchStats;
    private readonly CancellationToken _stoppingToken;

    public PlayerLifecycleHandler(
        ILogger<PlayerLifecycleHandler> logger,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IMatchTrackingService matchTracker,
        IPersistenceChannel persistenceChannel,
        IStatsRepository statsRepository,
        IMatchStatsService matchStats,
        CancellationTokenSource cancellationTokenSource)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _matchTracker = matchTracker;
        _persistenceChannel = persistenceChannel;
        _statsRepository = statsRepository;
        _matchStats = matchStats;
        _stoppingToken = cancellationTokenSource.Token;

        _playerSessions.PlayerSessionCreated += OnSessionCreated;
    }

    private void OnSessionCreated(ulong steamId)
    {
        _ = SeedLifetimeAsync(steamId);
    }

    // An unseeded session is never persisted, so one failed read would silently drop the player's whole
    // session: keep trying while they are connected.
    private async Task SeedLifetimeAsync(ulong steamId)
    {
        while (_playerSessions.GetActiveSteamIds().Contains(steamId))
        {
            try
            {
                // Write this player's queued and buffered snapshots first so the row read below is not older
                // than them (it throws while those writes still fail, which retries the seed later; failures
                // for other players don't hold it up).
                await _persistenceChannel.FlushAsync(steamId, _stoppingToken).ConfigureAwait(false);
                var lifetime = await _statsRepository.GetLifetimePlayerStatsAsync(steamId, _stoppingToken).ConfigureAwait(false);
                _playerSessions.HydrateLifetime(steamId, lifetime);
                _matchStats.EnsureBaseline(steamId);
                return;
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed lifetime stats for {SteamId}; retrying in {Delay}s", steamId, SeedRetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(SeedRetryDelay, _stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Register(BasePlugin plugin)
    {
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

            var state = PlayerControllerState.From(player);
            _playerSessions.EnsurePlayer(state);

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
        if (player != null && !player.IsBot && player.SteamID != 0)
        {
            var steamId = player.SteamID;
            _scrimManager.HandleDisconnect(steamId);

            var match = _matchTracker.CurrentMatch;
            _matchStats.FinalizePlayer(steamId);
            if (_playerSessions.TryGetSnapshot(steamId, out var snapshot, match?.MatchUuid))
            {
                _persistenceChannel.TryWrite(new PlayerStatsUpdate(snapshot));
            }
            _playerSessions.TryRemovePlayer(steamId);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false, SteamID: not 0 })
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
        if (player is { IsBot: false, SteamID: not 0 })
        {
            var team = @event.GetIntValue("team", 0);
            _playerSessions.MutatePlayer(player.SteamID, stats => stats.CurrentTeam = (PlayerTeam)team);
        }
        return HookResult.Continue;
    }
}
