using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    PlayerSnapshot[] CaptureSnapshots(bool onlyDirty = false, int? matchId = null, string? matchUuid = null);
    IReadOnlyCollection<ulong> GetActiveSteamIds();
    bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot, int? matchId = null, string? matchUuid = null);
}

public sealed class PlayerSessionService : IPlayerSessionService, IDisposable
{
    private readonly ConcurrentDictionary<ulong, PlayerStatsWrapper> _playerStats = [];
    private readonly ILogger<PlayerSessionService> _logger;
    private readonly IAnalyticsService _analytics;

    public PlayerSessionService(ILogger<PlayerSessionService> logger, IAnalyticsService analytics)
    {
        _logger = logger;
        _analytics = analytics;
    }

    public PlayerStats EnsurePlayer(ulong steamId, string name)
    {
        var wrapper = _playerStats.GetOrAdd(steamId, id => 
        {
            _logger.LogInformation("Creating new session for player: {Name} (SteamID: {SteamID})", name, id);
            return new PlayerStatsWrapper(new PlayerStats { SteamId = id });
        });
        
        wrapper.Lock.EnterWriteLock();
        try
        {
            if (wrapper.Stats.Name != name)
            {
                _logger.LogDebug("Updating player name in session: {OldName} -> {NewName} (SteamID: {SteamID})", wrapper.Stats.Name, name, steamId);
                wrapper.Stats.Name = name;
            }
            return wrapper.Stats;
        }
        finally
        {
            wrapper.Lock.ExitWriteLock();
        }
    }

    public bool TryGetPlayer(ulong steamId, out PlayerStats stats)
    {
        if (_playerStats.TryGetValue(steamId, out var wrapper))
        {
            stats = wrapper.Stats;
            return true;
        }
        stats = null!;
        return false;
    }

    public bool TryRemovePlayer(ulong steamId, out PlayerStats? stats)
    {
        var removed = _playerStats.TryRemove(steamId, out var wrapper);
        stats = wrapper?.Stats;
        wrapper?.Dispose();
        return removed;
    }

    public void MutatePlayer(ulong steamId, Action<PlayerStats> mutation)
    {
        if (!_playerStats.TryGetValue(steamId, out var wrapper))
        {
            return;
        }

        wrapper.Lock.EnterWriteLock();
        try
        {
            mutation(wrapper.Stats);
        }
        finally
        {
            wrapper.Lock.ExitWriteLock();
        }
    }

    public T WithPlayer<T>(ulong steamId, Func<PlayerStats, T> accessor, T defaultValue = default!)
    {
        if (!_playerStats.TryGetValue(steamId, out var wrapper))
        {
            return defaultValue;
        }

        wrapper.Lock.EnterReadLock();
        try
        {
            return accessor(wrapper.Stats);
        }
        finally
        {
            wrapper.Lock.ExitReadLock();
        }
    }

    public void ForEachPlayer(Action<PlayerStats> action)
    {
        foreach (var wrapper in _playerStats.Values)
        {
            wrapper.Lock.EnterWriteLock();
            try
            {
                action(wrapper.Stats);
            }
            finally
            {
                wrapper.Lock.ExitWriteLock();
            }
        }
    }

    public PlayerSnapshot[] CaptureSnapshots(bool onlyDirty = false, int? matchId = null, string? matchUuid = null)
    {
        var snapshots = new List<PlayerSnapshot>();
        foreach (var wrapper in _playerStats.Values)
        {
            wrapper.Lock.EnterUpgradeableReadLock();
            try
            {
                if (!onlyDirty || wrapper.Stats.IsDirty)
                {
                    snapshots.Add(_analytics.CreateSnapshot(wrapper.Stats, matchId, matchUuid));
                    if (onlyDirty)
                    {
                        wrapper.Lock.EnterWriteLock();
                        try
                        {
                            wrapper.Stats.ClearDirty();
                        }
                        finally
                        {
                            wrapper.Lock.ExitWriteLock();
                        }
                    }
                }
            }
            finally
            {
                wrapper.Lock.ExitUpgradeableReadLock();
            }
        }
        return snapshots.ToArray();
    }

    public IReadOnlyCollection<ulong> GetActiveSteamIds() => _playerStats.Keys.ToArray();

    public bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot, int? matchId = null, string? matchUuid = null)
    {
        if (_playerStats.TryGetValue(steamId, out var wrapper))
        {
            wrapper.Lock.EnterReadLock();
            try
            {
                snapshot = _analytics.CreateSnapshot(wrapper.Stats, matchId, matchUuid);
                return true;
            }
            finally
            {
                wrapper.Lock.ExitReadLock();
            }
        }
        snapshot = null!;
        return false;
    }

    public void Dispose()
    {
        foreach (var wrapper in _playerStats.Values)
        {
            wrapper.Dispose();
        }
        _playerStats.Clear();
    }

    private sealed class PlayerStatsWrapper : IDisposable
    {
        public PlayerStats Stats { get; }
        public ReaderWriterLockSlim Lock { get; }

        public PlayerStatsWrapper(PlayerStats stats)
        {
            Stats = stats;
            Lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        public void Dispose()
        {
            Lock?.Dispose();
        }
    }
}
