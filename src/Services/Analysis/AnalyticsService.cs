using System;
using System.Collections.Generic;
using System.Linq;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface IAnalyticsService
{
    decimal CalculateHLTVRating(PlayerStats stats);
    decimal CalculateImpactRating(PlayerStats stats);
    decimal CalculateKASTPercentage(PlayerStats stats);
    decimal CalculateADR(PlayerStats stats);
    decimal CalculateKDRatio(PlayerStats stats);
    decimal CalculateHeadshotPercentage(PlayerStats stats);
    decimal CalculateAccuracyPercentage(PlayerStats stats);
    decimal CalculateUtilityScore(PlayerStats stats);
    decimal CalculatePerformanceScore(PlayerStats stats);
    string GetPlayerRank(decimal performanceScore);
    PlayerSnapshot CreateSnapshot(PlayerStats stats, int? matchId = null, string? matchUuid = null);
}

public sealed class AnalyticsService : IAnalyticsService
{
    public decimal CalculateHLTVRating(PlayerStats stats)
    {
        if (stats.Round.RoundsPlayed == 0) return 0m;

        var killsRating = (stats.Combat.Kills / (decimal)stats.Round.RoundsPlayed) * 0.6m;
        var deathsRating = (0.7m - (stats.Combat.Deaths / (decimal)stats.Round.RoundsPlayed) * 0.5m);
        var impactRating = CalculateImpactRating(stats) * 0.3m;
        var kastRating = CalculateKASTPercentage(stats) / 100m * 0.2m;
        var survivalRating = stats.Round.RoundsPlayed > 0 ? ((decimal)(stats.Round.RoundsPlayed - stats.Combat.Deaths) / stats.Round.RoundsPlayed) * 0.154m : 0m;
        
        return Math.Max(0m, killsRating + deathsRating + impactRating + kastRating + survivalRating);
    }

    public decimal CalculateImpactRating(PlayerStats stats)
    {
        if (stats.Round.RoundsPlayed == 0) return 0m;

        var multiKillImpact = stats.Combat.MultiKill2 * 0.1m + stats.Combat.MultiKill3 * 0.2m + stats.Combat.MultiKill4 * 0.3m + stats.Combat.MultiKill5 * 0.5m;
        var clutchImpact = (decimal)stats.Combat.ClutchKills * 0.2m;
        var openingImpact = stats.Combat.FirstKills * 0.15m;
        var mvpImpact = stats.Combat.MVPs * 0.05m;

        return Math.Min(2.0m, multiKillImpact + clutchImpact + openingImpact + mvpImpact);
    }

    public decimal CalculateKASTPercentage(PlayerStats stats)
    {
        return stats.Round.RoundsPlayed > 0 ? (decimal)stats.Round.KASTRounds / stats.Round.RoundsPlayed * 100m : 0m;
    }

    public decimal CalculateADR(PlayerStats stats)
    {
        return stats.Round.RoundsPlayed > 0 ? (decimal)stats.Combat.DamageDealt / stats.Round.RoundsPlayed : 0m;
    }

    public decimal CalculateKDRatio(PlayerStats stats)
    {
        return stats.Combat.Deaths > 0 ? (decimal)stats.Combat.Kills / stats.Combat.Deaths : stats.Combat.Kills;
    }

    public decimal CalculateHeadshotPercentage(PlayerStats stats)
    {
        return stats.Combat.Kills > 0 ? (decimal)stats.Combat.Headshots / stats.Combat.Kills * 100m : 0m;
    }

    public decimal CalculateAccuracyPercentage(PlayerStats stats)
    {
        return stats.Combat.ShotsFired > 0 ? (decimal)stats.Combat.ShotsHit / stats.Combat.ShotsFired * 100m : 0m;
    }

    public decimal CalculateUtilityScore(PlayerStats stats)
    {
        if (stats.Round.RoundsPlayed == 0) return 0m;
        var damageScore = (stats.Utility.UtilityDamage / (decimal)stats.Round.RoundsPlayed) * 0.4m;
        var blindScore = (stats.Utility.BlindDuration / 1000m / (decimal)stats.Round.RoundsPlayed) * 0.4m;
        var smokeScore = (stats.Utility.SmokeAssists / (decimal)stats.Round.RoundsPlayed) * 0.2m;
        return damageScore + blindScore + smokeScore;
    }

    public decimal CalculatePerformanceScore(PlayerStats stats)
    {
        if (stats.Round.RoundsPlayed == 0) return 0m;

        var kdScore = Math.Min(30m, CalculateKDRatio(stats) * 10m);
        var adrScore = Math.Min(20m, CalculateADR(stats) / 2m);
        var kastScore = Math.Min(20m, CalculateKASTPercentage(stats) / 5m);
        var mvpScore = Math.Min(15m, (decimal)stats.Combat.MVPs / stats.Round.RoundsPlayed * 15m);
        var impactScoreValue = Math.Min(15m, CalculateImpactRating(stats) * 7.5m);

        return kdScore + adrScore + kastScore + mvpScore + impactScoreValue;
    }

    public string GetPlayerRank(decimal performanceScore)
    {
        return performanceScore switch
        {
            >= 90 => "S+",
            >= 80 => "S",
            >= 70 => "A+",
            >= 60 => "A",
            >= 50 => "B+",
            >= 40 => "B",
            >= 30 => "C+",
            >= 20 => "C",
            >= 10 => "D",
            _ => "F"
        };
    }

    public PlayerSnapshot CreateSnapshot(PlayerStats stats, int? matchId = null, string? matchUuid = null)
    {
        var perfScore = CalculatePerformanceScore(stats);
        var hltvRating = CalculateHLTVRating(stats);
        var impactRating = CalculateImpactRating(stats);
        var kastPercentage = CalculateKASTPercentage(stats);
        var adr = CalculateADR(stats);
        var kdRatio = CalculateKDRatio(stats);
        var hsPercentage = CalculateHeadshotPercentage(stats);
        var accuracyPercentage = CalculateAccuracyPercentage(stats);
        var utilityScore = CalculateUtilityScore(stats);
        var rank = GetPlayerRank(perfScore);

        var clutchSuccessRate = (stats.Combat.ClutchesWon + stats.Combat.ClutchesLost) > 0 
            ? (decimal)stats.Combat.ClutchesWon / (stats.Combat.ClutchesWon + stats.Combat.ClutchesLost) * 100m
            : 0m;
        
        var tradeKillRatio = (stats.Combat.TradeKills + stats.Combat.TradedDeaths) > 0 
            ? (decimal)stats.Combat.TradeKills / (stats.Combat.TradeKills + stats.Combat.TradedDeaths) 
            : 0m;

        var openingKillRatio = (stats.Combat.FirstKills + stats.Combat.FirstDeaths) > 0 
            ? (decimal)stats.Combat.FirstKills / (stats.Combat.FirstKills + stats.Combat.FirstDeaths) 
            : 0m;

        var totalGrenadesThrown = stats.Utility.FlashbangsThrown + stats.Utility.SmokesThrown + stats.Utility.HeGrenadesThrown + stats.Utility.MolotovsThrown + stats.Utility.DecoysThrown;
        var grenadeEffectivenessRate = totalGrenadesThrown > 0 
            ? (decimal)(stats.Utility.UtilitySuccessCount) / totalGrenadesThrown * 100m 
            : 0m;

        var flashEffectivenessRate = stats.Utility.FlashbangsThrown > 0 ? (decimal)stats.Utility.EnemiesBlinded / stats.Utility.FlashbangsThrown : 0m;
        var utilityUsagePerRound = stats.Round.RoundsPlayed > 0 ? (decimal)totalGrenadesThrown / stats.Round.RoundsPlayed : 0m;
        var averageMoneySpentPerRound = stats.Round.RoundsPlayed > 0 ? (decimal)stats.Economy.MoneySpent / stats.Round.RoundsPlayed : 0m;
        var survivalRating = stats.Round.RoundsPlayed > 0 ? ((decimal)(stats.Round.RoundsPlayed - stats.Combat.Deaths) / stats.Round.RoundsPlayed) * 0.154m : 0m;
        var utilityImpactScore = stats.Round.RoundsPlayed > 0 ? (decimal)stats.Utility.UtilityDamage / stats.Round.RoundsPlayed : 0m;
        var topWeapon = stats.Weapon.KillsByWeapon.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "None";

        return new PlayerSnapshot(
            matchId,
            matchUuid,
            stats.Round.RoundNumber,
            stats.Round.RoundStartUtc,
            stats.Round.AliveOnTeamAtRoundStart,
            stats.Round.AliveEnemyAtRoundStart,
            stats.SteamId,
            stats.Name,
            stats.Combat.Kills,
            stats.Combat.Deaths,
            stats.Combat.Assists,
            stats.Combat.Headshots,
            stats.Combat.DamageDealt,
            stats.Combat.DamageTaken,
            stats.Combat.DamageArmor,
            stats.Combat.ShotsFired,
            stats.Combat.ShotsHit,
            stats.Combat.MVPs,
            stats.Combat.Score,
            stats.Round.RoundsPlayed,
            stats.Round.RoundsWon,
            stats.Round.CtRounds,
            stats.Round.TRounds,
            stats.Bomb.BombPlants,
            stats.Bomb.BombDefuses,
            stats.Bomb.BombPlantAttempts,
            stats.Bomb.BombPlantAborts,
            stats.Bomb.BombDefuseAttempts,
            stats.Bomb.BombDefuseAborts,
            stats.Bomb.BombDefuseWithKit,
            stats.Bomb.BombDefuseWithoutKit,
            stats.Bomb.BombDrops,
            stats.Bomb.BombPickups,
            stats.Bomb.DefuserDrops,
            stats.Bomb.DefuserPickups,
            stats.Bomb.ClutchDefuses,
            stats.Bomb.TotalPlantTime,
            stats.Bomb.TotalDefuseTime,
            stats.Combat.HostagesRescued,
            totalGrenadesThrown,
            stats.Utility.FlashbangsThrown,
            stats.Utility.SmokesThrown,
            stats.Utility.MolotovsThrown,
            stats.Utility.HeGrenadesThrown,
            stats.Utility.DecoysThrown,
            stats.Utility.TacticalGrenadesThrown,
            stats.Utility.EnemiesBlinded,
            stats.Utility.TimesBlinded,
            stats.Utility.FlashAssists,
            stats.Utility.TotalBlindTime,
            stats.Utility.TotalBlindTimeInflicted,
            stats.Utility.UtilityDamage,
            stats.Utility.UtilityDamageTaken,
            stats.Bomb.BombKills,
            stats.Bomb.BombDeaths,
            stats.Round.Jumps,
            stats.Round.TotalSpawns,
            stats.Round.PlaytimeSeconds,
            stats.Economy.MoneySpent,
            stats.Economy.EquipmentValue,
            stats.Economy.ItemsPurchased,
            stats.Economy.ItemsPickedUp,
            stats.Round.ItemsDropped,
            stats.Round.CashEarned,
            stats.Economy.MoneySpent, // CashSpent
            stats.Economy.LossBonus,
            stats.Economy.RoundStartMoney,
            stats.Economy.RoundEndMoney,
            stats.Economy.EquipmentValueStart,
            stats.Economy.EquipmentValueEnd,
            stats.Utility.EnemiesBlinded,
            stats.Utility.TeammatesBlinded,
            stats.Utility.EffectiveFlashes,
            stats.Utility.EffectiveSmokes,
            stats.Utility.EffectiveHEGrenades,
            stats.Utility.EffectiveMolotovs,
            stats.Combat.MultiKillNades,
            stats.Combat.NadeKills,
            stats.Round.TradeWindowsMissed,
            stats.Utility.UtilityWasteCount,
            stats.Combat.EntryKills,
            stats.Combat.TradeKills,
            stats.Combat.TradedDeaths,
            stats.Combat.HighImpactKills,
            stats.Combat.LowImpactKills,
            stats.Combat.TradeOpportunities,
            stats.Combat.MultiKill2 + stats.Combat.MultiKill3 + stats.Combat.MultiKill4 + stats.Combat.MultiKill5,
            stats.Combat.FirstKills, // OpeningDuelsWon
            stats.Combat.FirstDeaths, // OpeningDuelsLost
            stats.Combat.Noscopes,
            stats.Combat.ThroughSmokeKills,
            stats.Combat.BlindKills,
            stats.Utility.FlashAssists,
            stats.Combat.WallbangKills,
            stats.Combat.Revenges,
            stats.Combat.ClutchesWon,
            stats.Combat.ClutchesLost,
            stats.Combat.ClutchPoints,
            stats.Combat.MvpsEliminations,
            stats.Combat.MvpsBomb,
            stats.Combat.MvpsHostage,
            stats.Combat.HeadshotsHit,
            stats.Combat.ChestHits,
            stats.Combat.StomachHits,
            stats.Combat.ArmHits,
            stats.Combat.LegHits,
            stats.Combat.CurrentRoundKills,
            stats.Combat.CurrentRoundDeaths,
            stats.Combat.CurrentRoundShotsFired,
            stats.Round.HadKillThisRound,
            stats.Round.HadAssistThisRound,
            stats.Round.SurvivedThisRound,
            stats.Round.WasTradedThisRound,
            stats.Round.DidTradeThisRound,
            stats.Round.WasFlashedForKill,
            stats.Round.KASTRounds,
            stats.Round.Pings,
            stats.Round.Footsteps,
            new Dictionary<string, int>(stats.Weapon.KillsByWeapon),
            new Dictionary<string, int>(stats.Weapon.ShotsByWeapon),
            new Dictionary<string, int>(stats.Weapon.HitsByWeapon),
            kdRatio,
            hsPercentage,
            accuracyPercentage,
            kastPercentage,
            adr,
            hltvRating,
            impactRating,
            utilityScore,
            perfScore,
            rank,
            stats.Round.RoundsPlayed > 0 ? (decimal)stats.Combat.Kills / stats.Round.RoundsPlayed : 0m,
            stats.Round.RoundsPlayed > 0 ? (decimal)stats.Combat.Deaths / stats.Round.RoundsPlayed : 0m,
            stats.Round.RoundsPlayed > 0 ? (decimal)stats.Combat.Assists / stats.Round.RoundsPlayed : 0m,
            clutchSuccessRate,
            tradeKillRatio,
            openingKillRatio,
            grenadeEffectivenessRate,
            flashEffectivenessRate,
            utilityUsagePerRound,
            averageMoneySpentPerRound,
            survivalRating,
            utilityImpactScore,
            topWeapon
        );
    }
}
