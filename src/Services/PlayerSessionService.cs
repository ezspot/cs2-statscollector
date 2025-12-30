using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
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
    PlayerSnapshot[] CaptureSnapshots(bool onlyDirty = false, int? matchId = null);
    IReadOnlyCollection<ulong> GetActiveSteamIds();
    bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot, int? matchId = null);
}

public sealed class PlayerSessionService : IPlayerSessionService
{
    private readonly ConcurrentDictionary<ulong, PlayerStats> _playerStats = [];
    private readonly ILogger<PlayerSessionService> _logger;

    private readonly IAnalyticsService _analytics;

    public PlayerSessionService(ILogger<PlayerSessionService> logger, IAnalyticsService analytics)
    {
        _logger = logger;
        _analytics = analytics;
    }

    public PlayerStats EnsurePlayer(ulong steamId, string name)
    {
        var stats = _playerStats.GetOrAdd(steamId, id => 
        {
            _logger.LogInformation("Creating new session for player: {Name} (SteamID: {SteamID})", name, id);
            return new PlayerStats { SteamId = id };
        });
        
        lock (stats.SyncRoot)
        {
            if (stats.Name != name)
            {
                _logger.LogDebug("Updating player name in session: {OldName} -> {NewName} (SteamID: {SteamID})", stats.Name, name, steamId);
                stats.Name = name;
            }
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

    public PlayerSnapshot[] CaptureSnapshots(bool onlyDirty = false, int? matchId = null)
    {
        var snapshots = new List<PlayerSnapshot>();
        foreach (var stats in _playerStats.Values)
        {
            lock (stats.SyncRoot)
            {
                if (!onlyDirty || stats.IsDirty)
                {
                    snapshots.Add(_analytics.CreateSnapshot(stats, matchId));
                    if (onlyDirty) stats.ClearDirty();
                }
            }
        }
        return snapshots.ToArray();
    }

    public IReadOnlyCollection<ulong> GetActiveSteamIds() => _playerStats.Keys.ToArray();

    public bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot, int? matchId = null)
    {
        if (_playerStats.TryGetValue(steamId, out var stats))
        {
            snapshot = _analytics.CreateSnapshot(stats, matchId);
            return true;
        }
        snapshot = null!;
        return false;
    }

    public void Clear() => _playerStats.Clear();
}
