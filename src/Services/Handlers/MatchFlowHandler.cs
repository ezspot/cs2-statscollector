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
    private readonly IPlayerSessionService _playerSessions;
    private readonly IScrimManager _scrimManager;
    private readonly IPauseService _pauseService;
    private readonly IRoundBackupService _roundBackup;
    private readonly IOptionsMonitor<PluginConfig> _configMonitor;
    private readonly IDamageReportService _damageReport;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IMatchStatsService _matchStats;

    private PluginConfig _config => _configMonitor.CurrentValue;
    private int _currentRoundNumber = 0;

    public MatchFlowHandler(
        ILogger<MatchFlowHandler> logger,
        IEnumerable<IEventProcessor> processors,
        IMatchTrackingService matchTracker,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IPauseService pauseService,
        IRoundBackupService roundBackup,
        IOptionsMonitor<PluginConfig> configMonitor,
        IDamageReportService damageReport,
        IPersistenceChannel persistenceChannel,
        IMatchStatsService matchStats)
    {
        _logger = logger;
        _processors = processors;
        _matchTracker = matchTracker;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _pauseService = pauseService;
        _roundBackup = roundBackup;
        _configMonitor = configMonitor;
        _damageReport = damageReport;
        _persistenceChannel = persistenceChannel;
        _matchStats = matchStats;
    }

    public void Register(BasePlugin plugin)
    {
        _roundBackup.Initialize();
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventRoundOfficiallyEnded>(OnRoundOfficiallyEnded);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);
        plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        _matchStats.FinalizeMatch();
        _matchTracker.EndMatch();
        if (_config.EnableScrim) _scrimManager.HandleMatchEnd();
        _logger.LogInformation("Match ended; per-match statistics finalized.");
        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        if (_config.EnableScrim || _matchTracker.CurrentMatch is null)
        {
            _matchTracker.StartMatch(Server.MapName);
        }
        _roundBackup.Reset();
        _pauseService.Reset();
        _logger.LogInformation("Match start announced. Live tracking enabled.");
        _currentRoundNumber = 1;
        _matchStats.CaptureBaseline();
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        _damageReport.ResetRound();
        _currentRoundNumber = GetCurrentRoundNumber();

        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            var state = PlayerControllerState.From(player);
            _playerSessions.MutatePlayer(steamId, stats =>
            {
                stats.ResetRoundStats();
                stats.CurrentTeam = state.Team;
                stats.Economy.RoundStartMoney = state.Money;
            });
        }

        _roundBackup.CreateSnapshot(_currentRoundNumber);
        _matchTracker.StartRound(_currentRoundNumber);

        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live)
        {
            _logger.LogDebug("Skipping RoundFreezeEnd logic: scrim enabled but not Live.");
            return HookResult.Continue;
        }

        var roundStartUtc = DateTime.UtcNow;
        var ctAlive = 0;
        var tAlive = 0;

        var playerStates = new List<PlayerControllerState>();
        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player is { IsValid: true })
            {
                var state = PlayerControllerState.From(player);
                playerStates.Add(state);
                if (state.Team == PlayerTeam.CounterTerrorist) ctAlive++;
                else if (state.Team == PlayerTeam.Terrorist) tAlive++;
            }
        }

        var context = new RoundContext(_currentRoundNumber, roundStartUtc, ctAlive, tAlive);
        foreach (var processor in _processors)
        {
            processor.OnRoundStart(context);
        }

        foreach (var state in playerStates)
        {
            _playerSessions.MutatePlayer(state.SteamId, stats =>
            {
                stats.Round.SurvivedThisRound = true;
                stats.Round.RoundNumber = _currentRoundNumber;
                stats.Round.RoundStartUtc = roundStartUtc;
                stats.Round.AliveOnTeamAtRoundStart = state.Team == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;
                stats.Round.AliveEnemyAtRoundStart = state.Team == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                stats.Economy.MoneySpent += Math.Max(0, stats.Economy.RoundStartMoney - state.Money);
                stats.Economy.EquipmentValue = state.EquipmentValue;
                stats.Economy.EquipmentValueStart = state.EquipmentValue;
            });
        }

        _logger.LogInformation("Round {Round} Freeze End. Timing started.", _currentRoundNumber);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;

        var winningTeamInt = @event.GetIntValue("winner", 0);
        var winningTeam = winningTeamInt switch { 2 => PlayerTeam.Terrorist, 3 => PlayerTeam.CounterTerrorist, _ => PlayerTeam.Spectator };

        if (_scrimManager.CurrentState == ScrimState.KnifeRound)
        {
            _ = _scrimManager.HandleKnifeRoundEnd(winningTeamInt);
            return HookResult.Continue;
        }

        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

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
            if (stats.CurrentTeam == PlayerTeam.CounterTerrorist) stats.Round.CtRounds++;
            else if (stats.CurrentTeam == PlayerTeam.Terrorist) stats.Round.TRounds++;
            if (stats.Round.HadKillThisRound || stats.Round.HadAssistThisRound || stats.Round.SurvivedThisRound || stats.Round.WasTradedThisRound) stats.Round.KASTRounds++;

            var player = Utilities.GetPlayerFromSteamId(stats.SteamId);
            if (player is { IsValid: true })
            {
                stats.Combat.Score = player.Score;

                if (player.InGameMoneyServices is not null)
                {
                    var equipmentValue = player.PlayerPawn.Value?.CurrentEquipmentValue ?? 0;
                    stats.Economy.RoundEndMoney = player.InGameMoneyServices.Account;
                    stats.Economy.EquipmentValue = equipmentValue;
                    stats.Economy.EquipmentValueEnd = equipmentValue;
                }
            }
        });

        _matchTracker.EndRound(_currentRoundNumber, winningTeamInt, winReason);

        SaveStatsAtRoundEnd();
        return HookResult.Continue;
    }

    private HookResult OnRoundOfficiallyEnded(EventRoundOfficiallyEnded @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
        if (!string.IsNullOrWhiteSpace(matchUuid))
            _persistenceChannel.TryWrite(new RoundOfficialEndUpdate(matchUuid, _currentRoundNumber));
        SaveStatsAtRoundEnd();
        return HookResult.Continue;
    }

    private void SaveStatsAtRoundEnd()
    {
        var match = _matchTracker.CurrentMatch;
        var snapshots = _playerSessions.CaptureSnapshots(true, match?.MatchUuid);

        foreach (var snapshot in snapshots)
        {
            if (_persistenceChannel.TryWrite(new PlayerStatsUpdate(snapshot)))
            {
                _playerSessions.MarkPersisted(snapshot.SteamId, snapshot.Version);
            }
        }
    }

    private static int GetCurrentRoundNumber()
    {
        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
        return Math.Max(1, (gameRules?.TotalRoundsPlayed ?? 0) + 1);
    }
}
