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
    private int _enemiesFlashed;
    private int _wallbangKills;
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
    private int _multiKill2;
    private int _multiKill3;
    private int _multiKill4;
    private int _multiKill5;
    private int _entryKills;
    private int _entryDeaths;
    private int _entryKillAttempts;
    private int _entryKillAttemptWins;

    private int _damageArmor;
    private int _score;
    private int _headshotsHit;
    private int _chestHits;
    private int _stomachHits;
    private int _armHits;
    private int _legHits;
    private int _lowImpactKills;
    private int _mvpsHostage;
    private int _mvpsEliminations;
    private int _mvpsBomb;
    private decimal _clutchPoints;
    private int _revenges;
    private int _currentRoundKills;
    private int _currentRoundDeaths;
    private int _currentRoundShotsFired;

    private int _multiKillNades;
    private int _nadeKills;
    private int _highImpactKills;

    private decimal _weightedKills;

    // Round Swing: sum of win-probability deltas from this player's enemy kills, credited only on
    // rounds their team won. _currentRoundSwing accumulates within a round; CommitRoundSwing folds it
    // into the cumulative _roundSwing at round end (winners only).
    private decimal _roundSwing;
    private decimal _currentRoundSwing;

    public CombatStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public decimal WeightedKills { get => _weightedKills; set { _weightedKills = value; _markDirty(); } }

    public decimal RoundSwing { get => _roundSwing; set { _roundSwing = value; _markDirty(); } }
    public decimal CurrentRoundSwing { get => _currentRoundSwing; set { _currentRoundSwing = value; _markDirty(); } }
    public void CommitRoundSwing() { _roundSwing += _currentRoundSwing; _markDirty(); }
    public void ResetRoundSwing() { _currentRoundSwing = 0m; }

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
    public int EnemiesFlashed { get => _enemiesFlashed; set { _enemiesFlashed = value; _markDirty(); } }
    public int WallbangKills { get => _wallbangKills; set { _wallbangKills = value; _markDirty(); } }
    public int Noscopes { get => _noscopes; set { _noscopes = value; _markDirty(); } }
    public int ThroughSmokeKills { get => _throughSmokeKills; set { _throughSmokeKills = value; _markDirty(); } }
    public int BlindKills { get => _blindKills; set { _blindKills = value; _markDirty(); } }
    public int FirstKills { get => _firstKills; set { _firstKills = value; _markDirty(); } }
    public int FirstDeaths { get => _firstDeaths; set { _firstDeaths = value; _markDirty(); } }
    public int TradeKills { get => _tradeKills; set { _tradeKills = value; _markDirty(); } }
    public int TradedDeaths { get => _tradedDeaths; set { _tradedDeaths = value; _markDirty(); } }
    public int ClutchKills { get => _clutchKills; set { _clutchKills = value; _markDirty(); } }
    public int ClutchesWon { get => _clutchesWon; set { _clutchesWon = value; _markDirty(); } }
    public int ClutchesLost { get => _clutchesLost; set { _clutchesLost = value; _markDirty(); } }
    public int Revenges { get => _revenges; set { _revenges = value; _markDirty(); } }
    public int MultiKill2 { get => _multiKill2; set { _multiKill2 = value; _markDirty(); } }
    public int MultiKill3 { get => _multiKill3; set { _multiKill3 = value; _markDirty(); } }
    public int MultiKill4 { get => _multiKill4; set { _multiKill4 = value; _markDirty(); } }
    public int MultiKill5 { get => _multiKill5; set { _multiKill5 = value; _markDirty(); } }
    public int MultiKills { get => _multiKill2 + _multiKill3 + _multiKill4 + _multiKill5; }
    public int EntryKills { get => _entryKills; set { _entryKills = value; _markDirty(); } }
    public int EntryDeaths { get => _entryDeaths; set { _entryDeaths = value; _markDirty(); } }
    public int EntryKillAttempts { get => _entryKillAttempts; set { _entryKillAttempts = value; _markDirty(); } }
    public int EntryKillAttemptWins { get => _entryKillAttemptWins; set { _entryKillAttemptWins = value; _markDirty(); } }
    public int HeadshotsHit { get => _headshotsHit; set { _headshotsHit = value; _markDirty(); } }
    public int ChestHits { get => _chestHits; set { _chestHits = value; _markDirty(); } }
    public int StomachHits { get => _stomachHits; set { _stomachHits = value; _markDirty(); } }
    public int ArmHits { get => _armHits; set { _armHits = value; _markDirty(); } }
    public int LegHits { get => _legHits; set { _legHits = value; _markDirty(); } }
    public int LowImpactKills { get => _lowImpactKills; set { _lowImpactKills = value; _markDirty(); } }
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

    public void Reset()
    {
        _kills = _deaths = _assists = _headshots = _headshotKills = 0;
        _damageDealt = _damageTaken = _shotsHit = _shotsFired = 0;
        _damageArmor = _score = 0;
        _mvps = 0;
        _enemiesFlashed = 0;
        _wallbangKills = _noscopes = 0;
        _throughSmokeKills = _blindKills = 0;
        _firstKills = _firstDeaths = _tradeKills = _tradedDeaths = 0;
        _clutchKills = _clutchesWon = _clutchesLost = 0;
        _revenges = 0;
        _multiKill2 = _multiKill3 = _multiKill4 = _multiKill5 = 0;
        _entryKills = _entryDeaths = _entryKillAttempts = _entryKillAttemptWins = 0;
        _mvpsEliminations = _mvpsBomb = _mvpsHostage = 0;
        _clutchPoints = 0;
        _headshotsHit = _chestHits = _stomachHits = _armHits = _legHits = 0;
        _lowImpactKills = 0;
        _currentRoundKills = _currentRoundDeaths = _currentRoundShotsFired = 0;
        _multiKillNades = _nadeKills = _highImpactKills = 0;
        _weightedKills = 0;
        _roundSwing = _currentRoundSwing = 0;
        _markDirty();
    }
}
