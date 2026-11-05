using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface IMatchStatsService
{
    /// <summary>Records each present player's cumulative totals as the start-of-match baseline.</summary>
    void CaptureBaseline();

    /// <summary>Captures a baseline for a single player who joined after the match started (and after
    /// their lifetime totals were seeded), so their per-match delta reflects only this match.</summary>
    void EnsureBaseline(ulong steamId);
    void FinalizePlayer(ulong steamId);

    /// <summary>Copy of the per-match baseline, so a round restore can return to it.</summary>
    IReadOnlyDictionary<ulong, PlayerSnapshot> CopyBaseline();

    /// <summary>Returns the baseline to <paramref name="baseline"/> for the sessions restarted from
    /// <paramref name="restartedFrom"/> (their totals at that point), keeping everyone else's.</summary>
    void RestoreBaseline(IReadOnlyDictionary<ulong, PlayerSnapshot> baseline, IEnumerable<PlayerSnapshot> restartedFrom);

    /// <summary>Rewrites the match row of a player who is not connected, from their totals and match baseline.</summary>
    void RewriteMatchStats(PlayerSnapshot totals, PlayerSnapshot baseline);

    /// <summary>Computes per-match deltas against the baseline and enqueues match summary + weapon rows. With the
    /// engine's own per-match counters (read at match end), logs where ours differ.</summary>
    void FinalizeMatch(IReadOnlyDictionary<ulong, EngineMatchStats>? engine = null);
}

/// <summary>
/// Produces per-match statistics (match_player_stats / match_weapon_stats) by diffing each player's
/// cumulative session totals between match start and match end. Session totals stay cumulative (so the
/// lifetime player_stats overwrite-merge remains idempotent); only the match delta is persisted here.
/// </summary>
public sealed class MatchStatsService(
    ILogger<MatchStatsService> logger,
    IPlayerSessionService playerSessions,
    IAnalyticsService analytics,
    IMatchTrackingService matchTracker,
    IPersistenceChannel persistenceChannel) : IMatchStatsService
{
    private readonly ILogger<MatchStatsService> _logger = logger;
    private readonly IPlayerSessionService _playerSessions = playerSessions;
    private readonly IAnalyticsService _analytics = analytics;
    private readonly IMatchTrackingService _matchTracker = matchTracker;
    private readonly IPersistenceChannel _persistenceChannel = persistenceChannel;

    private readonly object _lock = new();
    private readonly Dictionary<ulong, PlayerSnapshot> _baseline = [];

    public void CaptureBaseline()
    {
        int count;
        lock (_lock)
        {
            // Captured under the lock so a seed completing concurrently (EnsureBaseline) is not wiped by Clear.
            var snapshots = _playerSessions.CaptureSnapshots(onlyDirty: false);
            _baseline.Clear();
            foreach (var s in snapshots) _baseline[s.SteamId] = s;
            count = snapshots.Length;
        }
        _logger.LogInformation("Captured per-match baseline for {Count} players.", count);
    }

    public void EnsureBaseline(ulong steamId)
    {
        // Only meaningful while a match is live; if the player is already in the baseline (present at
        // match start) leave it untouched.
        if (_matchTracker.CurrentMatch?.MatchUuid is null) return;

        lock (_lock)
        {
            if (_baseline.ContainsKey(steamId)) return;
        }

        // TryGetSnapshot returns false until the session is seeded, so this baseline already includes
        // the player's lifetime totals — making the end-of-match delta their contribution this match.
        if (_playerSessions.TryGetSnapshot(steamId, out var snapshot))
        {
            lock (_lock) { _baseline.TryAdd(steamId, snapshot); }
        }
    }

    public void FinalizeMatch(IReadOnlyDictionary<ulong, EngineMatchStats>? engine = null)
    {
        var match = _matchTracker.CurrentMatch;
        if (match?.MatchUuid is null) return;

        List<PlayerSnapshot> baseline;
        lock (_lock)
        {
            if (_baseline.Count == 0) return;
            baseline = [.. _baseline.Values];
            _baseline.Clear();
        }

        var baselineById = baseline.ToDictionary(snapshot => snapshot.SteamId);
        var current = _playerSessions.CaptureSnapshots(onlyDirty: false, matchUuid: match.MatchUuid);

        var written = 0;
        foreach (var cur in current)
        {
            var engineStats = engine is not null && engine.TryGetValue(cur.SteamId, out var e) ? e : (EngineMatchStats?)null;
            if (WriteMatchStats(cur, baselineById.GetValueOrDefault(cur.SteamId), match.MatchUuid, engineStats)) written++;
        }

        _logger.LogInformation("Finalized per-match stats for {Count} players in match {Uuid}.", written, match.MatchUuid);
    }

    public void FinalizePlayer(ulong steamId)
    {
        var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
        if (matchUuid is null || !_playerSessions.TryGetSnapshot(steamId, out var current, matchUuid)) return;

        PlayerSnapshot? baseline;
        lock (_lock)
        {
            // Keep the match-start baseline: if the player reconnects, FinalizeMatch rewrites this row with
            // the whole match instead of only the part after the reconnect.
            if (!_baseline.TryGetValue(steamId, out baseline)) return;
        }

        if (WriteMatchStats(current, baseline, matchUuid))
        {
            _logger.LogInformation("Finalized per-match stats for disconnected player {SteamId}.", steamId);
        }

        lock (_lock)
        {
            if (_baseline.TryGetValue(steamId, out var kept)) _baseline[steamId] = CarryWeaponTotals(kept, current);
        }
    }

    public IReadOnlyDictionary<ulong, PlayerSnapshot> CopyBaseline()
    {
        lock (_lock) return new Dictionary<ulong, PlayerSnapshot>(_baseline);
    }

    public void RestoreBaseline(IReadOnlyDictionary<ulong, PlayerSnapshot> baseline, IEnumerable<PlayerSnapshot> restartedFrom)
    {
        lock (_lock)
        {
            foreach (var totals in restartedFrom)
            {
                if (baseline.TryGetValue(totals.SteamId, out var kept)) _baseline[totals.SteamId] = CarryWeaponTotals(kept, totals);
            }
        }
    }

    public void RewriteMatchStats(PlayerSnapshot totals, PlayerSnapshot baseline)
    {
        if (_matchTracker.CurrentMatch?.MatchUuid is { } matchUuid) WriteMatchStats(totals, baseline, matchUuid);
    }

    // Counters are reseeded from the database when a session restarts (reconnect, round restore), but weapon totals
    // are per session and restart at zero. Keep the old session's weapon numbers in the baseline as a negative
    // offset (baseline minus its totals) so the new session's totals minus the baseline still cover the whole match.
    private static PlayerSnapshot CarryWeaponTotals(PlayerSnapshot baseline, PlayerSnapshot sessionTotals) => baseline with
    {
        WeaponKills = Subtract(baseline.WeaponKills, sessionTotals.WeaponKills),
        WeaponHeadshots = Subtract(baseline.WeaponHeadshots, sessionTotals.WeaponHeadshots),
        WeaponShots = Subtract(baseline.WeaponShots, sessionTotals.WeaponShots),
        WeaponHits = Subtract(baseline.WeaponHits, sessionTotals.WeaponHits),
        WeaponDamage = Subtract(baseline.WeaponDamage, sessionTotals.WeaponDamage),
        WeaponDeaths = Subtract(baseline.WeaponDeaths, sessionTotals.WeaponDeaths)
    };

    private static Dictionary<string, int> Subtract(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right)
    {
        var result = new Dictionary<string, int>(left);
        foreach (var (weapon, value) in right) result[weapon] = result.GetValueOrDefault(weapon) - value;
        return result;
    }

    private bool WriteMatchStats(PlayerSnapshot current, PlayerSnapshot? baseline, string matchUuid, EngineMatchStats? engine = null)
    {
        var rounds = current.RoundsPlayed - (baseline?.RoundsPlayed ?? 0);
        if (rounds <= 0) return false;

        var delta = BuildMatchDelta(current, baseline, matchUuid, rounds);
        if (engine is { } engineStats) CrossCheck(delta, engineStats);
        var analytics = BuildAnalyticsSnapshot(current, matchUuid);
        return _persistenceChannel.TryWrite(new MatchPlayerStatsUpdate(delta, analytics));
    }

    /// <summary>
    /// The change of every counter from <paramref name="b"/> (nothing, when null) to <paramref name="cur"/>: the player's
    /// numbers over a match or a round. What is not a counter is kept from <paramref name="cur"/>: the round flags, the
    /// money and equipment stamped during the round, the derived ratios, and the longest kill.
    /// </summary>
    internal static PlayerSnapshot Delta(PlayerSnapshot cur, PlayerSnapshot? b)
    {
        int D(Func<PlayerSnapshot, int> f) => b is null ? f(cur) : f(cur) - f(b);
        decimal M(Func<PlayerSnapshot, decimal> f) => b is null ? f(cur) : f(cur) - f(b);
        float F(Func<PlayerSnapshot, float> f) => b is null ? f(cur) : f(cur) - f(b);
        return cur with
        {
            Kills = D(s => s.Kills),
            Deaths = D(s => s.Deaths),
            Assists = D(s => s.Assists),
            Headshots = D(s => s.Headshots),
            DamageDealt = D(s => s.DamageDealt),
            DamageTaken = D(s => s.DamageTaken),
            DamageArmor = D(s => s.DamageArmor),
            ShotsFired = D(s => s.ShotsFired),
            ShotsHit = D(s => s.ShotsHit),
            Mvps = D(s => s.Mvps),
            Score = D(s => s.Score),
            RoundsPlayed = D(s => s.RoundsPlayed),
            RoundsWon = D(s => s.RoundsWon),
            CtRounds = D(s => s.CtRounds),
            TRounds = D(s => s.TRounds),
            BombPlants = D(s => s.BombPlants),
            BombDefuses = D(s => s.BombDefuses),
            BombExplosions = D(s => s.BombExplosions),
            BombPlantAttempts = D(s => s.BombPlantAttempts),
            BombPlantAborts = D(s => s.BombPlantAborts),
            BombDefuseAttempts = D(s => s.BombDefuseAttempts),
            BombDefuseAborts = D(s => s.BombDefuseAborts),
            BombDefuseWithKit = D(s => s.BombDefuseWithKit),
            BombDefuseWithoutKit = D(s => s.BombDefuseWithoutKit),
            BombDrops = D(s => s.BombDrops),
            BombPickups = D(s => s.BombPickups),
            DefuserPickups = D(s => s.DefuserPickups),
            ClutchDefuses = D(s => s.ClutchDefuses),
            TotalPlantTime = D(s => s.TotalPlantTime),
            TotalDefuseTime = D(s => s.TotalDefuseTime),
            GrenadesThrown = D(s => s.GrenadesThrown),
            FlashesThrown = D(s => s.FlashesThrown),
            SmokesThrown = D(s => s.SmokesThrown),
            MolotovsThrown = D(s => s.MolotovsThrown),
            HeGrenadesThrown = D(s => s.HeGrenadesThrown),
            DecoysThrown = D(s => s.DecoysThrown),
            PlayersBlinded = D(s => s.PlayersBlinded),
            TimesBlinded = D(s => s.TimesBlinded),
            FlashAssists = D(s => s.FlashAssists),
            UtilityDamageDealt = D(s => s.UtilityDamageDealt),
            UtilityDamageTaken = D(s => s.UtilityDamageTaken),
            BombKills = D(s => s.BombKills),
            BombDeaths = D(s => s.BombDeaths),
            Jumps = D(s => s.Jumps),
            TotalSpawns = D(s => s.TotalSpawns),
            PlaytimeSeconds = D(s => s.PlaytimeSeconds),
            MoneySpent = D(s => s.MoneySpent),
            ItemsPurchased = D(s => s.ItemsPurchased),
            ItemsPickedUp = D(s => s.ItemsPickedUp),
            EnemiesFlashed = D(s => s.EnemiesFlashed),
            TeammatesFlashed = D(s => s.TeammatesFlashed),
            EffectiveFlashes = D(s => s.EffectiveFlashes),
            EffectiveHEGrenades = D(s => s.EffectiveHEGrenades),
            EffectiveMolotovs = D(s => s.EffectiveMolotovs),
            MultiKillNades = D(s => s.MultiKillNades),
            NadeKills = D(s => s.NadeKills),
            FlashWaste = D(s => s.FlashWaste),
            EntryKills = D(s => s.EntryKills),
            EntryDeaths = D(s => s.EntryDeaths),
            EntryKillAttempts = D(s => s.EntryKillAttempts),
            EntryKillAttemptWins = D(s => s.EntryKillAttemptWins),
            TradeKills = D(s => s.TradeKills),
            TradedDeaths = D(s => s.TradedDeaths),
            HighImpactKills = D(s => s.HighImpactKills),
            LowImpactKills = D(s => s.LowImpactKills),
            MultiKills = D(s => s.MultiKills),
            MultiKill2 = D(s => s.MultiKill2),
            MultiKill3 = D(s => s.MultiKill3),
            MultiKill4 = D(s => s.MultiKill4),
            MultiKill5 = D(s => s.MultiKill5),
            OpeningDuelsWon = D(s => s.OpeningDuelsWon),
            OpeningDuelsLost = D(s => s.OpeningDuelsLost),
            NoscopeKills = D(s => s.NoscopeKills),
            ThruSmokeKills = D(s => s.ThruSmokeKills),
            AttackerBlindKills = D(s => s.AttackerBlindKills),
            FlashAssistedKills = D(s => s.FlashAssistedKills),
            WallbangKills = D(s => s.WallbangKills),
            Revenges = D(s => s.Revenges),
            ClutchesWon = D(s => s.ClutchesWon),
            ClutchesLost = D(s => s.ClutchesLost),
            ClutchKills = D(s => s.ClutchKills),
            MvpsEliminations = D(s => s.MvpsEliminations),
            MvpsBomb = D(s => s.MvpsBomb),
            MvpsHostage = D(s => s.MvpsHostage),
            HeadshotsHit = D(s => s.HeadshotsHit),
            ChestHits = D(s => s.ChestHits),
            StomachHits = D(s => s.StomachHits),
            ArmHits = D(s => s.ArmHits),
            LegHits = D(s => s.LegHits),
            KASTRounds = D(s => s.KASTRounds),
            Pings = D(s => s.Pings),
            Footsteps = D(s => s.Footsteps),
            TeamKills = D(s => s.TeamKills),
            Suicides = D(s => s.Suicides),
            Clutch1v1Attempts = D(s => s.Clutch1v1Attempts),
            Clutch1v1Wins = D(s => s.Clutch1v1Wins),
            Clutch1v2Attempts = D(s => s.Clutch1v2Attempts),
            Clutch1v2Wins = D(s => s.Clutch1v2Wins),
            Clutch1v3Attempts = D(s => s.Clutch1v3Attempts),
            Clutch1v3Wins = D(s => s.Clutch1v3Wins),
            Clutch1v4Attempts = D(s => s.Clutch1v4Attempts),
            Clutch1v4Wins = D(s => s.Clutch1v4Wins),
            Clutch1v5Attempts = D(s => s.Clutch1v5Attempts),
            Clutch1v5Wins = D(s => s.Clutch1v5Wins),
            KillReward = D(s => s.KillReward),
            CashEarned = D(s => s.CashEarned),
            MoneySaved = D(s => s.MoneySaved),
            LiveTime = D(s => s.LiveTime),
            TeamDamage = D(s => s.TeamDamage),
            MultiKill1 = D(s => s.MultiKill1),
            CtKills = D(s => s.CtKills),
            TKills = D(s => s.TKills),
            CtDeaths = D(s => s.CtDeaths),
            TDeaths = D(s => s.TDeaths),
            CtDamage = D(s => s.CtDamage),
            TDamage = D(s => s.TDamage),
            CtFirstKills = D(s => s.CtFirstKills),
            TFirstKills = D(s => s.TFirstKills),
            CtFirstDeaths = D(s => s.CtFirstDeaths),
            TFirstDeaths = D(s => s.TFirstDeaths),
            RoundsSurvived = D(s => s.RoundsSurvived),
            RoundsTraded = D(s => s.RoundsTraded),
            CtRoundsWon = D(s => s.CtRoundsWon),
            TRoundsWon = D(s => s.TRoundsWon),
            PistolRoundsPlayed = D(s => s.PistolRoundsPlayed),
            PistolRoundsWon = D(s => s.PistolRoundsWon),
            PistolRoundKills = D(s => s.PistolRoundKills),
            ClutchPoints = M(s => s.ClutchPoints),
            RoundSwing = M(s => s.RoundSwing),
            KillDistanceTotal = M(s => s.KillDistanceTotal),
            TotalBlindTime = F(s => s.TotalBlindTime),
            TotalBlindTimeInflicted = F(s => s.TotalBlindTimeInflicted),
            WeaponKills = DeltaDictionary(cur.WeaponKills, b?.WeaponKills),
            WeaponHeadshots = DeltaDictionary(cur.WeaponHeadshots, b?.WeaponHeadshots),
            WeaponShots = DeltaDictionary(cur.WeaponShots, b?.WeaponShots),
            WeaponHits = DeltaDictionary(cur.WeaponHits, b?.WeaponHits),
            WeaponDamage = DeltaDictionary(cur.WeaponDamage, b?.WeaponDamage),
            WeaponDeaths = DeltaDictionary(cur.WeaponDeaths, b?.WeaponDeaths)
        };
    }

    private PlayerSnapshot BuildMatchDelta(PlayerSnapshot cur, PlayerSnapshot? b, string matchUuid, int rounds)
    {
        var delta = Delta(cur, b);
        var adr = rounds > 0 ? (decimal)delta.DamageDealt / rounds : 0m;
        var kast = rounds > 0 ? (decimal)delta.KASTRounds / rounds * 100m : 0m;
        return delta with
        {
            MatchUuid = matchUuid,
            AverageDamagePerRound = adr,
            KASTPercentage = kast,
            HLTVRating = _analytics.CalculateHltv2Rating(delta.Kills, delta.Assists, delta.Deaths, rounds, kast, adr)
        };
    }

    // The engine keeps its own per-match counters (CSMatchStats_t); ours come from the events. Kills, deaths, assists
    // and headshot kills are defined the same way on both sides, so a difference is a bug on one side, or a reconnect
    // (the engine restarts the player at zero). The others are only reported: the engine's damage, entry duel, flash
    // and multi-kill counts have their own definitions.
    private void CrossCheck(PlayerSnapshot d, EngineMatchStats e)
    {
        var exact = Differences([("kills", d.Kills, e.Kills), ("deaths", d.Deaths, e.Deaths), ("assists", d.Assists, e.Assists), ("headshot kills", d.Headshots, e.HeadshotKills)]);
        var reported = Differences(
        [
            ("damage", d.DamageDealt, e.Damage), ("utility damage", d.UtilityDamageDealt, e.UtilityDamage), ("enemies flashed", d.EnemiesFlashed, e.EnemiesFlashed),
            ("2k rounds", d.MultiKill2, e.Enemy2Ks), ("3k rounds", d.MultiKill3, e.Enemy3Ks), ("4k rounds", d.MultiKill4, e.Enemy4Ks), ("5k rounds", d.MultiKill5, e.Enemy5Ks),
            ("entry attempts", d.EntryKillAttempts, e.EntryCount), ("entry wins", d.EntryKillAttemptWins, e.EntryWins),
            ("1v1 attempts", d.Clutch1v1Attempts, e.Clutch1v1Count), ("1v1 wins", d.Clutch1v1Wins, e.Clutch1v1Wins),
            ("1v2 attempts", d.Clutch1v2Attempts, e.Clutch1v2Count), ("1v2 wins", d.Clutch1v2Wins, e.Clutch1v2Wins)
        ]);
        if (exact.Count > 0)
            _logger.LogWarning("Match {Match}: {Name} ({SteamId}) differs from the engine's counters (ours/engine): {Differences}", d.MatchUuid, d.Name, d.SteamId, string.Join(", ", exact));
        if (reported.Count > 0)
            _logger.LogInformation("Match {Match}: {Name} ({SteamId}) engine counters with their own definitions (ours/engine): {Differences}", d.MatchUuid, d.Name, d.SteamId, string.Join(", ", reported));
    }

    private static List<string> Differences((string Name, int Ours, int Engine)[] pairs) =>
        pairs.Where(p => p.Ours != p.Engine).Select(p => $"{p.Name} {p.Ours}/{p.Engine}").ToList();

    private PlayerAnalyticsSnapshot BuildAnalyticsSnapshot(PlayerSnapshot cur, string matchUuid)
    {
        // Rating 2.0 approximation from lifetime totals at this point in time.
        var rating2 = _analytics.CalculateHltv2Rating(cur.Kills, cur.Assists, cur.Deaths, cur.RoundsPlayed, cur.KASTPercentage, cur.AverageDamagePerRound);
        var entrySuccessRate = cur.EntryKillAttempts > 0
            ? (decimal)cur.EntryKillAttemptWins / cur.EntryKillAttempts * 100m
            : 0m;

        return new PlayerAnalyticsSnapshot(
            MatchUuid: matchUuid,
            SteamId: cur.SteamId,
            Name: cur.Name,
            Rating2: rating2,
            KillsPerRound: cur.AverageKillsPerRound,
            DeathsPerRound: cur.AverageDeathsPerRound,
            ImpactScore: cur.ImpactRating,
            KastPercentage: cur.KASTPercentage,
            AverageDamagePerRound: cur.AverageDamagePerRound,
            UtilityImpactScore: cur.UtilityImpactScore,
            ClutchSuccessRate: cur.ClutchSuccessRate,
            TradeSuccessRate: cur.TradeKillRatio * 100m,
            FlashWaste: cur.FlashWaste,
            EntryKills: cur.EntryKills,
            EntryDeaths: cur.EntryDeaths,
            EntryKillAttempts: cur.EntryKillAttempts,
            EntryKillAttemptWins: cur.EntryKillAttemptWins,
            EntrySuccessRate: entrySuccessRate,
            RoundsPlayed: cur.RoundsPlayed,
            KdRatio: cur.KDRatio,
            HeadshotPercentage: cur.HeadshotPercentage,
            OpeningKillRatio: cur.OpeningKillRatio,
            TradeKillRatio: cur.TradeKillRatio,
            GrenadeEffectivenessRate: cur.GrenadeEffectivenessRate,
            FlashEffectivenessRate: cur.FlashEffectivenessRate,
            UtilityUsagePerRound: cur.UtilityUsagePerRound,
            AverageMoneySpentPerRound: cur.AverageMoneySpentPerRound,
            PerformanceScore: cur.PerformanceScore,
            TopWeaponByKills: cur.TopWeaponByKills,
            SurvivalRating: cur.SurvivalRating,
            UtilityScore: cur.UtilityScore,
            ClutchPoints: cur.ClutchPoints,
            FlashAssistedKills: cur.FlashAssistedKills,
            WallbangKills: cur.WallbangKills,
            IdempotencyKey: $"{matchUuid}_{cur.SteamId}_Analytics");
    }

    private static Dictionary<string, int> DeltaDictionary(IReadOnlyDictionary<string, int> current, IReadOnlyDictionary<string, int>? baseline)
    {
        var result = new Dictionary<string, int>();
        foreach (var kvp in current)
        {
            var delta = kvp.Value - (baseline?.GetValueOrDefault(kvp.Key) ?? 0);
            if (delta > 0) result[kvp.Key] = delta;
        }
        return result;
    }
}
