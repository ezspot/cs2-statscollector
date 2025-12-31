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
    private readonly IMatchLifecyclePersistenceService _matchLifecyclePersistence;
    private readonly IStatsPersistenceService _statsPersistence;
    private readonly IStatsRepository _statsRepository;
    private readonly IScrimManager _scrimManager;
    private readonly IPauseService _pauseService;
    private readonly IRoundBackupService _roundBackup;
    private readonly IOptionsMonitor<PluginConfig> _configMonitor;
    private readonly IAnalyticsService _analytics;
    private readonly IDamageReportService _damageReport;
    private readonly IMatchReadyService _matchReady;
    private int _currentRoundNumber = 0;

    private PluginConfig _config => _configMonitor.CurrentValue;

    public GameEventHandlerService(
        ILogger<GameEventHandlerService> logger,
        IPlayerSessionService playerSessions,
        IEventDispatcher dispatcher,
        IEnumerable<IEventProcessor> processors,
        IMatchTrackingService matchTracker,
        IMatchLifecyclePersistenceService matchLifecyclePersistence,
        IStatsPersistenceService statsPersistence,
        IStatsRepository statsRepository,
        IScrimManager scrimManager,
        IPauseService pauseService,
        IRoundBackupService roundBackup,
        IOptionsMonitor<PluginConfig> configMonitor,
        IAnalyticsService analytics,
        IDamageReportService damageReport,
        IMatchReadyService matchReady)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _dispatcher = dispatcher;
        _matchTracker = matchTracker;
        _matchLifecyclePersistence = matchLifecyclePersistence;
        _statsPersistence = statsPersistence;
        _statsRepository = statsRepository;
        _scrimManager = scrimManager;
        _pauseService = pauseService;
        _roundBackup = roundBackup;
        _configMonitor = configMonitor;
        _analytics = analytics;
        _processors = processors;
        _damageReport = damageReport;
        _matchReady = matchReady;

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

        // Movement and Communication
        plugin.RegisterEventHandler<EventPlayerFootstep>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerPing>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerJump>((e, i) => _dispatcher.Dispatch(e, i));

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
        
        _damageReport.ResetRound();
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
            _ = _matchLifecyclePersistence.EnqueueStartRoundAsync(_matchTracker.CurrentMatch.MatchId, _currentRoundNumber);
        }

        _playerSessions.ForEachPlayer(stats =>
        {
            stats.Round.AliveOnTeamAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;
            stats.Round.AliveEnemyAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
            
            var player = Utilities.GetPlayerFromSteamId(stats.SteamId);
            if (player is { IsValid: true, InGameMoneyServices: not null })
            {
                stats.Economy.RoundStartMoney = player.InGameMoneyServices.Account;
                stats.Economy.EquipmentValueStart = stats.Economy.EquipmentValue;
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

        _damageReport.ReportToPlayers();

        _playerSessions.ForEachPlayer(stats =>
        {
            stats.Round.RoundsPlayed++;
            if (winningTeam != PlayerTeam.Spectator && stats.CurrentTeam == winningTeam) stats.Round.RoundsWon++;
            if (stats.Round.HadKillThisRound || stats.Round.HadAssistThisRound || stats.Round.SurvivedThisRound || stats.Round.DidTradeThisRound) stats.Round.KASTRounds++;
            
            var player = Utilities.GetPlayerFromSteamId(stats.SteamId);
            if (player is { IsValid: true, InGameMoneyServices: not null })
            {
                stats.Economy.RoundEndMoney = player.InGameMoneyServices.Account;
                stats.Economy.EquipmentValueEnd = stats.Economy.EquipmentValue;
            }
        });
        
        if (_matchTracker.CurrentRoundId != null)
        {
            _ = _matchLifecyclePersistence.EnqueueEndRoundAsync(_matchTracker.CurrentRoundId.Value, winningTeamInt, winReason);
        }

        _ = SaveStatsAtRoundEndAsync();
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
            
            player.PrintToChat($" [statsCollector] K:{stats.Combat.Kills} D:{stats.Combat.Deaths} A:{stats.Combat.Assists} ADR:{adr:F0} Rating:{rating:F2} Rank:{rank}");
        }
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
                case "start": _ = _scrimManager.StartScrimAsync(); break;
                case "stop": _ = _scrimManager.StopScrimAsync(); break;
                case "recover": _ = _scrimManager.RecoverAsync(); break;
                case "practice":
                    if (command.ArgCount < 3) return;
                    var enable = command.GetArg(2).ToLower() == "on";
                    _ = _scrimManager.SetPracticeModeAsync(enable);
                    break;
                case "veto":
                    _ = _scrimManager.StartVetoAsync();
                    break;
                case "setcaptain":
                    if (command.ArgCount < 4) return;
                    var team = int.Parse(command.GetArg(2));
                    var target = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(3)));
                    if (target != null) _ = _scrimManager.SetCaptainAsync(team, target.SteamID);
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
            case "ready": _ = _scrimManager.SetReadyAsync(player.SteamID, true); break;
            case "unready": _ = _scrimManager.SetReadyAsync(player.SteamID, false); break;
            case "vote":
                if (command.ArgCount < 3) return;
                _ = _scrimManager.VoteMapAsync(player.SteamID, command.GetArg(2));
                break;
            case "pick":
                if (command.ArgCount < 3) return;
                var pickTarget = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(2)));
                if (pickTarget != null) _ = _scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID);
                break;
            case "ct": _ = _scrimManager.SelectSideAsync(player.SteamID, "ct"); break;
            case "t": _ = _scrimManager.SelectSideAsync(player.SteamID, "t"); break;
        }
    }

    private void OnReadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _matchReady.SetReady(player.SteamID, true);
        if (_matchReady.AreAllReady())
        {
            Server.PrintToChatAll($" {ChatColors.Green}[Ready]{ChatColors.Default} All players are ready! Match starting...");
            _ = _scrimManager.StartScrimAsync();
        }
    }

    private void OnUnreadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _matchReady.SetReady(player.SteamID, false);
    }

    private void OnVoteCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        _ = _scrimManager.VoteMapAsync(player.SteamID, command.GetArg(1));
    }

    private void OnPickCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        var pickTarget = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(1)));
        if (pickTarget != null) _ = _scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID);
    }

    private void OnCtCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _ = _scrimManager.SelectSideAsync(player.SteamID, "ct");
    }

    private void OnTCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _ = _scrimManager.SelectSideAsync(player.SteamID, "t");
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

        _ = _pauseService.RequestPauseAsync(player, type);
    }

    private void OnUnpauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        _ = _pauseService.RequestUnpauseAsync(player);
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
