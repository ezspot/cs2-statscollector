using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
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
    private readonly PluginConfig _config;
    private readonly IDamageReportService _damageReport;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IMatchStatsService _matchStats;
    private readonly IBombEventProcessor _bombProcessor;

    private int _currentRoundNumber = 0;
    // A round_end was seen for the current round, and one for the current match. A restart (mp_restartgame, the end of
    // warmup) fires round_officially_ended with no round_end and then resets every engine counter.
    private bool _roundEndSeen;
    private bool _matchHadRoundEnd;
    // The engine's per-match counters per player at the last observation: the previous round_end or official round
    // end, or this round_start when there is none. Each observation adds the change since the previous one to the
    // lifetime totals, so points or money awarded after a round ended (exit frags, round-end payouts) count in that
    // round at its official end.
    private readonly Dictionary<ulong, EngineMatchStats> _lastTotals = [];
    // The same counters when the match started (a late joiner's: when first seen), for the match-end cross-check.
    private readonly Dictionary<ulong, EngineMatchStats> _matchStartTotals = [];
    private int _lastTotalsRound;
    // Both sides' buy of the round whose freeze end was seen last.
    private (int Round, RoundEconomy Economy)? _roundEconomy;

    public MatchFlowHandler(
        ILogger<MatchFlowHandler> logger,
        IEnumerable<IEventProcessor> processors,
        IMatchTrackingService matchTracker,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IPauseService pauseService,
        IRoundBackupService roundBackup,
        PluginConfig config,
        IDamageReportService damageReport,
        IPersistenceChannel persistenceChannel,
        IMatchStatsService matchStats,
        IBombEventProcessor bombProcessor)
    {
        _logger = logger;
        _processors = processors;
        _matchTracker = matchTracker;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _pauseService = pauseService;
        _roundBackup = roundBackup;
        _config = config;
        _damageReport = damageReport;
        _persistenceChannel = persistenceChannel;
        _matchStats = matchStats;
        _bombProcessor = bombProcessor;
    }

    public void Register(BasePlugin plugin)
    {
        _roundBackup.Initialize();
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnMapEnd>(GameRulesState.Reset);
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundPoststart>(OnRoundPoststart);
        plugin.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventRoundOfficiallyEnded>(OnRoundOfficiallyEnded);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);
        plugin.RegisterEventHandler<EventAnnouncePhaseEnd>(OnAnnouncePhaseEnd);
        plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
    }

    // The halftime side swap (and each overtime half's). The same announcement comes when regulation ends in a tie and
    // the match goes to overtime, and at the end of the match, without a swap: the game rules say whether sides switch.
    private HookResult OnAnnouncePhaseEnd(EventAnnouncePhaseEnd @event, GameEventInfo info)
    {
        if (!GameRulesState.SwitchingTeamsAtRoundReset) return HookResult.Continue;
        if (!_config.DeathmatchMode && (!_config.EnableScrim || _scrimManager.CurrentState is ScrimState.Live))
            _pauseService.OnSidesSwapped();
        return HookResult.Continue;
    }

    private void OnMapStart(string mapName)
    {
        GameRulesState.Reset();
        // A map change ends the running match (vote, admin changelevel): write its per-match rows now, or they are
        // lost and its baseline is applied to the next match.
        _matchStats.FinalizeMatch();
        _roundBackup.Reset();
        _pauseService.Reset();
        _matchStartTotals.Clear();
        _roundEndSeen = false;
        _matchHadRoundEnd = false;
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
        // The final round's round-end awards land after its round_end, and no later observation picks them up. The
        // same reading, against the match start, is the engine's account of the match.
        var players = PlayerControllerState.HumansBySteamId();
        var engine = new Dictionary<ulong, EngineMatchStats>();
        _playerSessions.ForEachPlayer(stats =>
        {
            if (!players.TryGetValue(stats.SteamId, out var player)) return;
            engine[stats.SteamId] = Observe(stats, player) - _matchStartTotals.GetValueOrDefault(stats.SteamId);
        });
        // The final round's lifetime and round rows, with those awards.
        SaveStats(_currentRoundNumber);
        _matchStats.FinalizeMatch(engine);
        _matchTracker.EndMatch();
        _roundBackup.Reset();
        _pauseService.Reset();
        _matchStartTotals.Clear();
        _roundEndSeen = false;
        _matchHadRoundEnd = false;
        if (_config.EnableScrim) _scrimManager.HandleMatchEnd();
        _logger.LogInformation("Match ended; per-match statistics finalized.");
        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        // A restart after rounds were played (mp_restartgame) starts a new match: the engine's counters start over too.
        var restarted = _matchTracker.CurrentMatch is not null && _matchHadRoundEnd;
        if (restarted) _matchStats.FinalizeMatch();
        if (_config.EnableScrim || _matchTracker.CurrentMatch is null || restarted)
        {
            _matchTracker.StartMatch(Server.MapName);
        }
        _matchHadRoundEnd = false;
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
        // The engine fires round_freeze_end just before this, so round 1's buy was recorded under the previous match.
        if (_roundEconomy is { Round: 1 } roundOne) _matchTracker.RecordRoundEconomy(1, roundOne.Economy);
        _matchStartTotals.Clear();
        var players = PlayerControllerState.HumansBySteamId();
        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            if (!players.TryGetValue(steamId, out var player)) continue;
            var totals = ReadEngineStats(player);
            _matchStartTotals[steamId] = totals;
            // The counters were reset with the match: against the old observation, round 1's awards would clamp to nothing.
            _lastTotals[steamId] = totals;
        }
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode || GameRulesState.IsWarmup) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        _damageReport.ResetRound();
        var players = PlayerControllerState.HumansBySteamId();
        var previousRound = _currentRoundNumber;
        var nextRound = GetCurrentRoundNumber();
        // The engine adds the survivors' live time between the official end and this round_start (OnPreResetRound), so
        // the previous round's rows go out once more with it. Only for the round that follows it: a restore or a
        // restart voids or resets whatever a write here would carry.
        if (_matchTracker.CurrentMatch is not null && previousRound >= 1 && _roundEndSeen && nextRound == previousRound + 1)
        {
            _playerSessions.ForEachPlayer(stats => { if (players.TryGetValue(stats.SteamId, out var player)) Observe(stats, player); });
            SaveStats(previousRound);
        }
        _roundEndSeen = false;
        _currentRoundNumber = nextRound;
        var restored = _roundBackup.ApplyRestore(_currentRoundNumber, _currentRoundNumber <= previousRound);
        // Only the next round of the same match continues from the previous round_end: a restart, new map or
        // round restore resets the engine counters (round 1 always follows one of the first two).
        if (restored || _currentRoundNumber == 1 || _currentRoundNumber != _lastTotalsRound + 1) _lastTotals.Clear();

        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            players.TryGetValue(steamId, out var player);
            var state = PlayerControllerState.From(player);
            if (player is not null)
            {
                var totals = ReadEngineStats(player);
                _lastTotals.TryAdd(steamId, totals);
                // A late joiner's engine counters start here.
                _matchStartTotals.TryAdd(steamId, totals);
            }
            _playerSessions.MutatePlayer(steamId, stats =>
            {
                stats.ResetRoundStats();
                stats.CurrentTeam = state.Team;
                // Until round_poststart: the engine hands out a new half's start money after round_start.
                stats.Economy.RoundStartMoney = state.Money;
            });
        }

        _roundBackup.CreateSnapshot(_currentRoundNumber);
        _matchTracker.StartRound(_currentRoundNumber);

        return HookResult.Continue;
    }

    // The money the round starts with: for the second half and each overtime half the engine assigns the start money
    // between round_start and round_poststart (RestartRound: AssignStartingMoneyToAllPlayers).
    private HookResult OnRoundPoststart(EventRoundPoststart @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode || GameRulesState.IsWarmup) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        var players = PlayerControllerState.HumansBySteamId();
        _playerSessions.ForEachPlayer(stats =>
        {
            if (players.TryGetValue(stats.SteamId, out var player) && player.InGameMoneyServices is { } money)
                stats.Economy.RoundStartMoney = money.Account;
        });
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
        var isPistolRound = GameRulesState.IsPistolRound;
        var ctAlive = 0;
        var tAlive = 0;

        // Everyone on a side starts the round, bots included.
        var everyone = Utilities.GetPlayers();
        foreach (var player in everyone)
        {
            if (player.TeamNum == (int)PlayerTeam.CounterTerrorist) ctAlive++;
            else if (player.TeamNum == (int)PlayerTeam.Terrorist) tAlive++;
        }

        var context = new RoundContext(_currentRoundNumber, roundStartUtc, ctAlive, tAlive, isPistolRound);
        foreach (var processor in _processors)
        {
            processor.OnRoundStart(context);
        }

        // Both sides' buy: the money they started the round with and what they hold now that buying is over.
        // ponytail: the humans' only; a bot has no session to keep its start money in.
        var economy = (CtMoney: 0, TMoney: 0, CtEquipment: 0, TEquipment: 0);
        var players = PlayerControllerState.HumansBySteamId(everyone);
        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            if (!players.TryGetValue(steamId, out var player)) continue;
            var state = PlayerControllerState.From(player);
            var isCt = state.Team == PlayerTeam.CounterTerrorist;
            var isT = state.Team == PlayerTeam.Terrorist;
            if (isCt) economy.CtEquipment += state.EquipmentValue;
            else if (isT) economy.TEquipment += state.EquipmentValue;
            _playerSessions.MutatePlayer(state.SteamId, stats =>
            {
                stats.Round.SurvivedThisRound = true;
                stats.Round.RoundNumber = _currentRoundNumber;
                stats.Round.RoundStartUtc = roundStartUtc;
                stats.Round.IsPistolRound = isPistolRound;
                stats.Round.AliveOnTeamAtRoundStart = isCt ? ctAlive : tAlive;
                stats.Round.AliveEnemyAtRoundStart = isCt ? tAlive : ctAlive;
                stats.Economy.EquipmentValue = state.EquipmentValue;
                stats.Economy.EquipmentValueStart = state.EquipmentValue;
                if (isCt) economy.CtMoney += stats.Economy.RoundStartMoney;
                else if (isT) economy.TMoney += stats.Economy.RoundStartMoney;
            });
        }
        _roundEconomy = (_currentRoundNumber, new RoundEconomy(economy.CtMoney, economy.TMoney, economy.CtEquipment, economy.TEquipment));
        _matchTracker.RecordRoundEconomy(_currentRoundNumber, _roundEconomy.Value.Economy);

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
            // knife.cfg ends warmup a few frames after the state changes; a warmup round is not the knife round.
            if (!GameRulesState.IsWarmup) _ = _scrimManager.HandleKnifeRoundEnd(winningTeamInt);
            return HookResult.Continue;
        }

        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;

        var winReason = @event.GetIntValue("reason", 0);
        // Game Commencing ends the round a lone first player was in, before warmup starts; nobody played it.
        if (GameRulesState.IsWarmup || winReason == (int)RoundEndReason.GameCommencing) return HookResult.Continue;

        _roundEndSeen = true;
        _matchHadRoundEnd = true;
        _pauseService.OnRoundEnd();

        foreach (var processor in _processors)
        {
            processor.OnRoundEnd(winningTeamInt, winReason);
        }

        _damageReport.ReportToPlayers();

        var players = PlayerControllerState.HumansBySteamId();
        _playerSessions.ForEachPlayer(stats =>
        {
            // Spectators, casters and coaches have sessions too, but they did not play the round; neither did someone
            // who joined a team after the freeze end (which stamps the round number).
            if (stats.CurrentTeam is not (PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)) return;
            if (stats.Round.RoundNumber != _currentRoundNumber) return;

            var won = winningTeam != PlayerTeam.Spectator && stats.CurrentTeam == winningTeam;
            stats.Round.RoundsPlayed++;
            if (won) stats.Round.RoundsWon++;
            if (stats.CurrentTeam == PlayerTeam.CounterTerrorist)
            {
                stats.Round.CtRounds++;
                if (won) stats.Round.CtRoundsWon++;
            }
            else
            {
                stats.Round.TRounds++;
                if (won) stats.Round.TRoundsWon++;
            }
            if (stats.Round.IsPistolRound)
            {
                stats.Round.PistolRoundsPlayed++;
                if (won) stats.Round.PistolRoundsWon++;
            }
            if (stats.Round.SurvivedThisRound) stats.Round.RoundsSurvived++;
            if (stats.Round.WasTradedThisRound) stats.Round.RoundsTraded++;
            if (stats.Round.HadKillThisRound || stats.Round.HadAssistThisRound || stats.Round.SurvivedThisRound || stats.Round.WasTradedThisRound) stats.Round.KASTRounds++;

            if (players.TryGetValue(stats.SteamId, out var player))
            {
                Observe(stats, player);
                SampleRoundEnd(stats, player);
            }
        });

        _lastTotalsRound = _currentRoundNumber;
        _matchTracker.EndRound(_currentRoundNumber, winningTeamInt, winReason, _bombProcessor.RoundBomb);

        SaveStats(_currentRoundNumber);
        return HookResult.Continue;
    }

    private HookResult OnRoundOfficiallyEnded(EventRoundOfficiallyEnded @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode || GameRulesState.IsWarmup) return HookResult.Continue;
        if (_config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live) return HookResult.Continue;
        // Without a round_end this is a restart (mp_restartgame, the end of warmup): the round was not played out, and
        // the engine resets its counters next.
        if (!_roundEndSeen) return HookResult.Continue;

        var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
        if (!string.IsNullOrWhiteSpace(matchUuid))
            _persistenceChannel.TryWrite(new RoundOfficialEndUpdate(matchUuid, _currentRoundNumber));
        // The awards since round_end (exit frags, the loser bonus and the other round-end payouts) belong to this round.
        var players = PlayerControllerState.HumansBySteamId();
        _playerSessions.ForEachPlayer(stats =>
        {
            if (!players.TryGetValue(stats.SteamId, out var player)) return;
            Observe(stats, player);
            SampleRoundEnd(stats, player);
        });
        SaveStats(_currentRoundNumber);
        return HookResult.Continue;
    }

    // The lifetime rows of everyone whose totals changed, and the round's own numbers: the totals now against the totals
    // at the round's start. Written at round end, at the official end and at the next round's start (the later write
    // replaces the earlier), like the lifetime row.
    private void SaveStats(int roundNumber)
    {
        var match = _matchTracker.CurrentMatch;
        var snapshots = _playerSessions.CaptureSnapshots(true, match?.MatchUuid);

        foreach (var snapshot in snapshots)
        {
            if (_persistenceChannel.TryWrite(new PlayerStatsUpdate(snapshot)))
            {
                _playerSessions.MarkPersisted(snapshot.SteamId, snapshot.Version);
            }

            if (match is null || _roundBackup.GetRoundStart(roundNumber, snapshot.SteamId) is not { } roundStart) continue;
            var round = MatchStatsService.Delta(snapshot, roundStart);
            if (round.RoundsPlayed == 1) _persistenceChannel.TryWrite(new PlayerRoundStatsUpdate(round));
        }
    }

    // The money and equipment at the end of the round, read at round_end and again at the official end: the loser bonus
    // and the round-end kill rewards are paid after the round_end event (RoundWin runs after it).
    private static void SampleRoundEnd(PlayerStats stats, CCSPlayerController player)
    {
        if (player.InGameMoneyServices is not { } money) return;
        var equipmentValue = player.PlayerPawn.Value?.CurrentEquipmentValue ?? 0;
        stats.Economy.RoundEndMoney = money.Account;
        stats.Economy.EquipmentValue = equipmentValue;
        stats.Economy.EquipmentValueEnd = equipmentValue;
    }

    // Reads the engine's per-match counters and adds the change since the last observation to the lifetime totals.
    // The counters restart at zero if a reconnecting player's match data is not restored, so a decrease adds nothing.
    private EngineMatchStats Observe(PlayerStats stats, CCSPlayerController player)
    {
        var now = ReadEngineStats(player);
        var observed = _lastTotals.TryGetValue(stats.SteamId, out var last);
        _lastTotals[stats.SteamId] = now;
        if (!observed) return now;
        stats.Combat.Score += Math.Max(0, now.Score - last.Score);
        stats.Economy.MoneySpent += Math.Max(0, now.CashSpent - last.CashSpent);
        stats.Economy.KillReward += Math.Max(0, now.KillReward - last.KillReward);
        stats.Economy.CashEarned += Math.Max(0, now.CashEarned - last.CashEarned);
        stats.Economy.MoneySaved += Math.Max(0, now.MoneySaved - last.MoneySaved);
        stats.Round.LiveTime += Math.Max(0, now.LiveTime - last.LiveTime);
        return now;
    }

    // The engine's CSMatchStats_t counters, the scoreboard score and the cash spent of a connected player.
    private static EngineMatchStats ReadEngineStats(CCSPlayerController player)
    {
        var cashSpent = player.InGameMoneyServices?.TotalCashSpent ?? 0;
        var m = player.ActionTrackingServices?.MatchStats;
        return m is null
            ? default(EngineMatchStats) with { Score = player.Score, CashSpent = cashSpent }
            : new EngineMatchStats(m.Kills, m.Deaths, m.Assists, m.HeadShotKills, m.Damage, m.UtilityDamage, m.EnemiesFlashed,
                m.Enemy2Ks, m.Enemy3Ks, m.Enemy4Ks, m.Enemy5Ks, m.EntryCount, m.EntryWins,
                m.I1v1Count, m.I1v1Wins, m.I1v2Count, m.I1v2Wins,
                m.KillReward, m.CashEarned, m.MoneySaved, m.LiveTime, player.Score, cashSpent);
    }

    private static int GetCurrentRoundNumber() => GameRulesState.TotalRoundsPlayed + 1;
}
