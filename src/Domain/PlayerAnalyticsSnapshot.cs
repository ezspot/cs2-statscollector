using System;

namespace statsCollector.Domain;

/// <summary>
/// A point-in-time row of a player's computed analytics, written once per player at match end
/// (calculated_at = finalize time). Building one of these per match yields a temporal timeline of a
/// player's performance. Maps 1:1 to the player_advanced_analytics table; match_id is resolved from
/// MatchUuid at insert time.
/// </summary>
public sealed record PlayerAnalyticsSnapshot(
    string MatchUuid,
    ulong SteamId,
    DateTime CalculatedAt,
    string Name,
    decimal Rating2,
    decimal KillsPerRound,
    decimal DeathsPerRound,
    decimal ImpactScore,
    decimal KastPercentage,
    decimal AverageDamagePerRound,
    decimal UtilityImpactScore,
    decimal ClutchSuccessRate,
    decimal TradeSuccessRate,
    int FlashWaste,
    int EntryKills,
    int EntryDeaths,
    int EntryKillAttempts,
    int EntryKillAttemptWins,
    decimal EntrySuccessRate,
    int RoundsPlayed,
    decimal KdRatio,
    decimal HeadshotPercentage,
    decimal OpeningKillRatio,
    decimal TradeKillRatio,
    decimal GrenadeEffectivenessRate,
    decimal FlashEffectivenessRate,
    decimal UtilityUsagePerRound,
    decimal AverageMoneySpentPerRound,
    decimal PerformanceScore,
    string TopWeaponByKills,
    decimal SurvivalRating,
    decimal UtilityScore,
    decimal ClutchPoints,
    int FlashAssistedKills,
    int WallbangKills,
    string IdempotencyKey
);
