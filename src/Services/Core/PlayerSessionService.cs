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
    /// <summary>Raised (once) with the SteamID and a session id when a new in-memory session is created, so a
    /// consumer can seed it from the database. Fires regardless of how the session was first created
    /// (connect, spawn, or hot-reload).</summary>
    event Action<ulong, long>? PlayerSessionCreated;
    void EnsurePlayer(PlayerControllerState player);

    /// <summary>Seeds the session with this id; a seed for a session since replaced (reconnect, round restore)
    /// is ignored, since it read the row the replacement must not start from.</summary>
    void HydrateLifetime(ulong steamId, long sessionId, LifetimePlayerStatsRow? lifetime);
    bool TryRemovePlayer(ulong steamId);
    void MutatePlayer(ulong steamId, Action<PlayerStats> mutation);
    T WithPlayer<T>(ulong steamId, Func<PlayerStats, T> accessor, T defaultValue = default!);
    void ForEachPlayer(Action<PlayerStats> action);
    PlayerSnapshot[] CaptureSnapshots(bool onlyDirty = false, string? matchUuid = null);
    void MarkPersisted(ulong steamId, long version);
    IReadOnlyCollection<ulong> GetActiveSteamIds();
    bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot, string? matchUuid = null);
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

    public event Action<ulong, long>? PlayerSessionCreated;

    public void EnsurePlayer(PlayerControllerState player)
    {
        var isNew = false;
        var wrapper = _playerStats.GetOrAdd(player.SteamId, id =>
        {
            isNew = true;
            _logger.LogInformation("Creating new session for player: {Name} (SteamID: {SteamID})", player.PlayerName, id);
            return new PlayerStatsWrapper(new PlayerStats { SteamId = id, Name = player.PlayerName });
        });

        wrapper.Lock.EnterWriteLock();
        try
        {
            if (wrapper.Stats.Name != player.PlayerName)
            {
                _logger.LogDebug("Updating player name in session: {OldName} -> {NewName} (SteamID: {SteamID})", wrapper.Stats.Name, player.PlayerName, player.SteamId);
                wrapper.Stats.Name = player.PlayerName;
            }
        }
        finally
        {
            wrapper.Lock.ExitWriteLock();
        }

        // Notify outside the lock so the seeding consumer can hydrate this session from the database
        // before it is eligible for persistence (see the LifetimeSeeded guard in the capture methods).
        if (isNew) PlayerSessionCreated?.Invoke(player.SteamId, wrapper.Id);
    }

    public void HydrateLifetime(ulong steamId, long sessionId, LifetimePlayerStatsRow? lifetime)
    {
        // The player left, or the session was replaced while this seed was reading.
        if (!_playerStats.TryGetValue(steamId, out var wrapper) || wrapper.Id != sessionId) return;

        wrapper.Lock.EnterWriteLock();
        try
        {
            // Both paths self-guard against running twice. A null row means a first-time player with no
            // persisted history — mark seeded so their (fresh) session is allowed to persist.
            if (lifetime != null) wrapper.Stats.SeedLifetime(lifetime);
            else wrapper.Stats.MarkLifetimeSeeded();
        }
        finally
        {
            wrapper.Lock.ExitWriteLock();
        }
    }

    public bool TryRemovePlayer(ulong steamId)
    {
        var removed = _playerStats.TryRemove(steamId, out var wrapper);
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

    public PlayerSnapshot[] CaptureSnapshots(bool onlyDirty = false, string? matchUuid = null)
    {
        var snapshots = new List<PlayerSnapshot>();
        foreach (var wrapper in _playerStats.Values)
        {
            wrapper.Lock.EnterReadLock();
            try
            {
                // Never persist a session that hasn't been seeded from its lifetime row yet — doing so
                // would overwrite the player's career totals with a near-empty session.
                if (!wrapper.Stats.LifetimeSeeded) continue;

                if (!onlyDirty || wrapper.Stats.IsDirty)
                {
                    snapshots.Add(_analytics.CreateSnapshot(wrapper.Stats, matchUuid));
                }
            }
            finally
            {
                wrapper.Lock.ExitReadLock();
            }
        }
        return snapshots.ToArray();
    }

    public IReadOnlyCollection<ulong> GetActiveSteamIds() => _playerStats.Keys.ToArray();

    public void MarkPersisted(ulong steamId, long version)
    {
        MutatePlayer(steamId, stats => stats.ClearDirty(version));
    }

    public bool TryGetSnapshot(ulong steamId, out PlayerSnapshot snapshot, string? matchUuid = null)
    {
        if (_playerStats.TryGetValue(steamId, out var wrapper))
        {
            wrapper.Lock.EnterReadLock();
            try
            {
                // Skip until seeded so a pre-seed snapshot can't overwrite the player's lifetime row.
                if (!wrapper.Stats.LifetimeSeeded)
                {
                    snapshot = null!;
                    return false;
                }

                snapshot = _analytics.CreateSnapshot(wrapper.Stats, matchUuid);
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
        private static long _lastId;

        public PlayerStats Stats { get; }
        public ReaderWriterLockSlim Lock { get; }
        public long Id { get; } = Interlocked.Increment(ref _lastId);

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
