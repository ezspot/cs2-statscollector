using System;
using System.Collections.Generic;
using statsCollector.Domain.Stats;

namespace statsCollector.Domain;

public sealed class PlayerStats
{
    private bool _isDirty;
    public bool IsDirty 
    { 
        get => _isDirty; 
        private set => _isDirty = value; 
    }

    public void MarkDirty() => _isDirty = true;
    public void ClearDirty() => _isDirty = false;

    // When this player's session started; used to derive playtime. Settable so a seeded lifetime
    // playtime can shift the origin backwards.
    public DateTime SessionStartUtc { get; private set; } = DateTime.UtcNow;

    // True once this session has been seeded from the persisted lifetime row, so seeding never runs
    // twice (e.g. if player_connect_full fires more than once).
    public bool LifetimeSeeded { get; private set; }

    private ulong _steamId;
    public ulong SteamId 
    { 
        get => _steamId; 
        set { if (_steamId != value) { _steamId = value; MarkDirty(); } } 
    }

    private string _name = string.Empty;
    public string Name 
    { 
        get => _name; 
        set { if (_name != value) { _name = value; MarkDirty(); } } 
    }

    private PlayerTeam _currentTeam = PlayerTeam.Spectator;
    public PlayerTeam CurrentTeam 
    { 
        get => _currentTeam; 
        set { if (_currentTeam != value) { _currentTeam = value; MarkDirty(); } } 
    }

    // Sub-models
    public CombatStats Combat { get; }
    public EconomyStats Economy { get; }
    public UtilityStats Utility { get; }
    public BombStats Bomb { get; }
    public RoundStats Round { get; }
    public WeaponStats Weapon { get; }

    public PlayerStats()
    {
        Combat = new CombatStats(MarkDirty);
        Economy = new EconomyStats(MarkDirty);
        Utility = new UtilityStats(MarkDirty);
        Bomb = new BombStats(MarkDirty);
        Round = new RoundStats(MarkDirty);
        Weapon = new WeaponStats(MarkDirty);
    }

    public void ResetRoundStats()
    {
        Round.ResetRoundFlags();
        Combat.ResetRoundSwing();
        MarkDirty();
    }

    /// <summary>
    /// Seeds this session from the player's persisted lifetime totals so subsequent (overwrite) writes
    /// extend their career rather than replacing it with a single session — required for correctness
    /// across reconnects and across multiple servers sharing one database. Additive (<c>+=</c>) so any
    /// stat already recorded in the brief window before the async load completes is preserved.
    /// Only raw counters are seeded; derived ratings are recomputed from them. The two un-stored
    /// intermediates that dominate the ratings are reconstructed: <c>KASTRounds</c> from the stored
    /// KAST% × rounds, and <c>WeightedKills</c> approximated by kills (the per-kill bonuses are small).
    /// </summary>
    /// <summary>Marks the session seeded without applying any totals — used for first-time players who
    /// have no persisted lifetime row yet, so their writes are allowed to proceed.</summary>
    public void MarkLifetimeSeeded()
    {
        if (LifetimeSeeded) return;
        LifetimeSeeded = true;
        MarkDirty();
    }

    public void SeedLifetime(LifetimePlayerStatsRow r)
    {
        if (LifetimeSeeded) return;

        Combat.Kills += r.Kills;
        Combat.Deaths += r.Deaths;
        Combat.Assists += r.Assists;
        Combat.Headshots += r.Headshots;
        Combat.DamageDealt += r.DamageDealt;
        Combat.DamageTaken += r.DamageTaken;
        Combat.DamageArmor += r.DamageArmor;
        Combat.ShotsFired += r.ShotsFired;
        Combat.ShotsHit += r.ShotsHit;
        Combat.MVPs += r.Mvps;
        Combat.Score += r.Score;
        Combat.MultiKillNades += r.MultiKillNades;
        Combat.NadeKills += r.NadeKills;
        Combat.EntryKills += r.EntryKills;
        Combat.EntryDeaths += r.EntryDeaths;
        Combat.EntryKillAttempts += r.EntryKillAttempts;
        Combat.EntryKillAttemptWins += r.EntryKillAttemptWins;
        Combat.TradeKills += r.TradeKills;
        Combat.TradedDeaths += r.TradedDeaths;
        Combat.HighImpactKills += r.HighImpactKills;
        Combat.LowImpactKills += r.LowImpactKills;
        Combat.FirstKills += r.OpeningDuelsWon;
        Combat.FirstDeaths += r.OpeningDuelsLost;
        Combat.Noscopes += r.NoscopeKills;
        Combat.ThroughSmokeKills += r.ThruSmokeKills;
        Combat.BlindKills += r.AttackerBlindKills;
        Combat.WallbangKills += r.WallbangKills;
        Combat.Revenges += r.Revenges;
        Combat.ClutchesWon += r.ClutchesWon;
        Combat.ClutchesLost += r.ClutchesLost;
        Combat.ClutchPoints += r.ClutchPoints;
        Combat.MvpsEliminations += r.MvpsEliminations;
        Combat.MvpsBomb += r.MvpsBomb;
        Combat.MvpsHostage += r.MvpsHostage;
        Combat.HeadshotsHit += r.HeadshotsHit;
        Combat.ChestHits += r.ChestHits;
        Combat.StomachHits += r.StomachHits;
        Combat.ArmHits += r.ArmHits;
        Combat.LegHits += r.LegHits;
        Combat.RoundSwing += r.RoundSwing;
        Combat.WeightedKills += r.Kills; // approximation; per-kill bonuses are not persisted

        Round.RoundsPlayed += r.RoundsPlayed;
        Round.RoundsWon += r.RoundsWon;
        Round.TotalSpawns += r.TotalSpawns;
        Round.CtRounds += r.CtRounds;
        Round.TRounds += r.TRounds;
        Round.Jumps += r.Jumps;
        Round.Pings += r.Pings;
        Round.Footsteps += r.Footsteps;
        // KASTRounds isn't a stored column; reconstruct it from the persisted KAST% so KAST and the
        // KAST-dependent HLTV rating stay correct after a reseed.
        Round.KASTRounds += (int)Math.Round(r.KastPercentage / 100m * r.RoundsPlayed);

        Utility.FlashbangsThrown += r.FlashesThrown;
        Utility.SmokesThrown += r.SmokesThrown;
        Utility.MolotovsThrown += r.MolotovsThrown;
        Utility.HeGrenadesThrown += r.HeGrenadesThrown;
        Utility.DecoysThrown += r.DecoysThrown;
        Utility.EnemiesBlinded += r.PlayersBlinded;
        Utility.TeammatesBlinded += r.TeammatesFlashed;
        Utility.TimesBlinded += r.TimesBlinded;
        Utility.FlashAssists += r.FlashAssists;
        Utility.TotalBlindTime += (float)r.TotalBlindTime;
        Utility.TotalBlindTimeInflicted += (float)r.TotalBlindTimeInflicted;
        Utility.UtilityDamage += r.UtilityDamageDealt;
        Utility.UtilityDamageTaken += r.UtilityDamageTaken;
        Utility.EffectiveFlashes += r.EffectiveFlashes;
        Utility.EffectiveSmokes += r.EffectiveSmokes;
        Utility.EffectiveHEGrenades += r.EffectiveHeGrenades;
        Utility.EffectiveMolotovs += r.EffectiveMolotovs;
        Utility.WastedFlashes += r.FlashWaste;

        Bomb.BombPlants += r.BombPlants;
        Bomb.BombDefuses += r.BombDefuses;
        Bomb.BombPlantAttempts += r.BombPlantAttempts;
        Bomb.BombPlantAborts += r.BombPlantAborts;
        Bomb.BombDefuseAttempts += r.BombDefuseAttempts;
        Bomb.BombDefuseAborts += r.BombDefuseAborts;
        Bomb.BombDefuseWithKit += r.BombDefuseWithKit;
        Bomb.BombDefuseWithoutKit += r.BombDefuseWithoutKit;
        Bomb.BombDrops += r.BombDrops;
        Bomb.BombPickups += r.BombPickups;
        Bomb.DefuserDrops += r.DefuserDrops;
        Bomb.DefuserPickups += r.DefuserPickups;
        Bomb.ClutchDefuses += r.ClutchDefuses;
        Bomb.TotalPlantTime += r.TotalPlantTime;
        Bomb.TotalDefuseTime += r.TotalDefuseTime;
        Bomb.BombKills += r.BombKills;
        Bomb.BombDeaths += r.BombDeaths;

        Economy.MoneySpent += r.MoneySpent;
        Economy.EquipmentValue += r.EquipmentValue;
        Economy.ItemsPurchased += r.ItemsPurchased;
        Economy.ItemsPickedUp += r.ItemsPickedUp;
        Economy.RoundStartMoney += r.RoundStartMoney;
        Economy.RoundEndMoney += r.RoundEndMoney;
        Economy.EquipmentValueStart += r.EquipmentValueStart;
        Economy.EquipmentValueEnd += r.EquipmentValueEnd;

        // Shift the playtime origin back by the persisted seconds so lifetime playtime continues.
        if (r.PlaytimeSeconds > 0)
            SessionStartUtc = SessionStartUtc.AddSeconds(-r.PlaytimeSeconds);

        LifetimeSeeded = true;
        MarkDirty();
    }
}
