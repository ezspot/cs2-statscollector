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

    /// <summary>Computes per-match deltas against the baseline and enqueues match summary + weapon rows.</summary>
    void FinalizeMatch();
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

    public void FinalizeMatch()
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
        var calculatedAt = DateTime.UtcNow;

        var written = 0;
        foreach (var cur in current)
        {
            if (WriteMatchStats(cur, baselineById.GetValueOrDefault(cur.SteamId), match.MatchUuid, calculatedAt)) written++;
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

        if (WriteMatchStats(current, baseline, matchUuid, DateTime.UtcNow))
        {
            _logger.LogInformation("Finalized per-match stats for disconnected player {SteamId}.", steamId);
        }

        // Counters are reseeded from the database on reconnect, but weapon totals are per session and restart
        // at zero. Keep this session's weapon numbers in the baseline as a negative offset (baseline minus
        // current) so a reconnected session's totals minus the baseline still cover the whole match.
        lock (_lock)
        {
            if (_baseline.TryGetValue(steamId, out var kept))
            {
                _baseline[steamId] = kept with
                {
                    WeaponKills = Subtract(kept.WeaponKills, current.WeaponKills),
                    WeaponShots = Subtract(kept.WeaponShots, current.WeaponShots),
                    WeaponHits = Subtract(kept.WeaponHits, current.WeaponHits)
                };
            }
        }
    }

    private static Dictionary<string, int> Subtract(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right)
    {
        var result = new Dictionary<string, int>(left);
        foreach (var (weapon, value) in right) result[weapon] = result.GetValueOrDefault(weapon) - value;
        return result;
    }

    private bool WriteMatchStats(PlayerSnapshot current, PlayerSnapshot? baseline, string matchUuid, DateTime calculatedAt)
    {
        var rounds = current.RoundsPlayed - (baseline?.RoundsPlayed ?? 0);
        if (rounds <= 0) return false;

        var delta = BuildMatchDelta(current, baseline, matchUuid, rounds);
        var analytics = BuildAnalyticsSnapshot(current, matchUuid, calculatedAt);
        return _persistenceChannel.TryWrite(new MatchPlayerStatsUpdate(delta, analytics));
    }

    private PlayerSnapshot BuildMatchDelta(PlayerSnapshot cur, PlayerSnapshot? b, string matchUuid, int rounds)
    {
        var kills = cur.Kills - (b?.Kills ?? 0);
        var deaths = cur.Deaths - (b?.Deaths ?? 0);
        var assists = cur.Assists - (b?.Assists ?? 0);
        var damage = cur.DamageDealt - (b?.DamageDealt ?? 0);
        var kastRounds = cur.KASTRounds - (b?.KASTRounds ?? 0);

        var adr = rounds > 0 ? (decimal)damage / rounds : 0m;
        var kast = rounds > 0 ? (decimal)kastRounds / rounds * 100m : 0m;
        var rating2 = _analytics.CalculateHltv2Rating(kills, assists, deaths, rounds, kast, adr);

        return cur with
        {
            MatchUuid = matchUuid,
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            Headshots = cur.Headshots - (b?.Headshots ?? 0),
            DamageDealt = damage,
            Mvps = cur.Mvps - (b?.Mvps ?? 0),
            Score = cur.Score - (b?.Score ?? 0),
            EntryKills = cur.EntryKills - (b?.EntryKills ?? 0),
            EntryDeaths = cur.EntryDeaths - (b?.EntryDeaths ?? 0),
            EntryKillAttempts = cur.EntryKillAttempts - (b?.EntryKillAttempts ?? 0),
            EntryKillAttemptWins = cur.EntryKillAttemptWins - (b?.EntryKillAttemptWins ?? 0),
            AverageDamagePerRound = adr,
            KASTPercentage = kast,
            HLTVRating = rating2,
            WeaponKills = DeltaDictionary(cur.WeaponKills, b?.WeaponKills),
            WeaponShots = DeltaDictionary(cur.WeaponShots, b?.WeaponShots),
            WeaponHits = DeltaDictionary(cur.WeaponHits, b?.WeaponHits)
        };
    }

    private PlayerAnalyticsSnapshot BuildAnalyticsSnapshot(PlayerSnapshot cur, string matchUuid, DateTime calculatedAt)
    {
        // Rating 2.0 approximation from lifetime totals at this point in time.
        var rating2 = _analytics.CalculateHltv2Rating(cur.Kills, cur.Assists, cur.Deaths, cur.RoundsPlayed, cur.KASTPercentage, cur.AverageDamagePerRound);
        var entrySuccessRate = cur.EntryKillAttempts > 0
            ? (decimal)cur.EntryKillAttemptWins / cur.EntryKillAttempts * 100m
            : 0m;

        return new PlayerAnalyticsSnapshot(
            MatchUuid: matchUuid,
            SteamId: cur.SteamId,
            CalculatedAt: calculatedAt,
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
