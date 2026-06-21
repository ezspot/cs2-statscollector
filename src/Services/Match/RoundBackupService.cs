using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IRoundBackupService
{
    void CreateSnapshot(int roundNumber);
    bool RestoreRound(int roundNumber);
    IReadOnlyList<int> GetAvailableRounds();
}

/// <summary>
/// Thin wrapper over CS2's native per-round backup system. The engine writes a full
/// game-state backup file at the start of each round when <c>mp_backup_round_file</c> is set,
/// and <c>mp_backup_restore_load_file</c> restores it — this is the only mechanism that can
/// actually roll a match back to a specific round (the old cvar restart could not).
/// </summary>
public sealed class RoundBackupService(
    ILogger<RoundBackupService> logger,
    IGameScheduler scheduler) : IRoundBackupService
{
    private const string BackupPrefix = "statscollector";

    private readonly ILogger<RoundBackupService> _logger = logger;
    private readonly IGameScheduler _scheduler = scheduler;
    private readonly HashSet<int> _rounds = [];
    private bool _backupsEnabled;

    public void CreateSnapshot(int roundNumber)
    {
        lock (_rounds) _rounds.Add(roundNumber);

        _scheduler.Schedule(() =>
        {
            if (!_backupsEnabled)
            {
                // Ensure the engine writes a backup file for every round.
                Server.ExecuteCommand($"mp_backup_round_file {BackupPrefix}");
                Server.ExecuteCommand("mp_backup_round_file_pattern %prefix%_round%round%.txt");
                _backupsEnabled = true;
            }
            _logger.LogDebug("Round {RoundNumber} backup point recorded.", roundNumber);
        });
    }

    public bool RestoreRound(int roundNumber)
    {
        bool known;
        lock (_rounds) known = _rounds.Contains(roundNumber);

        if (!known)
        {
            _logger.LogWarning("Attempted to restore round {RoundNumber} but no backup is recorded.", roundNumber);
            return false;
        }

        var fileName = $"{BackupPrefix}_round{roundNumber:D2}.txt";
        _logger.LogInformation("Restoring round {RoundNumber} from native backup {File}", roundNumber, fileName);
        _scheduler.Schedule(() => Server.ExecuteCommand($"mp_backup_restore_load_file {fileName}"));
        return true;
    }

    public IReadOnlyList<int> GetAvailableRounds()
    {
        lock (_rounds) return _rounds.OrderBy(r => r).ToList();
    }
}
