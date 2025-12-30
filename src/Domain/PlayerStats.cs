using System;
using System.Collections.Generic;
using System.Linq;

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

    private int _aliveOnTeamAtRoundStart;
    public int AliveOnTeamAtRoundStart 
    { 
        get => _aliveOnTeamAtRoundStart; 
        set { if (_aliveOnTeamAtRoundStart != value) { _aliveOnTeamAtRoundStart = value; MarkDirty(); } } 
    }

    private int _aliveEnemyAtRoundStart;
    public int AliveEnemyAtRoundStart 
    { 
        get => _aliveEnemyAtRoundStart; 
        set { if (_aliveEnemyAtRoundStart != value) { _aliveEnemyAtRoundStart = value; MarkDirty(); } } 
    }

    private int _roundNumber;
    public int RoundNumber 
    { 
        get => _roundNumber; 
        set { if (_roundNumber != value) { _roundNumber = value; MarkDirty(); } } 
    }

    private DateTime _roundStartUtc;
    public DateTime RoundStartUtc 
    { 
        get => _roundStartUtc; 
        set { if (_roundStartUtc != value) { _roundStartUtc = value; MarkDirty(); } } 
    }

    private int _kills;
    public int Kills 
    { 
        get => _kills; 
        set { if (_kills != value) { _kills = value; MarkDirty(); } } 
    }

    private int _deaths;
    public int Deaths 
    { 
        get => _deaths; 
        set { if (_deaths != value) { _deaths = value; MarkDirty(); } } 
    }

    private int _assists;
    public int Assists 
    { 
        get => _assists; 
        set { if (_assists != value) { _assists = value; MarkDirty(); } } 
    }

    private int _headshots;
    public int Headshots 
    { 
        get => _headshots; 
        set { if (_headshots != value) { _headshots = value; MarkDirty(); } } 
    }

    private int _damageDealt;
    public int DamageDealt 
    { 
        get => _damageDealt; 
        set { if (_damageDealt != value) { _damageDealt = value; MarkDirty(); } } 
    }

    private int _damageTaken;
    public int DamageTaken 
    { 
        get => _damageTaken; 
        set { if (_damageTaken != value) { _damageTaken = value; MarkDirty(); } } 
    }

    private int _damageArmor;
    public int DamageArmor 
    { 
        get => _damageArmor; 
        set { if (_damageArmor != value) { _damageArmor = value; MarkDirty(); } } 
    }

    private int _shotsFired;
    public int ShotsFired 
    { 
        get => _shotsFired; 
        set { if (_shotsFired != value) { _shotsFired = value; MarkDirty(); } } 
    }

    private int _shotsHit;
    public int ShotsHit 
    { 
        get => _shotsHit; 
        set { if (_shotsHit != value) { _shotsHit = value; MarkDirty(); } } 
    }

    private int _mvps;
    public int Mvps 
    { 
        get => _mvps; 
        set { if (_mvps != value) { _mvps = value; MarkDirty(); } } 
    }

    private int _score;
    public int Score 
    { 
        get => _score; 
        set { if (_score != value) { _score = value; MarkDirty(); } } 
    }

    private int _roundsPlayed;
    public int RoundsPlayed 
    { 
        get => _roundsPlayed; 
        set { if (_roundsPlayed != value) { _roundsPlayed = value; MarkDirty(); } } 
    }

    private int _roundsWon;
    public int RoundsWon 
    { 
        get => _roundsWon; 
        set { if (_roundsWon != value) { _roundsWon = value; MarkDirty(); } } 
    }

    private int _totalSpawns;
    public int TotalSpawns 
    { 
        get => _totalSpawns; 
        set { if (_totalSpawns != value) { _totalSpawns = value; MarkDirty(); } } 
    }

    private int _playtimeSeconds;
    public int PlaytimeSeconds 
    { 
        get => _playtimeSeconds; 
        set { if (_playtimeSeconds != value) { _playtimeSeconds = value; MarkDirty(); } } 
    }

    private int _ctRounds;
    public int CtRounds 
    { 
        get => _ctRounds; 
        set { if (_ctRounds != value) { _ctRounds = value; MarkDirty(); } } 
    }

    private int _tRounds;
    public int TRounds 
    { 
        get => _tRounds; 
        set { if (_tRounds != value) { _tRounds = value; MarkDirty(); } } 
    }

    private int _grenadesThrown;
    public int GrenadesThrown 
    { 
        get => _grenadesThrown; 
        set { if (_grenadesThrown != value) { _grenadesThrown = value; MarkDirty(); } } 
    }

    private int _flashesThrown;
    public int FlashesThrown 
    { 
        get => _flashesThrown; 
        set { if (_flashesThrown != value) { _flashesThrown = value; MarkDirty(); } } 
    }

    private int _smokesThrown;
    public int SmokesThrown 
    { 
        get => _smokesThrown; 
        set { if (_smokesThrown != value) { _smokesThrown = value; MarkDirty(); } } 
    }

    private int _molotovsThrown;
    public int MolotovsThrown 
    { 
        get => _molotovsThrown; 
        set { if (_molotovsThrown != value) { _molotovsThrown = value; MarkDirty(); } } 
    }

    private int _heGrenadesThrown;
    public int HeGrenadesThrown 
    { 
        get => _heGrenadesThrown; 
        set { if (_heGrenadesThrown != value) { _heGrenadesThrown = value; MarkDirty(); } } 
    }

    private int _decoysThrown;
    public int DecoysThrown 
    { 
        get => _decoysThrown; 
        set { if (_decoysThrown != value) { _decoysThrown = value; MarkDirty(); } } 
    }

    private int _tacticalGrenadesThrown;
    public int TacticalGrenadesThrown 
    { 
        get => _tacticalGrenadesThrown; 
        set { if (_tacticalGrenadesThrown != value) { _tacticalGrenadesThrown = value; MarkDirty(); } } 
    }

    private int _playersBlinded;
    public int PlayersBlinded 
    { 
        get => _playersBlinded; 
        set { if (_playersBlinded != value) { _playersBlinded = value; MarkDirty(); } } 
    }

    private int _timesBlinded;
    public int TimesBlinded 
    { 
        get => _timesBlinded; 
        set { if (_timesBlinded != value) { _timesBlinded = value; MarkDirty(); } } 
    }

    private int _flashAssists;
    public int FlashAssists 
    { 
        get => _flashAssists; 
        set { if (_flashAssists != value) { _flashAssists = value; MarkDirty(); } } 
    }

    private int _totalBlindTime;
    public int TotalBlindTime 
    { 
        get => _totalBlindTime; 
        set { if (_totalBlindTime != value) { _totalBlindTime = value; MarkDirty(); } } 
    }

    private int _totalBlindTimeInflicted;
    public int TotalBlindTimeInflicted 
    { 
        get => _totalBlindTimeInflicted; 
        set { if (_totalBlindTimeInflicted != value) { _totalBlindTimeInflicted = value; MarkDirty(); } } 
    }

    private int _utilityDamageDealt;
    public int UtilityDamageDealt 
    { 
        get => _utilityDamageDealt; 
        set { if (_utilityDamageDealt != value) { _utilityDamageDealt = value; MarkDirty(); } } 
    }

    private int _utilityDamageTaken;
    public int UtilityDamageTaken 
    { 
        get => _utilityDamageTaken; 
        set { if (_utilityDamageTaken != value) { _utilityDamageTaken = value; MarkDirty(); } } 
    }

    private int _bombPlants;
    public int BombPlants 
    { 
        get => _bombPlants; 
        set { if (_bombPlants != value) { _bombPlants = value; MarkDirty(); } } 
    }

    private int _bombDefuses;
    public int BombDefuses 
    { 
        get => _bombDefuses; 
        set { if (_bombDefuses != value) { _bombDefuses = value; MarkDirty(); } } 
    }

    private int _bombPlantAttempts;
    public int BombPlantAttempts 
    { 
        get => _bombPlantAttempts; 
        set { if (_bombPlantAttempts != value) { _bombPlantAttempts = value; MarkDirty(); } } 
    }

    private int _bombPlantAborts;
    public int BombPlantAborts 
    { 
        get => _bombPlantAborts; 
        set { if (_bombPlantAborts != value) { _bombPlantAborts = value; MarkDirty(); } } 
    }

    private int _bombDefuseAttempts;
    public int BombDefuseAttempts 
    { 
        get => _bombDefuseAttempts; 
        set { if (_bombDefuseAttempts != value) { _bombDefuseAttempts = value; MarkDirty(); } } 
    }

    private int _bombDefuseAborts;
    public int BombDefuseAborts 
    { 
        get => _bombDefuseAborts; 
        set { if (_bombDefuseAborts != value) { _bombDefuseAborts = value; MarkDirty(); } } 
    }

    private int _bombDefuseWithKit;
    public int BombDefuseWithKit 
    { 
        get => _bombDefuseWithKit; 
        set { if (_bombDefuseWithKit != value) { _bombDefuseWithKit = value; MarkDirty(); } } 
    }

    private int _bombDefuseWithoutKit;
    public int BombDefuseWithoutKit 
    { 
        get => _bombDefuseWithoutKit; 
        set { if (_bombDefuseWithoutKit != value) { _bombDefuseWithoutKit = value; MarkDirty(); } } 
    }

    private int _bombDrops;
    public int BombDrops 
    { 
        get => _bombDrops; 
        set { if (_bombDrops != value) { _bombDrops = value; MarkDirty(); } } 
    }

    private int _bombPickups;
    public int BombPickups 
    { 
        get => _bombPickups; 
        set { if (_bombPickups != value) { _bombPickups = value; MarkDirty(); } } 
    }

    private int _defuserDrops;
    public int DefuserDrops 
    { 
        get => _defuserDrops; 
        set { if (_defuserDrops != value) { _defuserDrops = value; MarkDirty(); } } 
    }

    private int _defuserPickups;
    public int DefuserPickups 
    { 
        get => _defuserPickups; 
        set { if (_defuserPickups != value) { _defuserPickups = value; MarkDirty(); } } 
    }

    private int _clutchDefuses;
    public int ClutchDefuses 
    { 
        get => _clutchDefuses; 
        set { if (_clutchDefuses != value) { _clutchDefuses = value; MarkDirty(); } } 
    }

    private int _totalPlantTime;
    public int TotalPlantTime 
    { 
        get => _totalPlantTime; 
        set { if (_totalPlantTime != value) { _totalPlantTime = value; MarkDirty(); } } 
    }

    private int _totalDefuseTime;
    public int TotalDefuseTime 
    { 
        get => _totalDefuseTime; 
        set { if (_totalDefuseTime != value) { _totalDefuseTime = value; MarkDirty(); } } 
    }

    private int _bombKills;
    public int BombKills 
    { 
        get => _bombKills; 
        set { if (_bombKills != value) { _bombKills = value; MarkDirty(); } } 
    }

    private int _bombDeaths;
    public int BombDeaths 
    { 
        get => _bombDeaths; 
        set { if (_bombDeaths != value) { _bombDeaths = value; MarkDirty(); } } 
    }

    private int _hostagesRescued;
    public int HostagesRescued 
    { 
        get => _hostagesRescued; 
        set { if (_hostagesRescued != value) { _hostagesRescued = value; MarkDirty(); } } 
    }

    private int _jumps;
    public int Jumps 
    { 
        get => _jumps; 
        set { if (_jumps != value) { _jumps = value; MarkDirty(); } } 
    }

    private int _moneySpent;
    public int MoneySpent 
    { 
        get => _moneySpent; 
        set { if (_moneySpent != value) { _moneySpent = value; MarkDirty(); } } 
    }

    private int _equipmentValue;
    public int EquipmentValue 
    { 
        get => _equipmentValue; 
        set { if (_equipmentValue != value) { _equipmentValue = value; MarkDirty(); } } 
    }

    private int _itemsPurchased;
    public int ItemsPurchased 
    { 
        get => _itemsPurchased; 
        set { if (_itemsPurchased != value) { _itemsPurchased = value; MarkDirty(); } } 
    }

    private int _itemsPickedUp;
    public int ItemsPickedUp 
    { 
        get => _itemsPickedUp; 
        set { if (_itemsPickedUp != value) { _itemsPickedUp = value; MarkDirty(); } } 
    }

    private int _itemsDropped;
    public int ItemsDropped 
    { 
        get => _itemsDropped; 
        set { if (_itemsDropped != value) { _itemsDropped = value; MarkDirty(); } } 
    }

    private int _cashEarned;
    public int CashEarned 
    { 
        get => _cashEarned; 
        set { if (_cashEarned != value) { _cashEarned = value; MarkDirty(); } } 
    }

    private int _cashSpent;
    public int CashSpent 
    { 
        get => _cashSpent; 
        set { if (_cashSpent != value) { _cashSpent = value; MarkDirty(); } } 
    }

    private int _lossBonus;
    public int LossBonus 
    { 
        get => _lossBonus; 
        set { if (_lossBonus != value) { _lossBonus = value; MarkDirty(); } } 
    }

    private int _roundStartMoney;
    public int RoundStartMoney 
    { 
        get => _roundStartMoney; 
        set { if (_roundStartMoney != value) { _roundStartMoney = value; MarkDirty(); } } 
    }

    private int _roundEndMoney;
    public int RoundEndMoney 
    { 
        get => _roundEndMoney; 
        set { if (_roundEndMoney != value) { _roundEndMoney = value; MarkDirty(); } } 
    }

    private int _equipmentValueStart;
    public int EquipmentValueStart 
    { 
        get => _equipmentValueStart; 
        set { if (_equipmentValueStart != value) { _equipmentValueStart = value; MarkDirty(); } } 
    }

    private int _equipmentValueEnd;
    public int EquipmentValueEnd 
    { 
        get => _equipmentValueEnd; 
        set { if (_equipmentValueEnd != value) { _equipmentValueEnd = value; MarkDirty(); } } 
    }

    private int _enemiesFlashed;
    public int EnemiesFlashed 
    { 
        get => _enemiesFlashed; 
        set { if (_enemiesFlashed != value) { _enemiesFlashed = value; MarkDirty(); } } 
    }

    private int _teammatesFlashed;
    public int TeammatesFlashed 
    { 
        get => _teammatesFlashed; 
        set { if (_teammatesFlashed != value) { _teammatesFlashed = value; MarkDirty(); } } 
    }

    private int _effectiveFlashes;
    public int EffectiveFlashes 
    { 
        get => _effectiveFlashes; 
        set { if (_effectiveFlashes != value) { _effectiveFlashes = value; MarkDirty(); } } 
    }

    private int _effectiveSmokes;
    public int EffectiveSmokes 
    { 
        get => _effectiveSmokes; 
        set { if (_effectiveSmokes != value) { _effectiveSmokes = value; MarkDirty(); } } 
    }

    private int _effectiveHeGrenades;
    public int EffectiveHEGrenades 
    { 
        get => _effectiveHeGrenades; 
        set { if (_effectiveHeGrenades != value) { _effectiveHeGrenades = value; MarkDirty(); } } 
    }

    private int _effectiveMolotovs;
    public int EffectiveMolotovs 
    { 
        get => _effectiveMolotovs; 
        set { if (_effectiveMolotovs != value) { _effectiveMolotovs = value; MarkDirty(); } } 
    }

    private int _flashWaste;
    public int FlashWaste 
    { 
        get => _flashWaste; 
        set { if (_flashWaste != value) { _flashWaste = value; MarkDirty(); } } 
    }

    private int _multiKillNades;
    public int MultiKillNades 
    { 
        get => _multiKillNades; 
        set { if (_multiKillNades != value) { _multiKillNades = value; MarkDirty(); } } 
    }

    private int _nadeKills;
    public int NadeKills 
    { 
        get => _nadeKills; 
        set { if (_nadeKills != value) { _nadeKills = value; MarkDirty(); } } 
    }

    private int _entryKills;
    public int EntryKills 
    { 
        get => _entryKills; 
        set { if (_entryKills != value) { _entryKills = value; MarkDirty(); } } 
    }

    private int _tradeKills;
    public int TradeKills 
    { 
        get => _tradeKills; 
        set { if (_tradeKills != value) { _tradeKills = value; MarkDirty(); } } 
    }

    private int _tradedDeaths;
    public int TradedDeaths 
    { 
        get => _tradedDeaths; 
        set { if (_tradedDeaths != value) { _tradedDeaths = value; MarkDirty(); } } 
    }

    private int _highImpactKills;
    public int HighImpactKills 
    { 
        get => _highImpactKills; 
        set { if (_highImpactKills != value) { _highImpactKills = value; MarkDirty(); } } 
    }

    private int _lowImpactKills;
    public int LowImpactKills 
    { 
        get => _lowImpactKills; 
        set { if (_lowImpactKills != value) { _lowImpactKills = value; MarkDirty(); } } 
    }

    private int _tradeOpportunities;
    public int TradeOpportunities 
    { 
        get => _tradeOpportunities; 
        set { if (_tradeOpportunities != value) { _tradeOpportunities = value; MarkDirty(); } } 
    }

    private int _tradeWindowsMissed;
    public int TradeWindowsMissed 
    { 
        get => _tradeWindowsMissed; 
        set { if (_tradeWindowsMissed != value) { _tradeWindowsMissed = value; MarkDirty(); } } 
    }

    private int _multiKills;
    public int MultiKills 
    { 
        get => _multiKills; 
        set { if (_multiKills != value) { _multiKills = value; MarkDirty(); } } 
    }

    private int _openingDuelsWon;
    public int OpeningDuelsWon 
    { 
        get => _openingDuelsWon; 
        set { if (_openingDuelsWon != value) { _openingDuelsWon = value; MarkDirty(); } } 
    }

    private int _openingDuelsLost;
    public int OpeningDuelsLost 
    { 
        get => _openingDuelsLost; 
        set { if (_openingDuelsLost != value) { _openingDuelsLost = value; MarkDirty(); } } 
    }

    private int _noscopeKills;
    public int NoscopeKills 
    { 
        get => _noscopeKills; 
        set { if (_noscopeKills != value) { _noscopeKills = value; MarkDirty(); } } 
    }

    private int _thruSmokeKills;
    public int ThruSmokeKills 
    { 
        get => _thruSmokeKills; 
        set { if (_thruSmokeKills != value) { _thruSmokeKills = value; MarkDirty(); } } 
    }

    private int _attackerBlindKills;
    public int AttackerBlindKills 
    { 
        get => _attackerBlindKills; 
        set { if (_attackerBlindKills != value) { _attackerBlindKills = value; MarkDirty(); } } 
    }

    private int _flashAssistedKills;
    public int FlashAssistedKills 
    { 
        get => _flashAssistedKills; 
        set { if (_flashAssistedKills != value) { _flashAssistedKills = value; MarkDirty(); } } 
    }

    private int _wallbangKills;
    public int WallbangKills 
    { 
        get => _wallbangKills; 
        set { if (_wallbangKills != value) { _wallbangKills = value; MarkDirty(); } } 
    }

    private int _revenges;
    public int Revenges 
    { 
        get => _revenges; 
        set { if (_revenges != value) { _revenges = value; MarkDirty(); } } 
    }

    private int _clutchesWon;
    public int ClutchesWon 
    { 
        get => _clutchesWon; 
        set { if (_clutchesWon != value) { _clutchesWon = value; MarkDirty(); } } 
    }

    private int _clutchesLost;
    public int ClutchesLost 
    { 
        get => _clutchesLost; 
        set { if (_clutchesLost != value) { _clutchesLost = value; MarkDirty(); } } 
    }

    private decimal _clutchPoints;
    public decimal ClutchPoints 
    { 
        get => _clutchPoints; 
        set { if (_clutchPoints != value) { _clutchPoints = value; MarkDirty(); } } 
    }

    private int _mvpsEliminations;
    public int MvpsEliminations 
    { 
        get => _mvpsEliminations; 
        set { if (_mvpsEliminations != value) { _mvpsEliminations = value; MarkDirty(); } } 
    }

    private int _mvpsBomb;
    public int MvpsBomb 
    { 
        get => _mvpsBomb; 
        set { if (_mvpsBomb != value) { _mvpsBomb = value; MarkDirty(); } } 
    }

    private int _mvpsHostage;
    public int MvpsHostage 
    { 
        get => _mvpsHostage; 
        set { if (_mvpsHostage != value) { _mvpsHostage = value; MarkDirty(); } } 
    }

    private int _headshotsHit;
    public int HeadshotsHit 
    { 
        get => _headshotsHit; 
        set { if (_headshotsHit != value) { _headshotsHit = value; MarkDirty(); } } 
    }

    private int _chestHits;
    public int ChestHits 
    { 
        get => _chestHits; 
        set { if (_chestHits != value) { _chestHits = value; MarkDirty(); } } 
    }

    private int _stomachHits;
    public int StomachHits 
    { 
        get => _stomachHits; 
        set { if (_stomachHits != value) { _stomachHits = value; MarkDirty(); } } 
    }

    private int _armHits;
    public int ArmHits 
    { 
        get => _armHits; 
        set { if (_armHits != value) { _armHits = value; MarkDirty(); } } 
    }

    private int _legHits;
    public int LegHits 
    { 
        get => _legHits; 
        set { if (_legHits != value) { _legHits = value; MarkDirty(); } } 
    }

    private int _currentRoundKills;
    public int CurrentRoundKills 
    { 
        get => _currentRoundKills; 
        set { if (_currentRoundKills != value) { _currentRoundKills = value; MarkDirty(); } } 
    }

    private int _currentRoundDeaths;
    public int CurrentRoundDeaths 
    { 
        get => _currentRoundDeaths; 
        set { if (_currentRoundDeaths != value) { _currentRoundDeaths = value; MarkDirty(); } } 
    }

    private int _currentRoundShotsFired;
    public int CurrentRoundShotsFired 
    { 
        get => _currentRoundShotsFired; 
        set { if (_currentRoundShotsFired != value) { _currentRoundShotsFired = value; MarkDirty(); } } 
    }

    private bool _hadKillThisRound;
    public bool HadKillThisRound 
    { 
        get => _hadKillThisRound; 
        set { if (_hadKillThisRound != value) { _hadKillThisRound = value; MarkDirty(); } } 
    }

    private bool _hadAssistThisRound;
    public bool HadAssistThisRound 
    { 
        get => _hadAssistThisRound; 
        set { if (_hadAssistThisRound != value) { _hadAssistThisRound = value; MarkDirty(); } } 
    }

    private bool _survivedThisRound = true;
    public bool SurvivedThisRound 
    { 
        get => _survivedThisRound; 
        set { if (_survivedThisRound != value) { _survivedThisRound = value; MarkDirty(); } } 
    }

    private bool _wasTradedThisRound;
    public bool WasTradedThisRound 
    { 
        get => _wasTradedThisRound; 
        set { if (_wasTradedThisRound != value) { _wasTradedThisRound = value; MarkDirty(); } } 
    }

    private bool _didTradeThisRound;
    public bool DidTradeThisRound 
    { 
        get => _didTradeThisRound; 
        set { if (_didTradeThisRound != value) { _didTradeThisRound = value; MarkDirty(); } } 
    }

    private bool _wasFlashedForKill;
    public bool WasFlashedForKill 
    { 
        get => _wasFlashedForKill; 
        set { if (_wasFlashedForKill != value) { _wasFlashedForKill = value; MarkDirty(); } } 
    }

    private int _kastRounds;
    public int KASTRounds 
    { 
        get => _kastRounds; 
        set { if (_kastRounds != value) { _kastRounds = value; MarkDirty(); } } 
    }

    private PlayerTeam _currentTeam = PlayerTeam.Spectator;
    public PlayerTeam CurrentTeam 
    { 
        get => _currentTeam; 
        set { if (_currentTeam != value) { _currentTeam = value; MarkDirty(); } } 
    }

    private readonly Dictionary<string, int> _weaponKills = new();
    private readonly Dictionary<string, int> _weaponShots = new();
    private readonly Dictionary<string, int> _weaponHits = new();

    public IReadOnlyDictionary<string, int> WeaponKills => _weaponKills;
    public IReadOnlyDictionary<string, int> WeaponShots => _weaponShots;
    public IReadOnlyDictionary<string, int> WeaponHits => _weaponHits;

    public void AddWeaponKill(string weapon) { _weaponKills[weapon] = _weaponKills.GetValueOrDefault(weapon, 0) + 1; MarkDirty(); }
    public void AddWeaponShot(string weapon) { _weaponShots[weapon] = _weaponShots.GetValueOrDefault(weapon, 0) + 1; MarkDirty(); }
    public void AddWeaponHit(string weapon) { _weaponHits[weapon] = _weaponHits.GetValueOrDefault(weapon, 0) + 1; MarkDirty(); }

    public object SyncRoot { get; } = new();


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
            MarkDirty();
        }
    }
}
