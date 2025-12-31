using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace statsCollector.Services;

public interface IMatchReadyService
{
    bool IsReady(ulong steamId);
    void SetReady(ulong steamId, bool ready);
    bool AreAllReady();
    void Reset();
    IReadOnlyCollection<ulong> GetReadyPlayers();
    int GetReadyCount();
    int GetRequiredCount();
}

public sealed class MatchReadyService : IMatchReadyService
{
    private readonly ILogger<MatchReadyService> _logger;
    private readonly ConcurrentDictionary<ulong, bool> _readyStatus = new();
    private const int MinPlayersRequired = 10; // Default competitive requirement

    public MatchReadyService(ILogger<MatchReadyService> logger)
    {
        _logger = logger;
    }

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

    public void Reset()
    {
        _readyStatus.Clear();
    }

    public IReadOnlyCollection<ulong> GetReadyPlayers() => _readyStatus.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();

    public int GetReadyCount() => _readyStatus.Count(kvp => kvp.Value);

    public int GetRequiredCount()
    {
        var activePlayers = Utilities.GetPlayers().Where(p => !p.IsBot && p.TeamNum > 1).Count();
        return Math.Max(1, activePlayers); // Require everyone currently in game to be ready
    }
}
