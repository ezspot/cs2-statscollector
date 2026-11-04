using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
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
    // Per-match engine counters (scoreboard score, cash spent) per player at the last observation: the previous
    // round_end, or this round_start when there is none. Each round_end adds the change since then to the
    // lifetime totals, so points scored after a round ended (exit frags) count in the next round.
    private readonly Dictionary<ulong, (int Score, int CashSpent)> _lastTotals = [];
    private int _lastTotalsRound;

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
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnMapEnd>(GameRulesState.Reset);
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventRoundOfficiallyEnded>(OnRoundOfficiallyEnded);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);
        plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
    }

    private void OnMapStart(string mapName)
    {
        GameRulesState.Reset();
        // A map change ends the running match (vote, admin changelevel): write its per-match rows now, or they are
        // lost and its baseline is applied to the next match.
        _matchStats.FinalizeMatch();
        _roundBackup.Reset();
        _matchTracker.StartMatch(mapName);
        _scrimManager.HandleMapStart(mapName);
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        // round_officially_ended does not follow the final round, so close it here.
        if (_matchTracker.CurrentMatch?.MatchUuid is { } matchUuid)
            _persistenceChannel.TryWrite(new RoundOfficialEndUpdate(matchUuid, _currentRoundNumber));
        _matchStats.FinalizeMatch();
        _matchTracker.EndMatch();
        _roundBackup.Reset();
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
        // This fires at round 1's freeze end, after round_start already recorded round 1 (and its backup
        // point) under the previous match.
        _matchTracker.StartRound(1);
        // Baseline first: the round 1 backup point keeps a copy of it for a later restore.
        _matchStats.CaptureBaseline();
        _roundBackup.CreateSnapshot(1);
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode || GameRulesState.IsWarmup) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        _damageReport.ResetRound();
        var previousRound = _currentRoundNumber;
        _currentRoundNumber = GetCurrentRoundNumber();
        var restored = _roundBackup.ApplyRestore(_currentRoundNumber, _currentRoundNumber <= previousRound);
        // Only the next round of the same match continues from the previous round_end: a restart, new map or
        // round restore resets the engine counters (round 1 always follows one of the first two).
        if (restored || _currentRoundNumber == 1 || _currentRoundNumber != _lastTotalsRound + 1) _lastTotals.Clear();

        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            var player = Utilities.GetPlayerFromSteamId64(steamId);
            var state = PlayerControllerState.From(player);
            if (player is { IsValid: true })
                _lastTotals.TryAdd(steamId, (player.Score, player.InGameMoneyServices?.TotalCashSpent ?? 0));
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
        if (_config.DeathmatchMode || GameRulesState.IsWarmup) return HookResult.Continue;
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
            var player = Utilities.GetPlayerFromSteamId64(steamId);
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

        var winReason = @event.GetIntValue("reason", 0);
        // Game Commencing ends the round a lone first player was in, before warmup starts; nobody played it.
        if (GameRulesState.IsWarmup || winReason == (int)RoundEndReason.GameCommencing) return HookResult.Continue;

        _pauseService.OnRoundEnd();

        foreach (var processor in _processors)
        {
            processor.OnRoundEnd(winningTeamInt, winReason);
        }

        _damageReport.ReportToPlayers();

        _playerSessions.ForEachPlayer(stats =>
        {
            // Spectators, casters and coaches have sessions too, but they did not play the round.
            if (stats.CurrentTeam is not (PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)) return;

            stats.Round.RoundsPlayed++;
            if (winningTeam != PlayerTeam.Spectator && stats.CurrentTeam == winningTeam) stats.Round.RoundsWon++;
            if (stats.CurrentTeam == PlayerTeam.CounterTerrorist) stats.Round.CtRounds++;
            else if (stats.CurrentTeam == PlayerTeam.Terrorist) stats.Round.TRounds++;
            if (stats.Round.HadKillThisRound || stats.Round.HadAssistThisRound || stats.Round.SurvivedThisRound || stats.Round.WasTradedThisRound) stats.Round.KASTRounds++;

            var player = Utilities.GetPlayerFromSteamId64(stats.SteamId);
            if (player is { IsValid: true })
            {
                // Deltas of the engine's per-match counters: includes buys after freeze end, and is not thrown
                // off by values seeded from the database (the old freeze-end money diff was both).
                var cashSpent = player.InGameMoneyServices?.TotalCashSpent ?? 0;
                var observed = _lastTotals.TryGetValue(stats.SteamId, out var last);
                _lastTotals[stats.SteamId] = (player.Score, cashSpent);
                if (observed)
                {
                    stats.Combat.Score += player.Score - last.Score;
                    stats.Economy.MoneySpent += Math.Max(0, cashSpent - last.CashSpent);
                }

                if (player.InGameMoneyServices is not null)
                {
                    var equipmentValue = player.PlayerPawn.Value?.CurrentEquipmentValue ?? 0;
                    stats.Economy.RoundEndMoney = player.InGameMoneyServices.Account;
                    stats.Economy.EquipmentValue = equipmentValue;
                    stats.Economy.EquipmentValueEnd = equipmentValue;
                }
            }
        });

        _lastTotalsRound = _currentRoundNumber;
        _matchTracker.EndRound(_currentRoundNumber, winningTeamInt, winReason);

        SaveStatsAtRoundEnd();
        return HookResult.Continue;
    }

    private HookResult OnRoundOfficiallyEnded(EventRoundOfficiallyEnded @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode || GameRulesState.IsWarmup) return HookResult.Continue;
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

    private static int GetCurrentRoundNumber() => GameRulesState.TotalRoundsPlayed + 1;
}
