using System;

namespace statsCollector.Domain.Stats;

public sealed class UtilityStats
{
    private readonly Action _markDirty;
    
    private int _flashbangsThrown;
    private int _smokesThrown;
    private int _heGrenadesThrown;
    private int _molotovsThrown;
    private int _decoysThrown;
    private int _utilityDamage;
    private int _enemiesBlinded;
    private int _teammatesBlinded;
    private int _blindDuration;
    private int _flashAssists;
    private int _smokeAssists;
    private int _utilitySuccessCount;
    private int _utilityWasteCount;

    private int _timesBlinded;
    private int _totalBlindTime;
    private int _totalBlindTimeInflicted;
    private int _utilityDamageTaken;
    private int _effectiveFlashes;
    private int _effectiveSmokes;
    private int _effectiveHeGrenades;
    private int _effectiveMolotovs;

    private int _tacticalGrenadesThrown;

    private int _flashAssistDuration; // ms
    private int _teamFlashDuration; // ms

    public UtilityStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public int FlashAssistDuration { get => _flashAssistDuration; set { _flashAssistDuration = value; _markDirty(); } }
    public int TeamFlashDuration { get => _teamFlashDuration; set { _teamFlashDuration = value; _markDirty(); } }
    public int WastedFlashes { get => _utilityWasteCount; set { _utilityWasteCount = value; _markDirty(); } }

    public int FlashbangsThrown { get => _flashbangsThrown; set { _flashbangsThrown = value; _markDirty(); } }
    public int SmokesThrown { get => _smokesThrown; set { _smokesThrown = value; _markDirty(); } }
    public int HeGrenadesThrown { get => _heGrenadesThrown; set { _heGrenadesThrown = value; _markDirty(); } }
    public int MolotovsThrown { get => _molotovsThrown; set { _molotovsThrown = value; _markDirty(); } }
    public int DecoysThrown { get => _decoysThrown; set { _decoysThrown = value; _markDirty(); } }
    public int UtilityDamage { get => _utilityDamage; set { _utilityDamage = value; _markDirty(); } }
    public int EnemiesBlinded { get => _enemiesBlinded; set { _enemiesBlinded = value; _markDirty(); } }
    public int TeammatesBlinded { get => _teammatesBlinded; set { _teammatesBlinded = value; _markDirty(); } }
    public int BlindDuration { get => _blindDuration; set { _blindDuration = value; _markDirty(); } }
    public int FlashAssists { get => _flashAssists; set { _flashAssists = value; _markDirty(); } }
    public int SmokeAssists { get => _smokeAssists; set { _smokeAssists = value; _markDirty(); } }
    public int UtilitySuccessCount { get => _utilitySuccessCount; set { _utilitySuccessCount = value; _markDirty(); } }
    public int UtilityWasteCount { get => _utilityWasteCount; set { _utilityWasteCount = value; _markDirty(); } }
    public int TimesBlinded { get => _timesBlinded; set { _timesBlinded = value; _markDirty(); } }
    public int TacticalGrenadesThrown { get => _tacticalGrenadesThrown; set { _tacticalGrenadesThrown = value; _markDirty(); } }
    public int TotalBlindTime { get => _totalBlindTime; set { _totalBlindTime = value; _markDirty(); } }
    public int TotalBlindTimeInflicted { get => _totalBlindTimeInflicted; set { _totalBlindTimeInflicted = value; _markDirty(); } }
    public int UtilityDamageTaken { get => _utilityDamageTaken; set { _utilityDamageTaken = value; _markDirty(); } }
    public int EffectiveFlashes { get => _effectiveFlashes; set { _effectiveFlashes = value; _markDirty(); } }
    public int EffectiveSmokes { get => _effectiveSmokes; set { _effectiveSmokes = value; _markDirty(); } }
    public int EffectiveHEGrenades { get => _effectiveHeGrenades; set { _effectiveHeGrenades = value; _markDirty(); } }
    public int EffectiveMolotovs { get => _effectiveMolotovs; set { _effectiveMolotovs = value; _markDirty(); } }

    public void Reset()
    {
        _flashbangsThrown = _smokesThrown = _heGrenadesThrown = _molotovsThrown = _decoysThrown = 0;
        _utilityDamage = _enemiesBlinded = _teammatesBlinded = _blindDuration = 0;
        _flashAssists = _smokeAssists = _utilitySuccessCount = _utilityWasteCount = 0;
        _timesBlinded = _totalBlindTime = _totalBlindTimeInflicted = _utilityDamageTaken = 0;
        _effectiveFlashes = _effectiveSmokes = _effectiveHeGrenades = _effectiveMolotovs = 0;
        _tacticalGrenadesThrown = 0;
        _flashAssistDuration = 0;
        _teamFlashDuration = 0;
        _markDirty();
    }
}
