using System;

namespace statsCollector.Domain.Stats;

public sealed class RoundStats
{
    private readonly Action _markDirty;
    
    private int _roundsPlayed;
    private int _roundsWon;
    private int _kastRounds;
    private int _aliveOnTeamAtRoundStart;
    private int _aliveEnemyAtRoundStart;
    private bool _hadKillThisRound;
    private bool _hadAssistThisRound;
    private bool _survivedThisRound;
    private bool _didTradeThisRound;

    private int _jumps;
    private int _totalSpawns;
    private int _playtimeSeconds;
    private int _itemsDropped;
    private int _cashEarned;
    private int _roundNumber;
    private DateTime _roundStartUtc;
    private int _ctRounds;
    private int _tRounds;
    private int _tradeWindowsMissed;
    private bool _wasTradedThisRound;
    private bool _wasFlashedForKill;
    private int _pings;
    private int _footsteps;

    public RoundStats(Action markDirty)
    {
        _markDirty = markDirty;
    }

    public int RoundsPlayed { get => _roundsPlayed; set { _roundsPlayed = value; _markDirty(); } }
    public int RoundsWon { get => _roundsWon; set { _roundsWon = value; _markDirty(); } }
    public int KASTRounds { get => _kastRounds; set { _kastRounds = value; _markDirty(); } }
    public int AliveOnTeamAtRoundStart { get => _aliveOnTeamAtRoundStart; set { _aliveOnTeamAtRoundStart = value; _markDirty(); } }
    public int AliveEnemyAtRoundStart { get => _aliveEnemyAtRoundStart; set { _aliveEnemyAtRoundStart = value; _markDirty(); } }
    public bool HadKillThisRound { get => _hadKillThisRound; set { _hadKillThisRound = value; _markDirty(); } }
    public bool HadAssistThisRound { get => _hadAssistThisRound; set { _hadAssistThisRound = value; _markDirty(); } }
    public bool SurvivedThisRound { get => _survivedThisRound; set { _survivedThisRound = value; _markDirty(); } }
    public bool DidTradeThisRound { get => _didTradeThisRound; set { _didTradeThisRound = value; _markDirty(); } }
    public int Jumps { get => _jumps; set { _jumps = value; _markDirty(); } }
    public int TotalSpawns { get => _totalSpawns; set { _totalSpawns = value; _markDirty(); } }
    public int PlaytimeSeconds { get => _playtimeSeconds; set { _playtimeSeconds = value; _markDirty(); } }
    public int ItemsDropped { get => _itemsDropped; set { _itemsDropped = value; _markDirty(); } }
    public int CashEarned { get => _cashEarned; set { _cashEarned = value; _markDirty(); } }
    public int RoundNumber { get => _roundNumber; set { _roundNumber = value; _markDirty(); } }
    public DateTime RoundStartUtc { get => _roundStartUtc; set { _roundStartUtc = value; _markDirty(); } }
    public int CtRounds { get => _ctRounds; set { _ctRounds = value; _markDirty(); } }
    public int TRounds { get => _tRounds; set { _tRounds = value; _markDirty(); } }
    public int TradeWindowsMissed { get => _tradeWindowsMissed; set { _tradeWindowsMissed = value; _markDirty(); } }
    public bool WasTradedThisRound { get => _wasTradedThisRound; set { _wasTradedThisRound = value; _markDirty(); } }
    public bool WasFlashedForKill { get => _wasFlashedForKill; set { _wasFlashedForKill = value; _markDirty(); } }
    public int Pings { get => _pings; set { _pings = value; _markDirty(); } }
    public int Footsteps { get => _footsteps; set { _footsteps = value; _markDirty(); } }

    public void ResetRoundFlags()
    {
        _hadKillThisRound = false;
        _hadAssistThisRound = false;
        _survivedThisRound = false;
        _didTradeThisRound = false;
        _wasTradedThisRound = false;
        _wasFlashedForKill = false;
        _pings = 0;
        _footsteps = 0;
        _markDirty();
    }

    public void Reset()
    {
        _roundsPlayed = _roundsWon = _kastRounds = 0;
        _aliveOnTeamAtRoundStart = _aliveEnemyAtRoundStart = 0;
        _jumps = _totalSpawns = _playtimeSeconds = 0;
        _itemsDropped = _cashEarned = 0;
        _roundNumber = _ctRounds = _tRounds = _tradeWindowsMissed = 0;
        ResetRoundFlags();
    }
}
