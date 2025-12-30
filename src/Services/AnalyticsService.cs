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
    PlayerSnapshot CreateSnapshot(PlayerStats stats, int? matchId = null);
}

public sealed class AnalyticsService : IAnalyticsService
{
    public decimal CalculateHLTVRating(PlayerStats stats)
    {
        if (stats.RoundsPlayed == 0) return 0m;

        var killsRating = (stats.Kills / (decimal)stats.RoundsPlayed) * 0.6m;
        var deathsRating = (0.7m - (stats.Deaths / (decimal)stats.RoundsPlayed) * 0.5m);
        var impactRating = CalculateImpactRating(stats) * 0.3m;
        var kastRating = CalculateKASTPercentage(stats) / 100m * 0.2m;
        var survivalRating = stats.RoundsPlayed > 0 ? ((decimal)(stats.RoundsPlayed - stats.Deaths) / stats.RoundsPlayed) * 0.154m : 0m;
        
        return Math.Max(0m, killsRating + deathsRating + impactRating + kastRating + survivalRating);
    }

    public decimal CalculateImpactRating(PlayerStats stats)
    {
        if (stats.RoundsPlayed == 0) return 0m;

        var multiKillImpact = stats.MultiKills * 0.1m;
        var clutchImpact = (decimal)stats.ClutchPoints * 0.2m;
        var openingImpact = stats.EntryKills * 0.15m;
        var mvpImpact = stats.Mvps * 0.05m;

        return Math.Min(2.0m, multiKillImpact + clutchImpact + openingImpact + mvpImpact);
    }

    public decimal CalculateKASTPercentage(PlayerStats stats)
    {
        return stats.RoundsPlayed > 0 ? (decimal)stats.KASTRounds / stats.RoundsPlayed * 100m : 0m;
    }

    public decimal CalculateADR(PlayerStats stats)
    {
        return stats.RoundsPlayed > 0 ? (decimal)stats.DamageDealt / stats.RoundsPlayed : 0m;
    }

    public decimal CalculateKDRatio(PlayerStats stats)
    {
        return stats.Deaths > 0 ? (decimal)stats.Kills / stats.Deaths : stats.Kills;
    }

    public decimal CalculateHeadshotPercentage(PlayerStats stats)
    {
        return stats.Kills > 0 ? (decimal)stats.Headshots / stats.Kills * 100m : 0m;
    }

    public decimal CalculateAccuracyPercentage(PlayerStats stats)
    {
        return stats.ShotsFired > 0 ? (decimal)stats.ShotsHit / stats.ShotsFired * 100m : 0m;
    }

    public decimal CalculateUtilityScore(PlayerStats stats)
    {
        if (stats.RoundsPlayed == 0) return 0m;
        var damageScore = (stats.UtilityDamageDealt / (decimal)stats.RoundsPlayed) * 0.4m;
        var blindScore = (stats.TotalBlindTimeInflicted / 1000m / (decimal)stats.RoundsPlayed) * 0.4m;
        var smokeScore = (stats.EffectiveSmokes / (decimal)stats.RoundsPlayed) * 0.2m;
        return damageScore + blindScore + smokeScore;
    }

    public decimal CalculatePerformanceScore(PlayerStats stats)
    {
        if (stats.RoundsPlayed == 0) return 0m;

        var kdScore = Math.Min(30m, CalculateKDRatio(stats) * 10m);
        var adrScore = Math.Min(20m, CalculateADR(stats) / 2m);
        var kastScore = Math.Min(20m, CalculateKASTPercentage(stats) / 5m);
        var mvpScore = Math.Min(15m, (decimal)stats.Mvps / stats.RoundsPlayed * 15m);
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

    public PlayerSnapshot CreateSnapshot(PlayerStats stats, int? matchId = null)
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

        var clutchSuccessRate = (stats.ClutchesWon + stats.ClutchesLost) > 0 
            ? (decimal)stats.ClutchesWon / (stats.ClutchesWon + stats.ClutchesLost) * 100m
            : 0m;
        
        var tradeKillRatio = (stats.TradeKills + stats.TradedDeaths) > 0 
            ? (decimal)stats.TradeKills / (stats.TradeKills + stats.TradedDeaths) 
            : 0m;

        var openingKillRatio = (stats.OpeningDuelsWon + stats.OpeningDuelsLost) > 0 
            ? (decimal)stats.OpeningDuelsWon / (stats.OpeningDuelsWon + stats.OpeningDuelsLost) 
            : 0m;

        var grenadeEffectivenessRate = stats.GrenadesThrown > 0 
            ? (decimal)(stats.EffectiveFlashes + stats.EffectiveSmokes + stats.EffectiveHEGrenades + stats.EffectiveMolotovs) / stats.GrenadesThrown * 100m 
            : 0m;

        var flashEffectivenessRate = stats.FlashesThrown > 0 ? (decimal)stats.EnemiesFlashed / stats.FlashesThrown : 0m;
        var utilityUsagePerRound = stats.RoundsPlayed > 0 ? (decimal)stats.GrenadesThrown / stats.RoundsPlayed : 0m;
        var averageMoneySpentPerRound = stats.RoundsPlayed > 0 ? (decimal)stats.MoneySpent / stats.RoundsPlayed : 0m;
        var survivalRating = stats.RoundsPlayed > 0 ? ((decimal)(stats.RoundsPlayed - stats.Deaths) / stats.RoundsPlayed) * 0.154m : 0m;
        var utilityImpactScore = stats.RoundsPlayed > 0 ? (decimal)stats.UtilityDamageDealt / stats.RoundsPlayed : 0m;
        var topWeapon = stats.WeaponKills.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "None";

        return new PlayerSnapshot(
            matchId,
            stats.RoundNumber,
            stats.RoundStartUtc,
            stats.AliveOnTeamAtRoundStart,
            stats.AliveEnemyAtRoundStart,
            stats.SteamId,
            stats.Name,
            stats.Kills,
            stats.Deaths,
            stats.Assists,
            stats.Headshots,
            stats.DamageDealt,
            stats.DamageTaken,
            stats.DamageArmor,
            stats.ShotsFired,
            stats.ShotsHit,
            stats.Mvps,
            stats.Score,
            stats.RoundsPlayed,
            stats.RoundsWon,
            stats.CtRounds,
            stats.TRounds,
            stats.BombPlants,
            stats.BombDefuses,
            stats.BombPlantAttempts,
            stats.BombPlantAborts,
            stats.BombDefuseAttempts,
            stats.BombDefuseAborts,
            stats.BombDefuseWithKit,
            stats.BombDefuseWithoutKit,
            stats.BombDrops,
            stats.BombPickups,
            stats.DefuserDrops,
            stats.DefuserPickups,
            stats.ClutchDefuses,
            stats.TotalPlantTime,
            stats.TotalDefuseTime,
            stats.HostagesRescued,
            stats.GrenadesThrown,
            stats.FlashesThrown,
            stats.SmokesThrown,
            stats.MolotovsThrown,
            stats.HeGrenadesThrown,
            stats.DecoysThrown,
            stats.TacticalGrenadesThrown,
            stats.PlayersBlinded,
            stats.TimesBlinded,
            stats.FlashAssists,
            stats.TotalBlindTime,
            stats.TotalBlindTimeInflicted,
            stats.UtilityDamageDealt,
            stats.UtilityDamageTaken,
            stats.BombKills,
            stats.BombDeaths,
            stats.Jumps,
            stats.TotalSpawns,
            stats.PlaytimeSeconds,
            stats.MoneySpent,
            stats.EquipmentValue,
            stats.ItemsPurchased,
            stats.ItemsPickedUp,
            stats.ItemsDropped,
            stats.CashEarned,
            stats.CashSpent,
            stats.LossBonus,
            stats.RoundStartMoney,
            stats.RoundEndMoney,
            stats.EquipmentValueStart,
            stats.EquipmentValueEnd,
            stats.EnemiesFlashed,
            stats.TeammatesFlashed,
            stats.EffectiveFlashes,
            stats.EffectiveSmokes,
            stats.EffectiveHEGrenades,
            stats.EffectiveMolotovs,
            stats.MultiKillNades,
            stats.NadeKills,
            stats.TradeWindowsMissed,
            stats.FlashWaste,
            stats.EntryKills,
            stats.TradeKills,
            stats.TradedDeaths,
            stats.HighImpactKills,
            stats.LowImpactKills,
            stats.TradeOpportunities,
            stats.MultiKills,
            stats.OpeningDuelsWon,
            stats.OpeningDuelsLost,
            stats.NoscopeKills,
            stats.ThruSmokeKills,
            stats.AttackerBlindKills,
            stats.FlashAssistedKills,
            stats.WallbangKills,
            stats.Revenges,
            stats.ClutchesWon,
            stats.ClutchesLost,
            stats.ClutchPoints,
            stats.MvpsEliminations,
            stats.MvpsBomb,
            stats.MvpsHostage,
            stats.HeadshotsHit,
            stats.ChestHits,
            stats.StomachHits,
            stats.ArmHits,
            stats.LegHits,
            stats.CurrentRoundKills,
            stats.CurrentRoundDeaths,
            stats.CurrentRoundShotsFired,
            stats.HadKillThisRound,
            stats.HadAssistThisRound,
            stats.SurvivedThisRound,
            stats.WasTradedThisRound,
            stats.DidTradeThisRound,
            stats.WasFlashedForKill,
            stats.KASTRounds,
            new Dictionary<string, int>(stats.WeaponKills),
            new Dictionary<string, int>(stats.WeaponShots),
            new Dictionary<string, int>(stats.WeaponHits),
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
            stats.RoundsPlayed > 0 ? (decimal)stats.Kills / stats.RoundsPlayed : 0m,
            stats.RoundsPlayed > 0 ? (decimal)stats.Deaths / stats.RoundsPlayed : 0m,
            stats.RoundsPlayed > 0 ? (decimal)stats.Assists / stats.RoundsPlayed : 0m,
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
