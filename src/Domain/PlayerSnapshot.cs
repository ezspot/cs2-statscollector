using System;
using System.Collections.Generic;
using System.Linq;

namespace statsCollector.Domain;

public sealed record PlayerSnapshot
(
    int? MatchId,
    int RoundNumber,
    DateTime RoundStartUtc,
    int AliveOnTeamAtRoundStart,
    int AliveEnemyAtRoundStart,

    ulong SteamId,
    string Name,
    int Kills,
    int Deaths,
    int Assists,
    int Headshots,
    int DamageDealt,
    int DamageTaken,
    int DamageArmor,
    int ShotsFired,
    int ShotsHit,
    int Mvps,
    int Score,
    int RoundsPlayed,
    int RoundsWon,
    
    int CtRounds,
    int TRounds,
    
    int BombPlants,
    int BombDefuses,
    int BombPlantAttempts,
    int BombPlantAborts,
    int BombDefuseAttempts,
    int BombDefuseAborts,
    int BombDefuseWithKit,
    int BombDefuseWithoutKit,
    int BombDrops,
    int BombPickups,
    int DefuserDrops,
    int DefuserPickups,
    int ClutchDefuses,
    int TotalPlantTime,
    int TotalDefuseTime,
    
    int HostagesRescued,
    
    int GrenadesThrown,
    int FlashesThrown,
    int SmokesThrown,
    int MolotovsThrown,
    int HeGrenadesThrown,
    int DecoysThrown,
    int TacticalGrenadesThrown,
    int PlayersBlinded,
    int TimesBlinded,
    int FlashAssists,
    int TotalBlindTime,
    int TotalBlindTimeInflicted,
    int UtilityDamageDealt,
    int UtilityDamageTaken,
    int BombKills,
    int BombDeaths,
    
    int Jumps,
    int TotalSpawns,
    int PlaytimeSeconds,
    
    int MoneySpent,
    int EquipmentValue,
    int ItemsPurchased,
    int ItemsPickedUp,
    int ItemsDropped,
    int CashEarned,
    int CashSpent,
    int LossBonus,
    int RoundStartMoney,
    int RoundEndMoney,
    int EquipmentValueStart,
    int EquipmentValueEnd,
    
    int EnemiesFlashed,
    int TeammatesFlashed,
    int EffectiveFlashes,
    int EffectiveSmokes,
    int EffectiveHEGrenades,
    int EffectiveMolotovs,
    int MultiKillNades,
    int NadeKills,
    int TradeWindowsMissed,
    int FlashWaste,
    int EntryKills,
    int TradeKills,
    int TradedDeaths,
    int HighImpactKills,
    int LowImpactKills,
    int TradeOpportunities,
    int MultiKills,
    int OpeningDuelsWon,
    int OpeningDuelsLost,
    int NoscopeKills,
    int ThruSmokeKills,
    int AttackerBlindKills,
    int FlashAssistedKills,
    int WallbangKills,
    int Revenges,
    int ClutchesWon,
    int ClutchesLost,
    decimal ClutchPoints,
    int MvpsEliminations,
    int MvpsBomb,
    int MvpsHostage,
    
    int HeadshotsHit,
    int ChestHits,
    int StomachHits,
    int ArmHits,
    int LegHits,
    
    int CurrentRoundKills,
    int CurrentRoundDeaths,
    int CurrentRoundShotsFired,
    bool HadKillThisRound,
    bool HadAssistThisRound,
    bool SurvivedThisRound,
    bool WasTradedThisRound,
    bool DidTradeThisRound,
    bool WasFlashedForKill,
    int KASTRounds,

    IReadOnlyDictionary<string, int> WeaponKills,
    IReadOnlyDictionary<string, int> WeaponShots,
    IReadOnlyDictionary<string, int> WeaponHits
)
{
    public decimal TradeSuccessRate => TradeOpportunities > 0 ? (decimal)TradeKills / TradeOpportunities * 100m : 0m;

    public decimal KDRatio => Deaths > 0 ? (decimal)Kills / Deaths : Kills;

    public decimal HeadshotPercentage => Kills > 0 ? (decimal)Headshots / Kills * 100m : 0m;

    public decimal AccuracyPercentage => ShotsFired > 0 ? (decimal)ShotsHit / ShotsFired * 100m : 0m;

    public decimal KASTPercentage => RoundsPlayed > 0 ? (decimal)KASTRounds / RoundsPlayed * 100m : 0m;

    public decimal AverageDamagePerRound => RoundsPlayed > 0 ? (decimal)DamageDealt / RoundsPlayed : 0m;

    public decimal AverageKillsPerRound => RoundsPlayed > 0 ? (decimal)Kills / RoundsPlayed : 0m;

    public decimal AverageAssistsPerRound => RoundsPlayed > 0 ? (decimal)Assists / RoundsPlayed : 0m;

    public decimal AverageDeathsPerRound => RoundsPlayed > 0 ? (decimal)Deaths / RoundsPlayed : 0m;

    public decimal SurvivalRating => RoundsPlayed > 0 ? ((decimal)(RoundsPlayed - Deaths) / RoundsPlayed) * 0.154m : 0m;

    public decimal UtilityScore
    {
        get
        {
            if (RoundsPlayed == 0) return 0m;
            var damageScore = (UtilityDamageDealt / (decimal)RoundsPlayed) * 0.4m;
            var blindScore = (TotalBlindTimeInflicted / 1000m / (decimal)RoundsPlayed) * 0.4m;
            var smokeScore = (EffectiveSmokes / (decimal)RoundsPlayed) * 0.2m;
            return damageScore + blindScore + smokeScore;
        }
    }

    public decimal HLTVRating
    {
        get
        {
            if (RoundsPlayed == 0) return 0m;

            var killsRating = AverageKillsPerRound * 0.6m;
            var deathsRating = (0.7m - AverageDeathsPerRound * 0.5m);
            var impactRating = ImpactRating * 0.3m;
            var kastRating = (KASTRounds / (decimal)RoundsPlayed) * 0.2m;
            var survivalRating = SurvivalRating;
            
            return Math.Max(0m, killsRating + deathsRating + impactRating + kastRating + survivalRating);
        }
    }

    public decimal ImpactRating
    {
        get
        {
            if (RoundsPlayed == 0) return 0m;

            var multiKillImpact = MultiKills * 0.1m;
            var clutchImpact = (decimal)ClutchPoints * 0.2m;
            var openingImpact = EntryKills * 0.15m;
            var mvpImpact = Mvps * 0.05m;

            return Math.Min(2.0m, multiKillImpact + clutchImpact + openingImpact + mvpImpact);
        }
    }

    public decimal AverageMoneySpentPerRound => RoundsPlayed > 0 ? (decimal)MoneySpent / RoundsPlayed : 0m;

    public decimal GrenadeEffectivenessRate
    {
        get
        {
            var totalEffective = EffectiveFlashes + EffectiveSmokes + EffectiveHEGrenades + EffectiveMolotovs;
            return GrenadesThrown > 0 ? (decimal)totalEffective / GrenadesThrown * 100m : 0m;
        }
    }

    public decimal FlashEffectivenessRate => FlashesThrown > 0 ? (decimal)EnemiesFlashed / FlashesThrown : 0m;

    public decimal UtilityUsagePerRound => RoundsPlayed > 0 ? (decimal)GrenadesThrown / RoundsPlayed : 0m;

    public decimal OpeningKillRatio => (OpeningDuelsWon + OpeningDuelsLost) > 0 
        ? (decimal)OpeningDuelsWon / (OpeningDuelsWon + OpeningDuelsLost) 
        : 0m;

    public decimal ClutchSuccessRate => (ClutchesWon + ClutchesLost) > 0 
        ? (decimal)ClutchesWon / (ClutchesWon + ClutchesLost) 
        : 0m;

    public decimal TradeKillRatio => (TradeKills + TradedDeaths) > 0 
        ? (decimal)TradeKills / (TradeKills + TradedDeaths) 
        : 0m;

    public string GetBestWeaponByKills()
    {
        return WeaponKills.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "None";
    }

    public decimal GetWeaponAccuracy(string weapon)
    {
        var shots = WeaponShots.GetValueOrDefault(weapon, 0);
        var hits = WeaponHits.GetValueOrDefault(weapon, 0);
        return shots > 0 ? (decimal)hits / shots * 100m : 0m;
    }

    public IReadOnlyList<(string Weapon, int Kills)> GetTopWeaponsByKills(int count = 3)
    {
        return WeaponKills
            .OrderByDescending(x => x.Value)
            .Take(count)
            .Select(x => (x.Key, x.Value))
            .ToList();
    }

    public IReadOnlyDictionary<string, decimal> GetAllWeaponAccuracies()
    {
        return WeaponShots.Keys
            .ToDictionary(
                weapon => weapon,
                weapon => GetWeaponAccuracy(weapon)
            );
    }

    public decimal PerformanceScore
    {
        get
        {
            if (RoundsPlayed == 0) return 0m;

            var kdScore = Math.Min(30m, KDRatio * 10m);
            var adrScore = Math.Min(20m, AverageDamagePerRound / 2m);
            var kastScore = Math.Min(20m, KASTPercentage / 5m);
            var mvpScore = Math.Min(15m, (decimal)Mvps / RoundsPlayed * 15m);
            var impactScoreValue = Math.Min(15m, ImpactRating * 7.5m);

            return kdScore + adrScore + kastScore + mvpScore + impactScoreValue;
        }
    }

    public string GetPlayerRank()
    {
        var score = PerformanceScore;
        
        return score switch
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
}
