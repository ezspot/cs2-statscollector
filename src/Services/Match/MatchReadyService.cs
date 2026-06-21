using System;
using System.Collections.Concurrent;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;

namespace statsCollector.Services;

public interface IMatchReadyService
{
    bool IsReady(ulong steamId);
    void SetReady(ulong steamId, bool ready);
    bool AreAllReady();
    int GetReadyCount();
    int GetRequiredCount();
}

public sealed class MatchReadyService : IMatchReadyService
{
    private readonly ConcurrentDictionary<ulong, bool> _readyStatus = new();

    public bool IsReady(ulong steamId) => _readyStatus.TryGetValue(steamId, out var ready) && ready;

    public void SetReady(ulong steamId, bool ready)
    {
        _readyStatus[steamId] = ready;
        var player = Utilities.GetPlayerFromSteamId(steamId);
        if (player != null)
        {
            var status = ready ? $"{ChatColors.Green}READY{ChatColors.Default}" : $"{ChatColors.Red}NOT READY{ChatColors.Default}";
            Server.PrintToChatAll($" {ChatColors.Blue}[Ready]{ChatColors.Default} {player.PlayerName} is now {status} ({GetReadyCount()}/{GetRequiredCount()})");
        }
    }

    public bool AreAllReady()
    {
        var activePlayers = Utilities.GetPlayers().Where(p => !p.IsBot && p.TeamNum > 1).ToList();
        if (activePlayers.Count < GetRequiredCount()) return false;

        return activePlayers.All(p => IsReady(p.SteamID));
    }

    public int GetReadyCount() => _readyStatus.Count(kvp => kvp.Value);

    public int GetRequiredCount()
    {
        var activePlayers = Utilities.GetPlayers().Where(p => !p.IsBot && p.TeamNum > 1).Count();
        return Math.Max(1, activePlayers); // Require everyone currently in game to be ready
    }
}
