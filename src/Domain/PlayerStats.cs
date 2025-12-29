using System;
using System.Collections.Generic;
using System.Linq;

namespace statsCollector.Domain;

public sealed class PlayerStats
{
    public int RoundNumber { get; set; }
    public DateTime RoundStartUtc { get; set; }
    public int AliveOnTeamAtRoundStart { get; set; }
    public int AliveEnemyAtRoundStart { get; set; }

    public ulong SteamId { get; init; }
    public string Name { get; set; } = string.Empty;
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Headshots { get; set; }
    public int DamageDealt { get; set; }
    public int DamageTaken { get; set; }
    public int DamageArmor { get; set; }
    public int ShotsFired { get; set; }
    public int ShotsHit { get; set; }
    public int Mvps { get; set; }
    public int Score { get; set; }
    public int RoundsPlayed { get; set; }
    public int RoundsWon { get; set; }
    public int TotalSpawns { get; set; }
    public int PlaytimeSeconds { get; set; }

    public int CtRounds { get; set; }
    public int TRounds { get; set; }

    public int GrenadesThrown { get; set; }
    public int FlashesThrown { get; set; }
    public int SmokesThrown { get; set; }
    public int MolotovsThrown { get; set; }
    public int HeGrenadesThrown { get; set; }
    public int DecoysThrown { get; set; }
    public int TacticalGrenadesThrown { get; set; }
    public int PlayersBlinded { get; set; }
    public int TimesBlinded { get; set; }
    public int FlashAssists { get; set; }
    public int TotalBlindTime { get; set; }
    public int TotalBlindTimeInflicted { get; set; }
    public int UtilityDamageDealt { get; set; }
    public int UtilityDamageTaken { get; set; }

    public int BombPlants { get; set; }
    public int BombDefuses { get; set; }
    public int BombPlantAttempts { get; set; }
    public int BombPlantAborts { get; set; }
    public int BombDefuseAttempts { get; set; }
    public int BombDefuseAborts { get; set; }
    public int BombDefuseWithKit { get; set; }
    public int BombDefuseWithoutKit { get; set; }
    public int BombDrops { get; set; }
    public int BombPickups { get; set; }
    public int DefuserDrops { get; set; }
    public int DefuserPickups { get; set; }
    public int ClutchDefuses { get; set; }
    public int TotalPlantTime { get; set; }
    public int TotalDefuseTime { get; set; }
    public int BombKills { get; set; }
    public int BombDeaths { get; set; }

    public int HostagesRescued { get; set; }

    public int Jumps { get; set; }

    public int MoneySpent { get; set; }
    public int EquipmentValue { get; set; }
    public int ItemsPurchased { get; set; }
    public int ItemsPickedUp { get; set; }
    public int ItemsDropped { get; set; }
    public int CashEarned { get; set; }
    public int CashSpent { get; set; }
    public int LossBonus { get; set; }
    public int RoundStartMoney { get; set; }
    public int RoundEndMoney { get; set; }
    public int EquipmentValueStart { get; set; }
    public int EquipmentValueEnd { get; set; }

    public int EnemiesFlashed { get; set; }
    public int TeammatesFlashed { get; set; }
    public int EffectiveFlashes { get; set; }
    public int EffectiveSmokes { get; set; }
    public int EffectiveHEGrenades { get; set; }
    public int EffectiveMolotovs { get; set; }
    public int FlashWaste { get; set; }
    public float TotalFlashIntensity { get; set; }
    public int ChokePointSmokes { get; set; }
    public int MultiKillNades { get; set; }
    public int NadeKills { get; set; }

    public int EntryKills { get; set; }
    public int TradeKills { get; set; }
    public int TradedDeaths { get; set; }
    public int HighImpactKills { get; set; }
    public int LowImpactKills { get; set; }
    public int TradeOpportunities { get; set; }
    public int TradeWindowsMissed { get; set; }
    public int MultiKills { get; set; }
    public int OpeningDuelsWon { get; set; }
    public int OpeningDuelsLost { get; set; }
    public int NoscopeKills { get; set; }
    public int ThruSmokeKills { get; set; }
    public int AttackerBlindKills { get; set; }
    public int FlashAssistedKills { get; set; }
    public int WallbangKills { get; set; }
    public int Revenges { get; set; }
    public int ClutchesWon { get; set; }
    public int ClutchesLost { get; set; }
    public decimal ClutchPoints { get; set; }

    public int MvpsEliminations { get; set; }
    public int MvpsBomb { get; set; }
    public int MvpsHostage { get; set; }

    public int HeadshotsHit { get; set; }
    public int ChestHits { get; set; }
    public int StomachHits { get; set; }
    public int ArmHits { get; set; }
    public int LegHits { get; set; }

    public int CurrentRoundKills { get; set; }
    public int CurrentRoundDeaths { get; set; }
    public int CurrentRoundShotsFired { get; set; }
    public bool HadKillThisRound { get; set; }
    public bool HadAssistThisRound { get; set; }
    public bool SurvivedThisRound { get; set; } = true;
    public bool WasTradedThisRound { get; set; }
    public bool DidTradeThisRound { get; set; }
    public bool WasFlashedForKill { get; set; }
    public int KASTRounds { get; set; }

    public PlayerTeam CurrentTeam { get; set; } = PlayerTeam.Spectator;

    public Dictionary<string, int> WeaponKills { get; } = new();
    public Dictionary<string, int> WeaponShots { get; } = new();
    public Dictionary<string, int> WeaponHits { get; } = new();

    public object SyncRoot { get; } = new();

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

    public decimal UtilityDamage => GrenadesThrown * 15m;

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

    public PlayerSnapshot ToSnapshot(int? matchId = null)
    {
        lock (SyncRoot)
        {
            return new PlayerSnapshot(
                matchId,
                RoundNumber,
                RoundStartUtc,
                AliveOnTeamAtRoundStart,
                AliveEnemyAtRoundStart,
                SteamId,
                Name,
                Kills,
                Deaths,
                Assists,
                Headshots,
                DamageDealt,
                DamageTaken,
                DamageArmor,
                ShotsFired,
                ShotsHit,
                Mvps,
                Score,
                RoundsPlayed,
                RoundsWon,
                CtRounds,
                TRounds,
                BombPlants,
                BombDefuses,
                BombPlantAttempts,
                BombPlantAborts,
                BombDefuseAttempts,
                BombDefuseAborts,
                BombDefuseWithKit,
                BombDefuseWithoutKit,
                BombDrops,
                BombPickups,
                DefuserDrops,
                DefuserPickups,
                ClutchDefuses,
                TotalPlantTime,
                TotalDefuseTime,
                HostagesRescued,
                GrenadesThrown,
                FlashesThrown,
                SmokesThrown,
                MolotovsThrown,
                HeGrenadesThrown,
                DecoysThrown,
                TacticalGrenadesThrown,
                PlayersBlinded,
                TimesBlinded,
                FlashAssists,
                TotalBlindTime,
                TotalBlindTimeInflicted,
                UtilityDamageDealt,
                UtilityDamageTaken,
                BombKills,
                BombDeaths,
                Jumps,
                TotalSpawns,
                PlaytimeSeconds,
                MoneySpent,
                EquipmentValue,
                ItemsPurchased,
                ItemsPickedUp,
                ItemsDropped,
                CashEarned,
                CashSpent,
                LossBonus,
                RoundStartMoney,
                RoundEndMoney,
                EquipmentValueStart,
                EquipmentValueEnd,
                EnemiesFlashed,
                TeammatesFlashed,
                EffectiveFlashes,
                EffectiveSmokes,
                EffectiveHEGrenades,
                EffectiveMolotovs,
                MultiKillNades,
                NadeKills,
                TradeWindowsMissed,
                FlashWaste,
                EntryKills,
                TradeKills,
                TradedDeaths,
                HighImpactKills,
                LowImpactKills,
                TradeOpportunities,
                MultiKills,
                OpeningDuelsWon,
                OpeningDuelsLost,
                NoscopeKills,
                ThruSmokeKills,
                AttackerBlindKills,
                FlashAssistedKills,
                WallbangKills,
                Revenges,
                ClutchesWon,
                ClutchesLost,
                ClutchPoints,
                MvpsEliminations,
                MvpsBomb,
                MvpsHostage,
                HeadshotsHit,
                ChestHits,
                StomachHits,
                ArmHits,
                LegHits,
                CurrentRoundKills,
                CurrentRoundDeaths,
                CurrentRoundShotsFired,
                HadKillThisRound,
                HadAssistThisRound,
                SurvivedThisRound,
                WasTradedThisRound,
                DidTradeThisRound,
                WasFlashedForKill,
                KASTRounds,
                new Dictionary<string, int>(WeaponKills),
                new Dictionary<string, int>(WeaponShots),
                new Dictionary<string, int>(WeaponHits)
            );
        }
    }

    public static PlayerSnapshot From(PlayerStats stats)
    {
        return stats.ToSnapshot();
    }

    public void ResetRoundStats()
    {
        lock (SyncRoot)
        {
            CurrentRoundKills = 0;
            CurrentRoundDeaths = 0;
            CurrentRoundShotsFired = 0;
            HadKillThisRound = false;
            HadAssistThisRound = false;
            SurvivedThisRound = true;
            WasTradedThisRound = false;
            DidTradeThisRound = false;
            WasFlashedForKill = false;
        }
    }

    public string GetBestWeaponByKills()
    {
        lock (SyncRoot)
        {
            return WeaponKills.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "None";
        }
    }

    public decimal GetWeaponAccuracy(string weapon)
    {
        lock (SyncRoot)
        {
            var shots = WeaponShots.GetValueOrDefault(weapon, 0);
            var hits = WeaponHits.GetValueOrDefault(weapon, 0);
            return shots > 0 ? (decimal)hits / shots * 100m : 0m;
        }
    }
}
