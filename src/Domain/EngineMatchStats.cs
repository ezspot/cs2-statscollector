namespace statsCollector.Domain;

/// <summary>
/// The engine's own per-match counters for one player (CSMatchStats_t), with the scoreboard score and the cash
/// spent, as read on the game thread. The difference of two readings is the change between them.
/// </summary>
public readonly record struct EngineMatchStats(
    int Kills, int Deaths, int Assists, int HeadshotKills, int Damage, int UtilityDamage, int EnemiesFlashed,
    int Enemy2Ks, int Enemy3Ks, int Enemy4Ks, int Enemy5Ks, int EntryCount, int EntryWins,
    int Clutch1v1Count, int Clutch1v1Wins, int Clutch1v2Count, int Clutch1v2Wins,
    int KillReward, int CashEarned, int MoneySaved, int LiveTime, int Score, int CashSpent)
{
    public static EngineMatchStats operator -(EngineMatchStats a, EngineMatchStats b) => new(
        a.Kills - b.Kills, a.Deaths - b.Deaths, a.Assists - b.Assists, a.HeadshotKills - b.HeadshotKills,
        a.Damage - b.Damage, a.UtilityDamage - b.UtilityDamage, a.EnemiesFlashed - b.EnemiesFlashed,
        a.Enemy2Ks - b.Enemy2Ks, a.Enemy3Ks - b.Enemy3Ks, a.Enemy4Ks - b.Enemy4Ks, a.Enemy5Ks - b.Enemy5Ks,
        a.EntryCount - b.EntryCount, a.EntryWins - b.EntryWins,
        a.Clutch1v1Count - b.Clutch1v1Count, a.Clutch1v1Wins - b.Clutch1v1Wins,
        a.Clutch1v2Count - b.Clutch1v2Count, a.Clutch1v2Wins - b.Clutch1v2Wins,
        a.KillReward - b.KillReward, a.CashEarned - b.CashEarned, a.MoneySaved - b.MoneySaved,
        a.LiveTime - b.LiveTime, a.Score - b.Score, a.CashSpent - b.CashSpent);
}
