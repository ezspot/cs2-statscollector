using System;

namespace statsCollector.Domain.Stats;

public sealed class BombStats
{
    private readonly Action _markDirty;
    
    private int _bombPlants;
    private int _bombDefuses;
    private int _bombPlantAttempts;
    private int _bombDefuseAttempts;
    private int _bombPlantAborts;
    private int _bombDefuseAborts;
    private int _bombDefuseWithKit;
    private int _bombDefuseWithoutKit;
    private int _bombDrops;
    private int _bombPickups;
    private int _defuserDrops;
    private int _defuserPickups;
    private int _bombKills;
    private int _bombDeaths;
    private int _clutchDefuses;
    private int _totalPlantTime;
    private int _totalDefuseTime;

    public BombStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public int BombPlants { get => _bombPlants; set { _bombPlants = value; _markDirty(); } }
    public int BombDefuses { get => _bombDefuses; set { _bombDefuses = value; _markDirty(); } }
    public int BombPlantAttempts { get => _bombPlantAttempts; set { _bombPlantAttempts = value; _markDirty(); } }
    public int BombDefuseAttempts { get => _bombDefuseAttempts; set { _bombDefuseAttempts = value; _markDirty(); } }
    public int BombPlantAborts { get => _bombPlantAborts; set { _bombPlantAborts = value; _markDirty(); } }
    public int BombDefuseAborts { get => _bombDefuseAborts; set { _bombDefuseAborts = value; _markDirty(); } }
    public int BombDefuseWithKit { get => _bombDefuseWithKit; set { _bombDefuseWithKit = value; _markDirty(); } }
    public int BombDefuseWithoutKit { get => _bombDefuseWithoutKit; set { _bombDefuseWithoutKit = value; _markDirty(); } }
    public int BombDrops { get => _bombDrops; set { _bombDrops = value; _markDirty(); } }
    public int BombPickups { get => _bombPickups; set { _bombPickups = value; _markDirty(); } }
    public int DefuserDrops { get => _defuserDrops; set { _defuserDrops = value; _markDirty(); } }
    public int DefuserPickups { get => _defuserPickups; set { _defuserPickups = value; _markDirty(); } }
    public int BombKills { get => _bombKills; set { _bombKills = value; _markDirty(); } }
    public int BombDeaths { get => _bombDeaths; set { _bombDeaths = value; _markDirty(); } }
    public int ClutchDefuses { get => _clutchDefuses; set { _clutchDefuses = value; _markDirty(); } }
    public int TotalPlantTime { get => _totalPlantTime; set { _totalPlantTime = value; _markDirty(); } }
    public int TotalDefuseTime { get => _totalDefuseTime; set { _totalDefuseTime = value; _markDirty(); } }

    public void Reset()
    {
        _bombPlants = _bombDefuses = _bombPlantAttempts = _bombDefuseAttempts = 0;
        _bombPlantAborts = _bombDefuseAborts = _bombDefuseWithKit = _bombDefuseWithoutKit = 0;
        _bombDrops = _bombPickups = _defuserDrops = _defuserPickups = 0;
        _bombKills = _bombDeaths = _clutchDefuses = 0;
        _totalPlantTime = _totalDefuseTime = 0;
        _markDirty();
    }
}
