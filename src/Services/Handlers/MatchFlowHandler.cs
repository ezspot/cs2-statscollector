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
        IDamageReportService damageReport)
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
    }

    public void Register(BasePlugin plugin)
    {
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);
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
}
