using System;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;
using statsCollector.Services;

namespace statsCollector.Services;

public interface IGameEventHandlerService
{
    void RegisterEvents(BasePlugin plugin);
}

public sealed class GameEventHandlerService : IGameEventHandlerService
{
    private readonly ILogger<GameEventHandlerService> _logger;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IEventDispatcher _dispatcher;
    private readonly IEnumerable<IEventProcessor> _processors;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IStatsPersistenceService _statsPersistence;
    private readonly IStatsRepository _statsRepository;
    private readonly IScrimManager _scrimManager;
    private readonly IPauseService _pauseService;
    private readonly IRoundBackupService _roundBackup;
    private readonly IOptionsMonitor<PluginConfig> _configMonitor;
    private readonly IAnalyticsService _analytics;
    private int _currentRoundNumber = 0;

    private PluginConfig _config => _configMonitor.CurrentValue;

    public GameEventHandlerService(
        ILogger<GameEventHandlerService> logger,
        IPlayerSessionService playerSessions,
        IEventDispatcher dispatcher,
        IEnumerable<IEventProcessor> processors,
        IMatchTrackingService matchTracker,
        IStatsPersistenceService statsPersistence,
        IStatsRepository statsRepository,
        IScrimManager scrimManager,
        IPauseService pauseService,
        IRoundBackupService roundBackup,
        IOptionsMonitor<PluginConfig> configMonitor,
        IAnalyticsService analytics)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _dispatcher = dispatcher;
        _matchTracker = matchTracker;
        _statsPersistence = statsPersistence;
        _statsRepository = statsRepository;
        _scrimManager = scrimManager;
        _pauseService = pauseService;
        _roundBackup = roundBackup;
        _configMonitor = configMonitor;
        _analytics = analytics;
        _processors = processors;

        // Register all processors for event dispatching
        foreach (var processor in _processors)
        {
            processor.RegisterEvents(_dispatcher);
        }
    }

    public void RegisterEvents(BasePlugin plugin)
    {
        // Player Lifecycle (Direct handling for core logic)
        plugin.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        plugin.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);

        // Map events to dispatcher via lambdas to decouple handler logic
        plugin.RegisterEventHandler<EventPlayerDeath>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerHurt>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventWeaponFire>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBulletImpact>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventRoundMvp>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerAvengedTeammate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerSpawned>((e, i) => _dispatcher.Dispatch(e, i));

        plugin.RegisterEventHandler<EventPlayerBlind>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventHegrenadeDetonate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventFlashbangDetonate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventSmokegrenadeDetonate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventMolotovDetonate>((e, i) => _dispatcher.Dispatch(e, i));

        plugin.RegisterEventHandler<EventItemPurchase>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventItemPickup>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventItemEquip>((e, i) => _dispatcher.Dispatch(e, i));

        plugin.RegisterEventHandler<EventBombBeginplant>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombAbortplant>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombPlanted>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombDefused>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombExploded>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombDropped>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombPickup>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombBegindefuse>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombAbortdefuse>((e, i) => _dispatcher.Dispatch(e, i));

        // Round events (Hybrid handling)
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);

        // Commands
        plugin.AddCommand("css_stats", "Show current stats", (player, info) => { if (ValidatePlayer(player)) OnStatsCommand(player!, info); });
        plugin.AddCommand("css_scrim", "Scrim system admin command", (player, info) => { if (ValidatePlayer(player)) OnScrimCommand(player!, info); });
        plugin.AddCommand("css_ready", "Player ready command", (player, info) => { if (ValidatePlayer(player)) OnReadyCommand(player!, info); });
        plugin.AddCommand("css_unready", "Player unready command", (player, info) => { if (ValidatePlayer(player)) OnUnreadyCommand(player!, info); });
        plugin.AddCommand("css_vote", "Player vote command", (player, info) => { if (ValidatePlayer(player)) OnVoteCommand(player!, info); });
        plugin.AddCommand("css_pick", "Captain pick command", (player, info) => { if (ValidatePlayer(player)) OnPickCommand(player!, info); });
        plugin.AddCommand("css_ct", "Select CT side after knife round", (player, info) => { if (ValidatePlayer(player)) OnCtCommand(player!, info); });
        plugin.AddCommand("css_pause", "Pause the match", (player, info) => { if (ValidatePlayer(player)) OnPauseCommand(player, info); });
        plugin.AddCommand("css_unpause", "Unpause the match", (player, info) => { if (ValidatePlayer(player)) OnUnpauseCommand(player, info); });
        plugin.AddCommand("css_restore", "Restore a previous round", (player, info) => { if (ValidatePlayer(player)) OnRestoreCommand(player!, info); });
        plugin.AddCommand("css_t", "Select T side", (player, info) => { if (ValidatePlayer(player)) OnTCommand(player!, info); });
    }

    private bool ValidatePlayer(CCSPlayerController? player)
    {
        if (player == null) return true; // Console is allowed
        if (!player.IsValid) return false;
        // In CS2, a valid SteamID is required for persistent stats. 0 usually indicates a bot or unauthenticated player.
        if (player.SteamID == 0) return false;
        return true;
    }

    #region Event Handlers
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
            SafeExecute(() => SavePlayerStatsOnDisconnectAsync(player), "SaveStatsOnDisconnect");
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
                stats.TotalSpawns++;
                stats.PlaytimeSeconds = (int)(DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
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
                if (team == 2) stats.TRounds++;
                else if (team == 3) stats.CtRounds++;
            });
        }
        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        _logger.LogInformation("Match start announced. Live tracking enabled.");
        _currentRoundNumber = 1;
        _matchTracker.StartMatchAsync(Server.MapName).ConfigureAwait(false);
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;
        
        _currentRoundNumber = @event.GetIntValue("round_number", 0);
        var roundStartUtc = DateTime.UtcNow;

        var ctAlive = 0;
        var tAlive = 0;
        _playerSessions.ForEachPlayer(stats =>
        {
            if (stats.CurrentTeam == PlayerTeam.CounterTerrorist) ctAlive++;
            else if (stats.CurrentTeam == PlayerTeam.Terrorist) tAlive++;
        });

        var context = new RoundContext(_currentRoundNumber, roundStartUtc, ctAlive, tAlive);
        foreach (var processor in _processors)
        {
            processor.OnRoundStart(context);
        }

        // Snapshot at round start for backup/restore (captures starting money and scores)
        _roundBackup.CreateSnapshot(_currentRoundNumber);

        if (_matchTracker.CurrentMatch != null)
        {
            SafeExecute(() => _matchTracker.StartRoundAsync(_matchTracker.CurrentMatch.MatchId, _currentRoundNumber), "StartRoundTracking");
        }

        _playerSessions.ForEachPlayer(stats =>
        {
            stats.RoundNumber = _currentRoundNumber;
            stats.RoundStartUtc = roundStartUtc;
            stats.AliveOnTeamAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;
            stats.AliveEnemyAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
            
            var player = Utilities.GetPlayerFromSteamId(stats.SteamId);
            if (player is { IsValid: true, InGameMoneyServices: not null })
            {
                stats.RoundStartMoney = player.InGameMoneyServices.Account;
                stats.EquipmentValueStart = stats.EquipmentValue;
            }
            
            stats.ResetRoundStats();
        });

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;

        Instrumentation.RoundsPlayedCounter.Add(1);
        var winningTeamInt = @event.GetIntValue("winner", 0);
        var winningTeam = winningTeamInt switch { 2 => PlayerTeam.Terrorist, 3 => PlayerTeam.CounterTerrorist, _ => PlayerTeam.Spectator };

        if (_scrimManager.CurrentState == ScrimState.KnifeRound)
        {
            _scrimManager.HandleKnifeRoundEnd(winningTeamInt);
            return HookResult.Continue;
        }

        _pauseService.OnRoundEnd();
        var winReason = @event.GetIntValue("reason", 0);

        foreach (var processor in _processors)
        {
            processor.OnRoundEnd(winningTeamInt, winReason);
        }

        _playerSessions.ForEachPlayer(stats =>
        {
            stats.RoundsPlayed++;
            if (winningTeam != PlayerTeam.Spectator && stats.CurrentTeam == winningTeam) stats.RoundsWon++;
            if (stats.HadKillThisRound || stats.HadAssistThisRound || stats.SurvivedThisRound || stats.DidTradeThisRound) stats.KASTRounds++;
            
            var player = Utilities.GetPlayerFromSteamId(stats.SteamId);
            if (player is { IsValid: true, InGameMoneyServices: not null })
            {
                stats.RoundEndMoney = player.InGameMoneyServices.Account;
                stats.EquipmentValueEnd = stats.EquipmentValue;
            }
        });
        
        if (_matchTracker.CurrentRoundId != null)
        {
            SafeExecute(() => _matchTracker.EndRoundAsync(_matchTracker.CurrentRoundId.Value, winningTeamInt, winReason), "EndRoundTracking");
        }

        SafeExecute(SaveStatsAtRoundEndAsync, "SaveStatsAtRoundEnd");
        return HookResult.Continue;
    }

    private async Task SaveStatsAtRoundEndAsync()
    {
        var matchId = _matchTracker.CurrentMatch?.MatchId;
        var snapshots = _playerSessions.CaptureSnapshots(true, matchId);
        if (snapshots.Length > 0) await _statsPersistence.EnqueueAsync(snapshots, default);
    }
    #endregion

    #region Command Handlers
    private void OnStatsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null)
        {
            _logger.LogInformation("statsCollector: {Count} active players", _playerSessions.GetActiveSteamIds().Count);
            return;
        }

        if (_playerSessions.TryGetPlayer(player.SteamID, out var stats))
        {
            var adr = _analytics.CalculateADR(stats);
            var rating = _analytics.CalculateHLTVRating(stats);
            var perfScore = _analytics.CalculatePerformanceScore(stats);
            var rank = _analytics.GetPlayerRank(perfScore);
            
            player.PrintToChat($" [statsCollector] K:{stats.Kills} D:{stats.Deaths} A:{stats.Assists} ADR:{adr:F0} Rating:{rating:F2} Rank:{rank}");
        }
    }

    private void SafeExecute(Func<Task> action, string operationName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var activity = Instrumentation.ActivitySource.StartActivity(operationName);
                await action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during async operation: {Operation}", operationName);
            }
        });
    }

    private void OnScrimCommand(CCSPlayerController? player, CommandInfo command)
    {
        var subCommand = command.GetArg(1).ToLower();
        // Admin commands
        if (subCommand is "start" or "stop" or "setcaptain" or "set" or "recover" or "practice" or "veto")
        {
            if (player != null && !AdminUtils.HasPermission(player, "@css/root", "@css/admin", "@css/scrimadmin"))
            {
                player.PrintToChat(" [Scrim] You do not have permission to use this command.");
                return;
            }

            switch (subCommand)
            {
                case "start": SafeExecute(() => _scrimManager.StartScrimAsync(), "StartScrim"); break;
                case "stop": SafeExecute(() => _scrimManager.StopScrimAsync(), "StopScrim"); break;
                case "recover": SafeExecute(() => _scrimManager.RecoverAsync(), "RecoverScrim"); break;
                case "practice":
                    if (command.ArgCount < 3) return;
                    var enable = command.GetArg(2).ToLower() == "on";
                    SafeExecute(() => _scrimManager.SetPracticeModeAsync(enable), "SetPracticeMode");
                    break;
                case "veto":
                    SafeExecute(() => _scrimManager.StartVetoAsync(), "StartVeto");
                    break;
                case "setcaptain":
                    if (command.ArgCount < 4) return;
                    var team = int.Parse(command.GetArg(2));
                    var target = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(3)));
                    if (target != null) SafeExecute(() => _scrimManager.SetCaptainAsync(team, target.SteamID), "SetCaptain");
                    break;
                case "set":
                    if (command.ArgCount < 4) return;
                    _scrimManager.SetOverride(command.GetArg(2), command.GetArg(3));
                    break;
            }
            return;
        }

        if (player == null) return;

        switch (subCommand)
        {
            case "ready": SafeExecute(() => _scrimManager.SetReadyAsync(player.SteamID, true), "SetReady"); break;
            case "unready": SafeExecute(() => _scrimManager.SetReadyAsync(player.SteamID, false), "SetUnready"); break;
            case "vote":
                if (command.ArgCount < 3) return;
                SafeExecute(() => _scrimManager.VoteMapAsync(player.SteamID, command.GetArg(2)), "VoteMap");
                break;
            case "pick":
                if (command.ArgCount < 3) return;
                var pickTarget = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(2)));
                if (pickTarget != null) SafeExecute(() => _scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID), "PickPlayer");
                break;
            case "ct": SafeExecute(() => _scrimManager.SelectSideAsync(player.SteamID, "ct"), "SelectSideCT"); break;
            case "t": SafeExecute(() => _scrimManager.SelectSideAsync(player.SteamID, "t"), "SelectSideT"); break;
        }
    }

    private void OnReadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        SafeExecute(() => _scrimManager.SetReadyAsync(player.SteamID, true), "ReadyCommand");
    }

    private void OnUnreadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        SafeExecute(() => _scrimManager.SetReadyAsync(player.SteamID, false), "UnreadyCommand");
    }

    private void OnVoteCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        SafeExecute(() => _scrimManager.VoteMapAsync(player.SteamID, command.GetArg(1)), "VoteCommand");
    }

    private void OnPickCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        var pickTarget = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(1)));
        if (pickTarget != null) SafeExecute(() => _scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID), "PickCommand");
    }

    private void OnCtCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        SafeExecute(() => _scrimManager.SelectSideAsync(player.SteamID, "ct"), "CtCommand");
    }

    private void OnTCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        SafeExecute(() => _scrimManager.SelectSideAsync(player.SteamID, "t"), "TCommand");
    }

    private void OnPauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        var type = PauseType.Tactical;
        if (command.ArgCount > 1)
        {
            var arg = command.GetArg(1).ToLower();
            type = arg switch
            {
                "tech" or "technical" => PauseType.Technical,
                "admin" => PauseType.Admin,
                _ => PauseType.Tactical
            };
        }

        if (type == PauseType.Admin && player != null && !AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            player.PrintToChat(" [Scrim] You do not have permission for admin pauses.");
            return;
        }

        SafeExecute(() => _pauseService.RequestPauseAsync(player, type), "PauseCommand");
    }

    private void OnUnpauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        SafeExecute(() => _pauseService.RequestUnpauseAsync(player), "UnpauseCommand");
    }

    private void OnRestoreCommand(CCSPlayerController player, CommandInfo command)
    {
        if (!AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            player.PrintToChat(" [statsCollector] You do not have permission to restore rounds.");
            return;
        }

        if (command.ArgCount < 2)
        {
            var rounds = string.Join(", ", _roundBackup.GetAvailableRounds());
            player.PrintToChat($" [statsCollector] Usage: .restore <round>. Available rounds: {rounds}");
            return;
        }

        if (int.TryParse(command.GetArg(1), out var roundNumber))
        {
            if (_roundBackup.RestoreRound(roundNumber))
            {
                player.PrintToChat($" [statsCollector] Round {roundNumber} restore initiated.");
            }
            else
            {
                player.PrintToChat($" [statsCollector] Failed to restore round {roundNumber}.");
            }
        }
    }
    #endregion
}
