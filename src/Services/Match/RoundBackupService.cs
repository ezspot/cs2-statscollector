using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IRoundBackupService
{
    void Initialize();
    void Reset();
    void CreateSnapshot(int roundNumber);
    bool RestoreRound(int roundNumber);

    /// <summary>Called at round start. If this round was restored (by <see cref="RestoreRound"/>, or the round number
    /// went back: a restore from the console or a vote), rolls every player's stats back to its start and returns
    /// true.</summary>
    bool ApplyRestore(int roundNumber, bool roundNumberWentBack);

    IReadOnlyList<int> GetAvailableRounds();

    /// <summary>A player's totals at the start of a recorded round of this match, or null.</summary>
    PlayerSnapshot? GetRoundStart(int roundNumber, ulong steamId);
}

/// <summary>
/// Thin wrapper over CS2's native per-round backup system. With <c>mp_backup_round_file</c> set, and the server
/// started with <c>-allow_round_backup_files</c>, the engine writes a full game-state backup file at the start of
/// each round, and <c>mp_backup_restore_load_file</c> restores it — this is the only mechanism that can actually roll
/// a match back to a specific round (the old cvar restart could not). The stats recorded since that round's start are
/// rolled back with it.
/// </summary>
public sealed class RoundBackupService(
    ILogger<RoundBackupService> logger,
    IGameScheduler scheduler,
    IPlayerSessionService playerSessions,
    IPersistenceChannel persistenceChannel,
    IPositionPersistenceService positionPersistence,
    IMatchTrackingService matchTracker,
    IMatchStatsService matchStats) : IRoundBackupService
{
    private const string BackupPrefix = "statscollector";

    private sealed record RoundStart(PlayerSnapshot[] Players, IReadOnlyDictionary<ulong, PlayerSnapshot> Baseline);

    private readonly ILogger<RoundBackupService> _logger = logger;
    private readonly IGameScheduler _scheduler = scheduler;
    // Every player's totals, and the per-match baseline, at the start of each round of the current match.
    private readonly Dictionary<int, RoundStart> _rounds = [];
    private int _pendingRestore;

    public void Initialize()
    {
        _scheduler.Schedule(() =>
        {
            Server.ExecuteCommand($"mp_backup_round_file {BackupPrefix}");
            Server.ExecuteCommand("mp_backup_round_file_pattern %prefix%_round%round%.txt");
        });
    }

    public void Reset()
    {
        lock (_rounds)
        {
            _rounds.Clear();
            _pendingRestore = 0;
        }
    }

    public void CreateSnapshot(int roundNumber)
    {
        var start = new RoundStart(playerSessions.CaptureSnapshots(), matchStats.CopyBaseline());
        // A replayed round keeps the totals from its first start, which is where the replay starts from.
        lock (_rounds) _rounds.TryAdd(roundNumber, start);

        _logger.LogDebug("Round {RoundNumber} backup point recorded.", roundNumber);
    }

    public bool RestoreRound(int roundNumber)
    {
        bool known;
        lock (_rounds) known = _rounds.ContainsKey(roundNumber);

        if (!known)
        {
            _logger.LogWarning("Attempted to restore round {RoundNumber} but no backup is recorded.", roundNumber);
            return false;
        }

        // Backup files are numbered by rounds already played: the one written at the start of round N is
        // round(N-1) (round00 = start of the match), so restoring round N loads N-1.
        var fileName = $"{BackupPrefix}_round{roundNumber - 1:D2}.txt";
        // Without the file the engine restores nothing, and rolling the stats back would lose the rounds since.
        if (!File.Exists(Path.Combine(Server.GameDirectory, "csgo", fileName)))
        {
            _logger.LogWarning("No backup file {File} for round {RoundNumber}; the server must be started with -allow_round_backup_files.", fileName, roundNumber);
            return false;
        }

        lock (_rounds) _pendingRestore = roundNumber;
        _logger.LogInformation("Restoring round {RoundNumber} from native backup {File}", roundNumber, fileName);
        _scheduler.Schedule(() => Server.ExecuteCommand($"mp_backup_restore_load_file {fileName}"));
        return true;
    }

    // Rolling back at the restored round's start, not when the command runs, also undoes what the restore itself
    // records: the engine moves everyone to spectator (alive players die) and ends the interrupted round as a draw.
    public bool ApplyRestore(int roundNumber, bool roundNumberWentBack)
    {
        RoundStart? start;
        lock (_rounds)
        {
            // Round 1 also starts again after a match restart, so only a .restore rolls back to it.
            var restored = _pendingRestore == roundNumber || (roundNumberWentBack && roundNumber >= 2);

            // The rounds from here on are played again, so their recorded starts are out of date. A restored round keeps
            // its own: that is the state the restore returns to.
            if (restored || roundNumberWentBack)
            {
                foreach (var replayed in _rounds.Keys.Where(round => round > roundNumber || (!restored && round == roundNumber)).ToList())
                    _rounds.Remove(replayed);
            }
            if (!restored) return false;
            _pendingRestore = 0;

            if (!_rounds.TryGetValue(roundNumber, out start))
            {
                _logger.LogWarning("Round {Round} was restored, but its start was not recorded; stats were not rolled back.", roundNumber);
                return false;
            }
        }

        RollBackStats(roundNumber, start);
        return true;
    }

    // Each player's lifetime row goes back to its value at the start of the restored round, and their session restarts
    // and re-seeds from it, as on a reconnect. The per-match baseline goes back with it.
    // ponytail: players who joined during the voided rounds keep those rounds' stats (they have no round-start totals),
    // and a leaver whose only rounds of the match were voided keeps their match row.
    private void RollBackStats(int roundNumber, RoundStart start)
    {
        if (matchTracker.CurrentMatch?.MatchUuid is { } matchUuid)
        {
            persistenceChannel.TryWrite(new RoundsVoidedUpdate(matchUuid, roundNumber));
            positionPersistence.VoidRounds(matchUuid, roundNumber);
        }

        var restarted = new List<PlayerSnapshot>();
        var left = new List<PlayerSnapshot>();
        foreach (var totals in start.Players)
        {
            // Time on the server still counts.
            var playtime = playerSessions.TryGetSnapshot(totals.SteamId, out var now) ? now.PlaytimeSeconds : totals.PlaytimeSeconds;
            if (!persistenceChannel.TryWrite(new PlayerStatsUpdate(totals with { PlaytimeSeconds = playtime })))
            {
                _logger.LogError("Could not queue the round {RoundNumber} totals of {SteamId}; their stats keep the voided rounds.", roundNumber, totals.SteamId);
                continue;
            }

            restarted.Add(totals);
            if (playerSessions.TryRemovePlayer(totals.SteamId))
                playerSessions.EnsurePlayer(PlayerControllerState.From(null) with { SteamId = totals.SteamId, PlayerName = totals.Name });
            else
                left.Add(totals);
        }

        matchStats.RestoreBaseline(start.Baseline, restarted);
        // Their match row was written when they left, voided rounds included.
        foreach (var totals in left)
        {
            if (start.Baseline.TryGetValue(totals.SteamId, out var baseline)) matchStats.RewriteMatchStats(totals, baseline);
        }
        _logger.LogInformation("Rolled back the stats of {Count} players to the start of round {RoundNumber}.", restarted.Count, roundNumber);
    }

    public IReadOnlyList<int> GetAvailableRounds()
    {
        lock (_rounds) return _rounds.Keys.OrderBy(r => r).ToList();
    }

    public PlayerSnapshot? GetRoundStart(int roundNumber, ulong steamId)
    {
        lock (_rounds) return _rounds.TryGetValue(roundNumber, out var start) ? Array.Find(start.Players, p => p.SteamId == steamId) : null;
    }
}
