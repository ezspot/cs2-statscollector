using System;
using System.Collections.Generic;
using System.Linq;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface IAnalyticsService
{
    decimal CalculateHLTVRating(PlayerStats stats);
    decimal CalculateADR(PlayerStats stats);
    decimal CalculatePerformanceScore(PlayerStats stats);
    string GetPlayerRank(decimal performanceScore);
    decimal CalculateHltv2Rating(int kills, int assists, int deaths, int rounds, decimal kastPercent, decimal adr);
    PlayerSnapshot CreateSnapshot(PlayerStats stats, string? matchUuid = null);
}

public sealed class AnalyticsService : IAnalyticsService
{
    public decimal CalculateHLTVRating(PlayerStats stats)
    {
        return CalculateHltv2Rating(
            stats.Combat.Kills,
            stats.Combat.Assists,
            stats.Combat.Deaths,
            stats.Round.RoundsPlayed,
            CalculateKASTPercentage(stats),
            CalculateADR(stats));
    }

    private decimal CalculateImpactRating(PlayerStats stats)
    {
        if (stats.Round.RoundsPlayed == 0) return 0m;

        var multiKillImpact = stats.Combat.MultiKill2 * 0.1m + stats.Combat.MultiKill3 * 0.2m + stats.Combat.MultiKill4 * 0.3m + stats.Combat.MultiKill5 * 0.5m;
        var clutchImpact = (decimal)stats.Combat.ClutchKills * 0.2m;
        var openingImpact = stats.Combat.EntryKills * 0.25m + (stats.Combat.EntryKillAttemptWins * 0.1m); // Enhanced entry impact
        var mvpImpact = stats.Combat.MVPs * 0.05m;

        return Math.Min(2.0m, (multiKillImpact + clutchImpact + openingImpact + mvpImpact) / stats.Round.RoundsPlayed);
    }

    private decimal CalculateKASTPercentage(PlayerStats stats)
    {
        return stats.Round.RoundsPlayed > 0 ? (decimal)stats.Round.KASTRounds / stats.Round.RoundsPlayed * 100m : 0m;
    }

    public decimal CalculateADR(PlayerStats stats)
    {
        return stats.Round.RoundsPlayed > 0 ? (decimal)stats.Combat.DamageDealt / stats.Round.RoundsPlayed : 0m;
    }

    private decimal CalculateKDRatio(PlayerStats stats)
    {
        return stats.Combat.Deaths > 0 ? (decimal)stats.Combat.Kills / stats.Combat.Deaths : stats.Combat.Kills;
    }

    private decimal CalculateHeadshotPercentage(PlayerStats stats)
    {
        return stats.Combat.Kills > 0 ? (decimal)stats.Combat.Headshots / stats.Combat.Kills * 100m : 0m;
    }

    private decimal CalculateAccuracyPercentage(PlayerStats stats)
    {
        return stats.Combat.ShotsFired > 0 ? Math.Min(100m, (decimal)stats.Combat.ShotsHit / stats.Combat.ShotsFired * 100m) : 0m;
    }

    private decimal CalculateUtilityScore(PlayerStats stats)
    {
        if (stats.Round.RoundsPlayed == 0) return 0m;
        var rounds = (decimal)stats.Round.RoundsPlayed;
        var damageScore = (stats.Utility.UtilityDamage / rounds) * 0.4m;
        // Enemy blind time (seconds) inflicted per round.
        var blindScore = ((decimal)stats.Utility.TotalBlindTimeInflicted / rounds) * 0.4m;
        var flashAssistScore = (stats.Utility.FlashAssists / rounds) * 0.2m;
        return damageScore + blindScore + flashAssistScore;
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

    // Community Rating 2.0 approximation; HLTV's exact formula is not public.
    public decimal CalculateHltv2Rating(int kills, int assists, int deaths, int rounds, decimal kastPercent, decimal adr)
    {
        if (rounds <= 0) return 0m;

        var kpr = (decimal)kills / rounds;
        var dpr = (decimal)deaths / rounds;
        var apr = (decimal)assists / rounds;
        var impact = (2.13m * kpr) + (0.42m * apr) - 0.41m;
        var rating = (0.0073m * kastPercent) + (0.3591m * kpr) - (0.5329m * dpr) + (0.2372m * impact) + (0.0032m * adr) + 0.1587m;
        return Math.Max(0m, Math.Round(rating, 3));
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

    public PlayerSnapshot CreateSnapshot(PlayerStats stats, string? matchUuid = null)
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
        var effectiveGrenades = stats.Utility.EffectiveFlashes + stats.Utility.EffectiveHEGrenades + stats.Utility.EffectiveMolotovs;
        var grenadeEffectivenessRate = totalGrenadesThrown > 0
            ? (decimal)effectiveGrenades / totalGrenadesThrown * 100m
            : 0m;

        var flashEffectivenessRate = stats.Utility.FlashbangsThrown > 0 ? (decimal)stats.Utility.EffectiveFlashes / stats.Utility.FlashbangsThrown * 100m : 0m;
        var utilityUsagePerRound = stats.Round.RoundsPlayed > 0 ? (decimal)totalGrenadesThrown / stats.Round.RoundsPlayed : 0m;
        var averageMoneySpentPerRound = stats.Round.RoundsPlayed > 0 ? (decimal)stats.Economy.MoneySpent / stats.Round.RoundsPlayed : 0m;
        var survivalRating = stats.Round.RoundsPlayed > 0 ? Math.Max(0m, (decimal)(stats.Round.RoundsPlayed - stats.Combat.Deaths) / stats.Round.RoundsPlayed) * 0.154m : 0m;
        var utilityImpactScore = stats.Round.RoundsPlayed > 0 ? (decimal)stats.Utility.UtilityDamage / stats.Round.RoundsPlayed : 0m;
        var topWeapon = stats.Weapon.KillsByWeapon.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "None";

        return new PlayerSnapshot(
            Version: stats.Version,
            MatchUuid: matchUuid,
            RoundNumber: stats.Round.RoundNumber,
            SteamId: stats.SteamId,
            Name: stats.Name,
            Kills: stats.Combat.Kills,
            Deaths: stats.Combat.Deaths,
            Assists: stats.Combat.Assists,
            Headshots: stats.Combat.Headshots,
            DamageDealt: stats.Combat.DamageDealt,
            DamageTaken: stats.Combat.DamageTaken,
            DamageArmor: stats.Combat.DamageArmor,
            ShotsFired: stats.Combat.ShotsFired,
            ShotsHit: stats.Combat.ShotsHit,
            Mvps: stats.Combat.MVPs,
            Score: stats.Combat.Score,
            RoundsPlayed: stats.Round.RoundsPlayed,
            RoundsWon: stats.Round.RoundsWon,
            CtRounds: stats.Round.CtRounds,
            TRounds: stats.Round.TRounds,
            BombPlants: stats.Bomb.BombPlants,
            BombDefuses: stats.Bomb.BombDefuses,
            BombExplosions: stats.Bomb.BombExplosions,
            BombPlantAttempts: stats.Bomb.BombPlantAttempts,
            BombPlantAborts: stats.Bomb.BombPlantAborts,
            BombDefuseAttempts: stats.Bomb.BombDefuseAttempts,
            BombDefuseAborts: stats.Bomb.BombDefuseAborts,
            BombDefuseWithKit: stats.Bomb.BombDefuseWithKit,
            BombDefuseWithoutKit: stats.Bomb.BombDefuseWithoutKit,
            BombDrops: stats.Bomb.BombDrops,
            BombPickups: stats.Bomb.BombPickups,
            DefuserPickups: stats.Bomb.DefuserPickups,
            ClutchDefuses: stats.Bomb.ClutchDefuses,
            TotalPlantTime: stats.Bomb.TotalPlantTime,
            TotalDefuseTime: stats.Bomb.TotalDefuseTime,
            GrenadesThrown: totalGrenadesThrown,
            FlashesThrown: stats.Utility.FlashbangsThrown,
            SmokesThrown: stats.Utility.SmokesThrown,
            MolotovsThrown: stats.Utility.MolotovsThrown,
            HeGrenadesThrown: stats.Utility.HeGrenadesThrown,
            DecoysThrown: stats.Utility.DecoysThrown,
            TimesBlinded: stats.Utility.TimesBlinded,
            FlashAssists: stats.Utility.FlashAssists,
            TotalBlindTime: stats.Utility.TotalBlindTime,
            TotalBlindTimeInflicted: stats.Utility.TotalBlindTimeInflicted,
            UtilityDamageDealt: stats.Utility.UtilityDamage,
            UtilityDamageTaken: stats.Utility.UtilityDamageTaken,
            BombKills: stats.Bomb.BombKills,
            BombDeaths: stats.Bomb.BombDeaths,
            Jumps: stats.Round.Jumps,
            TotalSpawns: stats.Round.TotalSpawns,
            PlaytimeSeconds: (int)(DateTime.UtcNow - stats.SessionStartUtc).TotalSeconds,
            MoneySpent: stats.Economy.MoneySpent,
            EquipmentValue: stats.Economy.EquipmentValue,
            ItemsPurchased: stats.Economy.ItemsPurchased,
            ItemsPickedUp: stats.Economy.ItemsPickedUp,
            RoundStartMoney: stats.Economy.RoundStartMoney,
            RoundEndMoney: stats.Economy.RoundEndMoney,
            EquipmentValueStart: stats.Economy.EquipmentValueStart,
            EquipmentValueEnd: stats.Economy.EquipmentValueEnd,
            EnemiesFlashed: stats.Utility.EnemiesBlinded,
            TeammatesFlashed: stats.Utility.TeammatesBlinded,
            EffectiveFlashes: stats.Utility.EffectiveFlashes,
            EffectiveHEGrenades: stats.Utility.EffectiveHEGrenades,
            EffectiveMolotovs: stats.Utility.EffectiveMolotovs,
            MultiKillNades: stats.Combat.MultiKillNades,
            NadeKills: stats.Combat.NadeKills,
            FlashWaste: stats.Utility.WastedFlashes,
            EntryKills: stats.Combat.EntryKills,
            EntryDeaths: stats.Combat.EntryDeaths,
            EntryKillAttempts: stats.Combat.EntryKillAttempts,
            EntryKillAttemptWins: stats.Combat.EntryKillAttemptWins,
            TradeKills: stats.Combat.TradeKills,
            TradedDeaths: stats.Combat.TradedDeaths,
            HighImpactKills: stats.Combat.HighImpactKills,
            LowImpactKills: stats.Combat.LowImpactKills,
            MultiKills: stats.Combat.MultiKills,
            MultiKill2: stats.Combat.MultiKill2,
            MultiKill3: stats.Combat.MultiKill3,
            MultiKill4: stats.Combat.MultiKill4,
            MultiKill5: stats.Combat.MultiKill5,
            OpeningDuelsWon: stats.Combat.FirstKills,
            OpeningDuelsLost: stats.Combat.FirstDeaths,
            NoscopeKills: stats.Combat.Noscopes,
            ThruSmokeKills: stats.Combat.ThroughSmokeKills,
            AttackerBlindKills: stats.Combat.BlindKills,
            WallbangKills: stats.Combat.WallbangKills,
            Revenges: stats.Combat.Revenges,
            ClutchesWon: stats.Combat.ClutchesWon,
            ClutchesLost: stats.Combat.ClutchesLost,
            ClutchKills: stats.Combat.ClutchKills,
            ClutchPoints: stats.Combat.ClutchPoints,
            MvpsEliminations: stats.Combat.MvpsEliminations,
            MvpsBomb: stats.Combat.MvpsBomb,
            MvpsHostage: stats.Combat.MvpsHostage,
            HeadshotsHit: stats.Combat.HeadshotsHit,
            ChestHits: stats.Combat.ChestHits,
            StomachHits: stats.Combat.StomachHits,
            ArmHits: stats.Combat.ArmHits,
            LegHits: stats.Combat.LegHits,
            HadKillThisRound: stats.Round.HadKillThisRound,
            HadAssistThisRound: stats.Round.HadAssistThisRound,
            SurvivedThisRound: stats.Round.SurvivedThisRound,
            WasTradedThisRound: stats.Round.WasTradedThisRound,
            KASTRounds: stats.Round.KASTRounds,
            Pings: stats.Round.Pings,
            Footsteps: stats.Round.Footsteps,
            RoundSwing: stats.Combat.RoundSwing,
            WeaponKills: new Dictionary<string, int>(stats.Weapon.KillsByWeapon),
            WeaponShots: new Dictionary<string, int>(stats.Weapon.ShotsByWeapon),
            WeaponHits: new Dictionary<string, int>(stats.Weapon.HitsByWeapon),
            KDRatio: kdRatio,
            HeadshotPercentage: hsPercentage,
            AccuracyPercentage: accuracyPercentage,
            KASTPercentage: kastPercentage,
            AverageDamagePerRound: adr,
            HLTVRating: hltvRating,
            ImpactRating: impactRating,
            UtilityScore: utilityScore,
            PerformanceScore: perfScore,
            AverageKillsPerRound: stats.Round.RoundsPlayed > 0 ? (decimal)stats.Combat.Kills / stats.Round.RoundsPlayed : 0m,
            AverageDeathsPerRound: stats.Round.RoundsPlayed > 0 ? (decimal)stats.Combat.Deaths / stats.Round.RoundsPlayed : 0m,
            ClutchSuccessRate: clutchSuccessRate,
            TradeKillRatio: tradeKillRatio,
            OpeningKillRatio: openingKillRatio,
            GrenadeEffectivenessRate: grenadeEffectivenessRate,
            FlashEffectivenessRate: flashEffectivenessRate,
            UtilityUsagePerRound: utilityUsagePerRound,
            AverageMoneySpentPerRound: averageMoneySpentPerRound,
            SurvivalRating: survivalRating,
            UtilityImpactScore: utilityImpactScore,
            TopWeaponByKills: topWeapon,
            TeamKills: stats.Combat.TeamKills,
            Suicides: stats.Combat.Suicides,
            Clutch1v1Attempts: stats.Combat.Clutch1v1Attempts,
            Clutch1v1Wins: stats.Combat.Clutch1v1Wins,
            Clutch1v2Attempts: stats.Combat.Clutch1v2Attempts,
            Clutch1v2Wins: stats.Combat.Clutch1v2Wins,
            Clutch1v3Attempts: stats.Combat.Clutch1v3Attempts,
            Clutch1v3Wins: stats.Combat.Clutch1v3Wins,
            Clutch1v4Attempts: stats.Combat.Clutch1v4Attempts,
            Clutch1v4Wins: stats.Combat.Clutch1v4Wins,
            Clutch1v5Attempts: stats.Combat.Clutch1v5Attempts,
            Clutch1v5Wins: stats.Combat.Clutch1v5Wins,
            KillReward: stats.Economy.KillReward,
            CashEarned: stats.Economy.CashEarned,
            MoneySaved: stats.Economy.MoneySaved,
            LiveTime: stats.Round.LiveTime,
            TeamDamage: stats.Combat.TeamDamage,
            MultiKill1: stats.Combat.MultiKill1,
            CtKills: stats.Combat.CtKills,
            TKills: stats.Combat.TKills,
            CtDeaths: stats.Combat.CtDeaths,
            TDeaths: stats.Combat.TDeaths,
            CtDamage: stats.Combat.CtDamage,
            TDamage: stats.Combat.TDamage,
            CtFirstKills: stats.Combat.CtFirstKills,
            TFirstKills: stats.Combat.TFirstKills,
            CtFirstDeaths: stats.Combat.CtFirstDeaths,
            TFirstDeaths: stats.Combat.TFirstDeaths,
            RoundsSurvived: stats.Round.RoundsSurvived,
            RoundsTraded: stats.Round.RoundsTraded,
            CtRoundsWon: stats.Round.CtRoundsWon,
            TRoundsWon: stats.Round.TRoundsWon,
            PistolRoundsPlayed: stats.Round.PistolRoundsPlayed,
            PistolRoundsWon: stats.Round.PistolRoundsWon,
            PistolRoundKills: stats.Combat.PistolRoundKills,
            KillDistanceTotal: stats.Combat.KillDistanceTotal,
            LongestKill: stats.Combat.LongestKill,
            WeaponHeadshots: new Dictionary<string, int>(stats.Weapon.HeadshotsByWeapon),
            WeaponDamage: new Dictionary<string, int>(stats.Weapon.DamageByWeapon),
            WeaponDeaths: new Dictionary<string, int>(stats.Weapon.DeathsByWeapon));
    }
}
