using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;
using statsCollector.Services;

namespace statsCollector.Services.Handlers;

public sealed class MatchFlowHandler : IGameHandler
{
    private readonly ILogger<MatchFlowHandler> _logger;
    private readonly IEnumerable<IEventProcessor> _processors;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IMatchLifecyclePersistenceService _matchLifecyclePersistence;
    private readonly IStatsPersistenceService _statsPersistence;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IScrimManager _scrimManager;
    private readonly IPauseService _pauseService;
    private readonly IRoundBackupService _roundBackup;
    private readonly IOptionsMonitor<PluginConfig> _configMonitor;
    private readonly IDamageReportService _damageReport;
    private readonly ITaskTracker _taskTracker;
    
    private PluginConfig _config => _configMonitor.CurrentValue;
    private int _currentRoundNumber = 0;

    public MatchFlowHandler(
        ILogger<MatchFlowHandler> logger,
        IEnumerable<IEventProcessor> processors,
        IMatchTrackingService matchTracker,
        IMatchLifecyclePersistenceService matchLifecyclePersistence,
        IStatsPersistenceService statsPersistence,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IPauseService pauseService,
        IRoundBackupService roundBackup,
        IOptionsMonitor<PluginConfig> configMonitor,
        IDamageReportService damageReport,
        ITaskTracker taskTracker)
    {
        _logger = logger;
        _processors = processors;
        _matchTracker = matchTracker;
        _matchLifecyclePersistence = matchLifecyclePersistence;
        _statsPersistence = statsPersistence;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _pauseService = pauseService;
        _roundBackup = roundBackup;
        _configMonitor = configMonitor;
        _damageReport = damageReport;
        _taskTracker = taskTracker;
    }

    public void Register(BasePlugin plugin)
    {
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);
    }

    private HookResult OnRoundAnnounceMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        _logger.LogInformation("Match start announced. Live tracking enabled.");
        _currentRoundNumber = 1;
        _taskTracker.Track("StartMatch", _matchTracker.StartMatchAsync(Server.MapName));
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;
        
        _damageReport.ResetRound();
        _currentRoundNumber = @event.GetIntValue("round_number", 0);
        
        // We use RoundFreezeEnd for actual start timing, but reset stats here
        _playerSessions.ForEachPlayer(stats =>
        {
            stats.ResetRoundStats();
        });

        _roundBackup.CreateSnapshot(_currentRoundNumber);

        if (_matchTracker.CurrentMatch != null)
        {
            var matchId = _matchTracker.CurrentMatch.MatchId;
            var roundNumber = _currentRoundNumber;
            _taskTracker.Track("EnqueueStartRound", _matchLifecyclePersistence.EnqueueStartRoundAsync(matchId, roundNumber));
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;

        var roundStartUtc = DateTime.UtcNow;
        var ctAlive = 0;
        var tAlive = 0;
        
        // Capture player states on the game thread
        var playerStates = new List<PlayerControllerState>();
        _playerSessions.GetActiveSteamIds().ToList().ForEach(steamId => 
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player is { IsValid: true })
            {
                var state = PlayerControllerState.From(player);
                playerStates.Add(state);
                if (state.Team == PlayerTeam.CounterTerrorist) ctAlive++;
                else if (state.Team == PlayerTeam.Terrorist) tAlive++;
            }
        });

        var context = new RoundContext(_currentRoundNumber, roundStartUtc, ctAlive, tAlive);
        foreach (var processor in _processors)
        {
            processor.OnRoundStart(context);
        }

        foreach (var state in playerStates)
        {
            _playerSessions.MutatePlayer(state.SteamId, stats => 
            {
                stats.Round.AliveOnTeamAtRoundStart = state.Team == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;
                stats.Round.AliveEnemyAtRoundStart = state.Team == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                stats.Economy.RoundStartMoney = state.Money;
                stats.Economy.EquipmentValueStart = stats.Economy.EquipmentValue;
            });
        }

        _logger.LogInformation("Round {Round} Freeze End. Timing started.", _currentRoundNumber);
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
            var roundId = _matchTracker.CurrentRoundId.Value;
            _taskTracker.Track("EnqueueEndRound", _matchLifecyclePersistence.EnqueueEndRoundAsync(roundId, winningTeamInt, winReason));
        }

        _taskTracker.Track("SaveStatsAtRoundEnd", SaveStatsAtRoundEndAsync());
        return HookResult.Continue;
    }

    private async Task SaveStatsAtRoundEndAsync()
    {
        var match = _matchTracker.CurrentMatch;
        var snapshots = _playerSessions.CaptureSnapshots(true, match?.MatchId, match?.MatchUuid);
        if (snapshots.Length > 0) await _statsPersistence.EnqueueAsync(snapshots, default);
    }
}
