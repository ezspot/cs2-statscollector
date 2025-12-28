using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using CounterStrikeSharp.API.Core;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface IPlayerSessionService
{
    PlayerStats EnsurePlayer(ulong steamId, string name);
    bool TryGetPlayer(ulong steamId, out PlayerStats stats);
    bool TryRemovePlayer(ulong steamId, out PlayerStats? stats);
    void MutatePlayer(ulong steamId, Action<PlayerStats> mutation);
    T WithPlayer<T>(ulong steamId, Func<PlayerStats, T> accessor, T defaultValue = default!);
    void ForEachPlayer(Action<PlayerStats> action);
    PlayerSnapshot[] CaptureSnapshots();
    IReadOnlyCollection<ulong> GetActiveSteamIds();
    bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot);
}

public sealed class PlayerSessionService : IPlayerSessionService
{
    private readonly ConcurrentDictionary<ulong, PlayerStats> _playerStats = [];

    public PlayerStats EnsurePlayer(ulong steamId, string name)
    {
        var stats = _playerStats.GetOrAdd(steamId, id => new PlayerStats { SteamId = id });
        lock (stats.SyncRoot)
        {
            stats.Name = name;
        }
        return stats;
    }

    public bool TryGetPlayer(ulong steamId, out PlayerStats stats) => _playerStats.TryGetValue(steamId, out stats!);

    public bool TryRemovePlayer(ulong steamId, out PlayerStats? stats)
    {
        var removed = _playerStats.TryRemove(steamId, out var existing);
        stats = existing;
        return removed;
    }

    public void MutatePlayer(ulong steamId, Action<PlayerStats> mutation)
    {
        if (!_playerStats.TryGetValue(steamId, out var stats))
        {
            return;
        }

        lock (stats.SyncRoot)
        {
            mutation(stats);
        }
    }

    public T WithPlayer<T>(ulong steamId, Func<PlayerStats, T> accessor, T defaultValue = default!)
    {
        if (!_playerStats.TryGetValue(steamId, out var stats))
        {
            return defaultValue;
        }

        lock (stats.SyncRoot)
        {
            return accessor(stats);
        }
    }

    public void ForEachPlayer(Action<PlayerStats> action)
    {
        foreach (var stats in _playerStats.Values)
        {
            lock (stats.SyncRoot)
            {
                action(stats);
            }
        }
    }

    public PlayerSnapshot[] CaptureSnapshots() => _playerStats.Values.Select(ps => ps.ToSnapshot()).ToArray();

    public IReadOnlyCollection<ulong> GetActiveSteamIds() => _playerStats.Keys.ToArray();

    public bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot)
    {
        snapshot = default!;
        if (!_playerStats.TryGetValue(steamId, out var stats))
        {
            return false;
        }

        lock (stats.SyncRoot)
        {
            snapshot = stats.ToSnapshot();
            return true;
        }
    }

    public void Clear() => _playerStats.Clear();
}
