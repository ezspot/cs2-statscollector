using System;
using System.Collections.Generic;
namespace statsCollector.Domain;

public record ScrimRecoveryData(
    ScrimState State,
    int? MatchId,
    List<ulong> Team1,
    List<ulong> Team2,
    Dictionary<int, ulong> Captains,
    string? SelectedMap,
    DateTime SavedAtUtc,
    // New fields for exact sub-state recovery
    Dictionary<ulong, bool> ReadyPlayers = null!,
    Dictionary<string, int> MapVotes = null!,
    Dictionary<ulong, string> PlayerVotes = null!,
    List<ulong> PickPool = null!,
    bool Team1Picking = true,
    int PickTurn = 0,
    int KnifeWinnerTeam = 0
);
