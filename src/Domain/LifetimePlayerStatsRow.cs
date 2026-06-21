namespace statsCollector.Domain;

/// <summary>
/// Flat projection of a player's persisted lifetime row (<c>player_stats</c>), read back on connect to
/// seed the in-memory session. Only the raw cumulative counters are carried — derived ratings
/// (kd_ratio, hltv_rating, kast_percentage, …) are recomputed from these, so they are not seeded
/// directly. <c>KastPercentage</c> and <c>PlaytimeSeconds</c> are the two exceptions: they are used to
/// reconstruct the un-stored intermediates <c>KASTRounds</c> and the session start time.
/// Column names map via Dapper's underscore matching.
/// </summary>
public sealed record LifetimePlayerStatsRow
{
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int Headshots { get; init; }
    public int DamageDealt { get; init; }
    public int DamageTaken { get; init; }
    public int DamageArmor { get; init; }
    public int ShotsFired { get; init; }
    public int ShotsHit { get; init; }
    public int Mvps { get; init; }
    public int Score { get; init; }

    public int RoundsPlayed { get; init; }
    public int RoundsWon { get; init; }
    public int TotalSpawns { get; init; }
    public int PlaytimeSeconds { get; init; }
    public int CtRounds { get; init; }
    public int TRounds { get; init; }
    public int Jumps { get; init; }
    public int Pings { get; init; }
    public int Footsteps { get; init; }
    public decimal KastPercentage { get; init; }

    public int FlashesThrown { get; init; }
    public int SmokesThrown { get; init; }
    public int MolotovsThrown { get; init; }
    public int HeGrenadesThrown { get; init; }
    public int DecoysThrown { get; init; }
    public int PlayersBlinded { get; init; }
    public int TeammatesFlashed { get; init; }
    public int TimesBlinded { get; init; }
    public int FlashAssists { get; init; }
    public decimal TotalBlindTime { get; init; }
    public decimal TotalBlindTimeInflicted { get; init; }
    public int UtilityDamageDealt { get; init; }
    public int UtilityDamageTaken { get; init; }
    public int EffectiveFlashes { get; init; }
    public int EffectiveSmokes { get; init; }
    public int EffectiveHeGrenades { get; init; }
    public int EffectiveMolotovs { get; init; }
    public int FlashWaste { get; init; }

    public int BombPlants { get; init; }
    public int BombDefuses { get; init; }
    public int BombPlantAttempts { get; init; }
    public int BombPlantAborts { get; init; }
    public int BombDefuseAttempts { get; init; }
    public int BombDefuseAborts { get; init; }
    public int BombDefuseWithKit { get; init; }
    public int BombDefuseWithoutKit { get; init; }
    public int BombDrops { get; init; }
    public int BombPickups { get; init; }
    public int DefuserDrops { get; init; }
    public int DefuserPickups { get; init; }
    public int ClutchDefuses { get; init; }
    public int TotalPlantTime { get; init; }
    public int TotalDefuseTime { get; init; }
    public int BombKills { get; init; }
    public int BombDeaths { get; init; }

    public int MoneySpent { get; init; }
    public int EquipmentValue { get; init; }
    public int ItemsPurchased { get; init; }
    public int ItemsPickedUp { get; init; }
    public int RoundStartMoney { get; init; }
    public int RoundEndMoney { get; init; }
    public int EquipmentValueStart { get; init; }
    public int EquipmentValueEnd { get; init; }

    public int MultiKillNades { get; init; }
    public int NadeKills { get; init; }
    public int EntryKills { get; init; }
    public int EntryDeaths { get; init; }
    public int EntryKillAttempts { get; init; }
    public int EntryKillAttemptWins { get; init; }
    public int TradeKills { get; init; }
    public int TradedDeaths { get; init; }
    public int HighImpactKills { get; init; }
    public int LowImpactKills { get; init; }
    public int OpeningDuelsWon { get; init; }
    public int OpeningDuelsLost { get; init; }
    public int NoscopeKills { get; init; }
    public int ThruSmokeKills { get; init; }
    public int AttackerBlindKills { get; init; }
    public int WallbangKills { get; init; }
    public int Revenges { get; init; }
    public int ClutchesWon { get; init; }
    public int ClutchesLost { get; init; }
    public decimal ClutchPoints { get; init; }
    public int MvpsEliminations { get; init; }
    public int MvpsBomb { get; init; }
    public int MvpsHostage { get; init; }
    public int HeadshotsHit { get; init; }
    public int ChestHits { get; init; }
    public int StomachHits { get; init; }
    public int ArmHits { get; init; }
    public int LegHits { get; init; }
    public decimal RoundSwing { get; init; }
}
