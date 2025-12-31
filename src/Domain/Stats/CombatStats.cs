using System;

namespace statsCollector.Domain.Stats;

public sealed class CombatStats
{
    private readonly Action _markDirty;
    
    private int _kills;
    private int _deaths;
    private int _assists;
    private int _headshots;
    private int _headshotKills;
    private int _damageDealt;
    private int _damageTaken;
    private int _shotsHit;
    private int _shotsFired;
    private int _mvps;
    private int _teamKills;
    private int _suicides;
    private int _enemiesFlashed;
    private int _friendliesFlashed;
    private int _knifeKills;
    private int _wallbangKills;
    private int _collateralKills;
    private int _noscopes;
    private int _throughSmokeKills;
    private int _blindKills;
    private int _firstKills;
    private int _firstDeaths;
    private int _tradeKills;
    private int _tradedDeaths;
    private int _clutchKills;
    private int _clutchesWon;
    private int _clutchesLost;
    private int _clutch1v1Won;
    private int _clutch1v2Won;
    private int _clutch1v3Won;
    private int _clutch1v4Won;
    private int _clutch1v5Won;
    private int _revengeKills;
    private int _dominationKills;
    private int _rampageKills;
    private int _killStreak;
    private int _deathStreak;
    private int _maxKillStreak;
    private int _maxDeathStreak;
    private int _multiKill2;
    private int _multiKill3;
    private int _multiKill4;
    private int _multiKill5;
    private int _entryKills;
    private int _entryDeaths;

    private int _damageArmor;
    private int _score;
    private int _headshotsHit;
    private int _chestHits;
    private int _stomachHits;
    private int _armHits;
    private int _legHits;
    private int _lowImpactKills;
    private int _tradeOpportunities;
    private int _mvpsHostage;
    private int _mvpsEliminations;
    private int _mvpsBomb;
    private decimal _clutchPoints;
    private int _noscopeKills;
    private int _thruSmokeKills;
    private int _attackerBlindKills;
    private int _flashAssistedKills;
    private int _revenges;
    private int _currentRoundKills;
    private int _currentRoundDeaths;
    private int _currentRoundShotsFired;

    private int _multiKillNades;
    private int _nadeKills;
    private int _highImpactKills;
    private int _hostagesRescued;

    public CombatStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public int Kills { get => _kills; set { _kills = value; _markDirty(); } }
    public int Deaths { get => _deaths; set { _deaths = value; _markDirty(); } }
    public int Assists { get => _assists; set { _assists = value; _markDirty(); } }
    public int Headshots { get => _headshots; set { _headshots = value; _markDirty(); } }
    public int HeadshotKills { get => _headshotKills; set { _headshotKills = value; _markDirty(); } }
    public int DamageDealt { get => _damageDealt; set { _damageDealt = value; _markDirty(); } }
    public int DamageTaken { get => _damageTaken; set { _damageTaken = value; _markDirty(); } }
    public int DamageArmor { get => _damageArmor; set { _damageArmor = value; _markDirty(); } }
    public int Score { get => _score; set { _score = value; _markDirty(); } }
    public int ShotsHit { get => _shotsHit; set { _shotsHit = value; _markDirty(); } }
    public int ShotsFired { get => _shotsFired; set { _shotsFired = value; _markDirty(); } }
    public int MVPs { get => _mvps; set { _mvps = value; _markDirty(); } }
    public int TeamKills { get => _teamKills; set { _teamKills = value; _markDirty(); } }
    public int Suicides { get => _suicides; set { _suicides = value; _markDirty(); } }
    public int EnemiesFlashed { get => _enemiesFlashed; set { _enemiesFlashed = value; _markDirty(); } }
    public int FriendliesFlashed { get => _friendliesFlashed; set { _friendliesFlashed = value; _markDirty(); } }
    public int KnifeKills { get => _knifeKills; set { _knifeKills = value; _markDirty(); } }
    public int WallbangKills { get => _wallbangKills; set { _wallbangKills = value; _markDirty(); } }
    public int CollateralKills { get => _collateralKills; set { _collateralKills = value; _markDirty(); } }
    public int Noscopes { get => _noscopes; set { _noscopes = value; _markDirty(); } }
    public int NoscopeKills { get => _noscopeKills; set { _noscopeKills = value; _markDirty(); } }
    public int ThroughSmokeKills { get => _throughSmokeKills; set { _throughSmokeKills = value; _markDirty(); } }
    public int ThruSmokeKills { get => _thruSmokeKills; set { _thruSmokeKills = value; _markDirty(); } }
    public int BlindKills { get => _blindKills; set { _blindKills = value; _markDirty(); } }
    public int AttackerBlindKills { get => _attackerBlindKills; set { _attackerBlindKills = value; _markDirty(); } }
    public int FlashAssistedKills { get => _flashAssistedKills; set { _flashAssistedKills = value; _markDirty(); } }
    public int FirstKills { get => _firstKills; set { _firstKills = value; _markDirty(); } }
    public int FirstDeaths { get => _firstDeaths; set { _firstDeaths = value; _markDirty(); } }
    public int TradeKills { get => _tradeKills; set { _tradeKills = value; _markDirty(); } }
    public int TradedDeaths { get => _tradedDeaths; set { _tradedDeaths = value; _markDirty(); } }
    public int ClutchKills { get => _clutchKills; set { _clutchKills = value; _markDirty(); } }
    public int ClutchesWon { get => _clutchesWon; set { _clutchesWon = value; _markDirty(); } }
    public int ClutchesLost { get => _clutchesLost; set { _clutchesLost = value; _markDirty(); } }
    public int Clutch1v1Won { get => _clutch1v1Won; set { _clutch1v1Won = value; _markDirty(); } }
    public int Clutch1v2Won { get => _clutch1v2Won; set { _clutch1v2Won = value; _markDirty(); } }
    public int Clutch1v3Won { get => _clutch1v3Won; set { _clutch1v3Won = value; _markDirty(); } }
    public int Clutch1v4Won { get => _clutch1v4Won; set { _clutch1v4Won = value; _markDirty(); } }
    public int Clutch1v5Won { get => _clutch1v5Won; set { _clutch1v5Won = value; _markDirty(); } }
    public int RevengeKills { get => _revengeKills; set { _revengeKills = value; _markDirty(); } }
    public int Revenges { get => _revenges; set { _revenges = value; _markDirty(); } }
    public int DominationKills { get => _dominationKills; set { _dominationKills = value; _markDirty(); } }
    public int RampageKills { get => _rampageKills; set { _rampageKills = value; _markDirty(); } }
    public int KillStreak { get => _killStreak; set { _killStreak = value; _markDirty(); } }
    public int DeathStreak { get => _deathStreak; set { _deathStreak = value; _markDirty(); } }
    public int MaxKillStreak { get => _maxKillStreak; set { _maxKillStreak = value; _markDirty(); } }
    public int MaxDeathStreak { get => _maxDeathStreak; set { _maxDeathStreak = value; _markDirty(); } }
    public int MultiKill2 { get => _multiKill2; set { _multiKill2 = value; _markDirty(); } }
    public int MultiKill3 { get => _multiKill3; set { _multiKill3 = value; _markDirty(); } }
    public int MultiKill4 { get => _multiKill4; set { _multiKill4 = value; _markDirty(); } }
    public int MultiKill5 { get => _multiKill5; set { _multiKill5 = value; _markDirty(); } }
    public int EntryKills { get => _entryKills; set { _entryKills = value; _markDirty(); } }
    public int EntryDeaths { get => _entryDeaths; set { _entryDeaths = value; _markDirty(); } }
    public int HeadshotsHit { get => _headshotsHit; set { _headshotsHit = value; _markDirty(); } }
    public int ChestHits { get => _chestHits; set { _chestHits = value; _markDirty(); } }
    public int StomachHits { get => _stomachHits; set { _stomachHits = value; _markDirty(); } }
    public int ArmHits { get => _armHits; set { _armHits = value; _markDirty(); } }
    public int LegHits { get => _legHits; set { _legHits = value; _markDirty(); } }
    public int LowImpactKills { get => _lowImpactKills; set { _lowImpactKills = value; _markDirty(); } }
    public int TradeOpportunities { get => _tradeOpportunities; set { _tradeOpportunities = value; _markDirty(); } }
    public int MvpsHostage { get => _mvpsHostage; set { _mvpsHostage = value; _markDirty(); } }
    public int MvpsEliminations { get => _mvpsEliminations; set { _mvpsEliminations = value; _markDirty(); } }
    public int MvpsBomb { get => _mvpsBomb; set { _mvpsBomb = value; _markDirty(); } }
    public decimal ClutchPoints { get => _clutchPoints; set { _clutchPoints = value; _markDirty(); } }
    public int CurrentRoundKills { get => _currentRoundKills; set { _currentRoundKills = value; _markDirty(); } }
    public int CurrentRoundDeaths { get => _currentRoundDeaths; set { _currentRoundDeaths = value; _markDirty(); } }
    public int CurrentRoundShotsFired { get => _currentRoundShotsFired; set { _currentRoundShotsFired = value; _markDirty(); } }

    public int MultiKillNades { get => _multiKillNades; set { _multiKillNades = value; _markDirty(); } }
    public int NadeKills { get => _nadeKills; set { _nadeKills = value; _markDirty(); } }
    public int HighImpactKills { get => _highImpactKills; set { _highImpactKills = value; _markDirty(); } }
    public int HostagesRescued { get => _hostagesRescued; set { _hostagesRescued = value; _markDirty(); } }

    public void Reset()
    {
        _kills = _deaths = _assists = _headshots = _headshotKills = 0;
        _damageDealt = _damageTaken = _shotsHit = _shotsFired = 0;
        _damageArmor = _score = 0;
        _mvps = _teamKills = _suicides = 0;
        _enemiesFlashed = _friendliesFlashed = 0;
        _knifeKills = _wallbangKills = _collateralKills = _noscopes = 0;
        _noscopeKills = _throughSmokeKills = _thruSmokeKills = _blindKills = 0;
        _attackerBlindKills = _flashAssistedKills = 0;
        _firstKills = _firstDeaths = _tradeKills = _tradedDeaths = 0;
        _clutchKills = _clutchesWon = _clutchesLost = 0;
        _clutch1v1Won = _clutch1v2Won = _clutch1v3Won = _clutch1v4Won = _clutch1v5Won = 0;
        _revengeKills = _revenges = _dominationKills = _rampageKills = 0;
        _killStreak = _deathStreak = _maxKillStreak = _maxDeathStreak = 0;
        _multiKill2 = _multiKill3 = _multiKill4 = _multiKill5 = 0;
        _entryKills = _entryDeaths = 0;
        _mvpsEliminations = _mvpsBomb = _mvpsHostage = 0;
        _clutchPoints = 0;
        _headshotsHit = _chestHits = _stomachHits = _armHits = _legHits = 0;
        _lowImpactKills = _tradeOpportunities = 0;
        _currentRoundKills = _currentRoundDeaths = _currentRoundShotsFired = 0;
        _multiKillNades = _nadeKills = _highImpactKills = _hostagesRescued = 0;
        _markDirty();
    }
}
