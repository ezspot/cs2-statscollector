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
        Combat.KillStreak = 0; // Example of cross-cutting reset
        MarkDirty();
    }
}
