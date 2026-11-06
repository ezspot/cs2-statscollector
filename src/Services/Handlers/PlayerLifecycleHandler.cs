using System;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Config;
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
    private readonly PluginConfig _config;
    private readonly CancellationToken _stoppingToken;

    public PlayerLifecycleHandler(
        ILogger<PlayerLifecycleHandler> logger,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IMatchTrackingService matchTracker,
        IPersistenceChannel persistenceChannel,
        IStatsRepository statsRepository,
        IMatchStatsService matchStats,
        PluginConfig config,
        CancellationTokenSource cancellationTokenSource)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _matchTracker = matchTracker;
        _persistenceChannel = persistenceChannel;
        _statsRepository = statsRepository;
        _matchStats = matchStats;
        _config = config;
        _stoppingToken = cancellationTokenSource.Token;

        _playerSessions.PlayerSessionCreated += OnSessionCreated;
    }

    private void OnSessionCreated(ulong steamId, long sessionId)
    {
        _ = SeedLifetimeAsync(steamId, sessionId);
    }

    // An unseeded session is never persisted, so one failed read would silently drop the player's whole
    // session: keep trying while they are connected.
    private async Task SeedLifetimeAsync(ulong steamId, long sessionId)
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
                _playerSessions.HydrateLifetime(steamId, sessionId, lifetime);
                // The player left while the seed was waiting on the database: their session was held for this write.
                if (_playerSessions.TryRemoveDisconnected(steamId, out var snapshot, _matchTracker.CurrentMatch?.MatchUuid))
                {
                    _persistenceChannel.TryWrite(new PlayerStatsUpdate(snapshot));
                    return;
                }
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
        plugin.RegisterListener<Listeners.OnClientAuthorized>((slot, _) => OnClientAuthorized(slot));
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
    }

    // The controller's SteamID is what the client claimed. Steam validates it after the connect, and until then
    // AuthorizedSteamID is null (CounterStrikeSharp 1.0.379 CCSPlayerController.cs:293-302); a session keyed on the
    // claim could credit someone else's account. A LAN server (sv_lan 1) never validates, so the claim is all it has.
    internal static bool IsAuthorized(CCSPlayerController player) =>
        player.AuthorizedSteamID is not null || GameRulesState.ConVarBool("sv_lan", false);

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null && !player.IsBot && player.SteamID != 0 && IsAuthorized(player)) StartSession(player);
        return HookResult.Continue;
    }

    // Validation that completes after player_connect_full starts the session here; before it, connect_full does.
    private void OnClientAuthorized(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is { IsValid: true, IsBot: false, SteamID: not 0 } && player.Connected == PlayerConnectedState.Connected)
            StartSession(player);
    }

    private void StartSession(CCSPlayerController player)
    {
        _logger.LogInformation("Player {Name} fully connected. SteamID: {SteamID}", player.PlayerName, player.SteamID);

        _playerSessions.EnsurePlayer(PlayerControllerState.From(player));

        if (_scrimManager.CurrentState is ScrimState.Live)
        {
            _logger.LogInformation("Late-joiner detected: {Name}.", player.PlayerName);
        }
        _scrimManager.HandlePlayerConnected(player.SteamID);
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: true }) return HookResult.Continue;
        // The controller is already gone for some disconnects; the event's xuid still names the player (0 for a bot).
        var steamId = player?.SteamID ?? @event.Xuid;
        if (steamId == 0) return HookResult.Continue;

        // A map change disconnects every client, and they reconnect on the new map; only a real disconnect may
        // abort the scrim (the knife round is played after the change to the voted map).
        if (!IsMapChangeDisconnect(@event.Reason)) _scrimManager.HandleDisconnect(steamId);

        var match = _matchTracker.CurrentMatch;
        _matchStats.FinalizePlayer(steamId);
        // An unseeded session (its seed is waiting on the database) has nothing persistable yet: it stays, marked,
        // until SeedLifetimeAsync has seeded it and writes it from there.
        _playerSessions.MarkDisconnected(steamId);
        if (_playerSessions.TryRemoveDisconnected(steamId, out var snapshot, match?.MatchUuid))
        {
            _persistenceChannel.TryWrite(new PlayerStatsUpdate(snapshot));
        }
        return HookResult.Continue;
    }

    // ENetworkDisconnectionReason: SHUTDOWN, RECONNECTION, LOOPSHUTDOWN, LOOPDEACTIVATE, HOST_ENDGAME,
    // LOOP_LEVELLOAD_ACTIVATE (GameTracking-CS2 Protobufs/network_connection.proto).
    private static bool IsMapChangeDisconnect(int reason) => reason is 1 or 53 or 54 or 55 or 56 or 57;

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false, SteamID: not 0 } && IsAuthorized(player))
        {
            var state = PlayerControllerState.From(player);
            _playerSessions.EnsurePlayer(state);
            // Only spawns in tracked rounds count: warmup, deathmatch, lobby and practice respawn players constantly.
            if (_config.DeathmatchMode || GameRulesState.IsWarmup ||
                (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live)) return HookResult.Continue;
            _playerSessions.MutatePlayer(state.SteamId, stats => stats.Round.TotalSpawns++);
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
