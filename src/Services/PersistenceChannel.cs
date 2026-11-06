using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly.CircuitBreaker;
using Polly.Timeout;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

// Serialisable: the retry buffer is kept on disk across a restart (PersistenceChannel.SpillFailedBatches).
[JsonPolymorphic]
[JsonDerivedType(typeof(PlayerStatsUpdate), "player")]
[JsonDerivedType(typeof(MatchPlayerStatsUpdate), "matchPlayer")]
[JsonDerivedType(typeof(RoundStartUpdate), "roundStart")]
[JsonDerivedType(typeof(RoundEndUpdate), "roundEnd")]
[JsonDerivedType(typeof(RoundEconomyUpdate), "roundEconomy")]
[JsonDerivedType(typeof(PlayerRoundStatsUpdate), "playerRound")]
[JsonDerivedType(typeof(RoundOfficialEndUpdate), "roundOfficialEnd")]
[JsonDerivedType(typeof(RoundsVoidedUpdate), "roundsVoided")]
[JsonDerivedType(typeof(MatchStartUpdate), "matchStart")]
[JsonDerivedType(typeof(MatchEndUpdate), "matchEnd")]
public abstract record StatsUpdate(string? MatchUuid = null, int? RoundNumber = null, ulong? SteamId = null);

public sealed record PlayerStatsUpdate(PlayerSnapshot Snapshot)
    : StatsUpdate(Snapshot.MatchUuid, Snapshot.RoundNumber, Snapshot.SteamId);

public sealed record MatchPlayerStatsUpdate(PlayerSnapshot Snapshot, PlayerAnalyticsSnapshot Analytics)
    : StatsUpdate(Snapshot.MatchUuid, Snapshot.RoundNumber, Snapshot.SteamId);

public sealed record RoundStartUpdate(string MatchId, int Number)
    : StatsUpdate(MatchId, Number);

public sealed record RoundEndUpdate(string MatchId, int Number, int WinnerTeam, int WinReason, RoundBombInfo Bomb)
    : StatsUpdate(MatchId, Number);

/// <summary>Both sides' money at round start and equipment value at freeze end.</summary>
public sealed record RoundEconomyUpdate(string MatchId, int Number, RoundEconomy Economy)
    : StatsUpdate(MatchId, Number);

/// <summary>A player's totals over one round: their snapshot at round end minus the one at round start.</summary>
public sealed record PlayerRoundStatsUpdate(PlayerSnapshot RoundDelta)
    : StatsUpdate(RoundDelta.MatchUuid, RoundDelta.RoundNumber, RoundDelta.SteamId);

public sealed record RoundOfficialEndUpdate(string MatchId, int Number)
    : StatsUpdate(MatchId, Number);

/// <summary>A round restore replays rounds from <paramref name="FromRound"/> on, so their rows are dropped.</summary>
public sealed record RoundsVoidedUpdate(string MatchId, int FromRound)
    : StatsUpdate(MatchId, FromRound);

public sealed record MatchStartUpdate(string MapName, string MatchId)
    : StatsUpdate(MatchId);

public sealed record MatchEndUpdate(string MatchId)
    : StatsUpdate(MatchId);

public interface IPersistenceChannel : IHostedService
{
    bool TryWrite(StatsUpdate update);

    /// <summary>Writes everything queued so far. With a SteamID, only fails while that player's own writes are
    /// still buffered, so failures for other players don't hold it up.</summary>
    Task FlushAsync(ulong? steamId = null, CancellationToken cancellationToken = default);
}

public sealed class PersistenceChannel : BackgroundService, IPersistenceChannel
{
    private abstract record PersistenceMessage;
    private sealed record UpdateMessage(StatsUpdate Update) : PersistenceMessage;
    private sealed record FlushMessage(TaskCompletionSource Completion, ulong? SteamId) : PersistenceMessage;

    private readonly Channel<PersistenceMessage> _channel;
    private readonly ILogger<PersistenceChannel> _logger;
    private readonly IStatsRepository _repository;
    private readonly Queue<List<StatsUpdate>> _failedBatches = new();
    private readonly string _spillPath;
    private const long MaxBufferSize = 50 * 1024 * 1024;
    private long _currentBufferSize;
    // Open from construction: the channel buffers writes made before the worker's first read.
    private volatile bool _acceptingWrites = true;

    public PersistenceChannel(
        ILogger<PersistenceChannel> logger,
        IStatsRepository repository,
        PluginConfig config,
        string? spillPath = null)
    {
        _logger = logger;
        _repository = repository;
        // Unwritten batches kept across a restart, next to the scrim state. Resolved here, on the game thread (a native call).
        _spillPath = spillPath ?? Path.Combine(JsonRecoveryService.PluginDirectory, "pending_stats.json");
        _channel = Channel.CreateBounded<PersistenceMessage>(new BoundedChannelOptions(config.PersistenceChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool TryWrite(StatsUpdate update)
    {
        if (!_acceptingWrites || !_channel.Writer.TryWrite(new UpdateMessage(update)))
        {
            _logger.LogWarning(
                "Persistence channel unavailable or full. Dropped {UpdateType} for {SteamId} in {Match}",
                update.GetType().Name,
                update.SteamId,
                update.MatchUuid);
            return false;
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Persistence background worker started.");

        var batch = new List<StatsUpdate>(100);
        try
        {
            await RestoreSpilledBatchesAsync(stoppingToken).ConfigureAwait(false);
            while (await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var message))
                {
                    switch (message)
                    {
                        case UpdateMessage update:
                            batch.Add(update.Update);
                            if (batch.Count >= 100)
                            {
                                await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                                batch.Clear();
                            }
                            break;

                        case FlushMessage flush:
                            if (batch.Count > 0)
                            {
                                await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                                batch.Clear();
                            }
                            await ProcessRetryQueueAsync(stoppingToken).ConfigureAwait(false);
                            var unflushed = flush.SteamId is { } steamId
                                ? _failedBatches.Count(failed => failed.Any(pending => pending.SteamId == steamId))
                                : _failedBatches.Count;
                            if (unflushed == 0)
                            {
                                flush.Completion.TrySetResult();
                            }
                            else
                            {
                                flush.Completion.TrySetException(new InvalidOperationException(
                                    $"Unable to flush {unflushed} persistence batches."));
                            }
                            break;
                    }
                }

                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Persistence worker stopping.");
        }
        finally
        {
            _acceptingWrites = false;
            while (_channel.Reader.TryRead(out var message))
            {
                if (message is UpdateMessage update)
                {
                    batch.Add(update.Update);
                }
                else if (message is FlushMessage flush)
                {
                    flush.Completion.TrySetCanceled(stoppingToken);
                }
            }

            if (batch.Count > 0 && _failedBatches.Count > 0)
            {
                // An outage is on: retrying now would spend the unload budget before the spill below gets its turn.
                BufferFailedBatch([.. batch]);
            }
            else if (batch.Count > 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await ProcessBatchAsync(batch, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    BufferFailedBatch([.. batch]);
                }
            }
            SpillFailedBatches();
        }
    }

    // What an outage left in the retry buffer at unload is kept on disk and written at the next start.
    private void SpillFailedBatches()
    {
        if (_failedBatches.Count == 0) return;
        var updates = _failedBatches.Sum(batch => batch.Count);
        try
        {
            File.WriteAllText(_spillPath + ".tmp", JsonSerializer.Serialize(_failedBatches));
            File.Move(_spillPath + ".tmp", _spillPath, true);
            _logger.LogWarning("Kept {Batches} unwritten persistence batches ({Updates} updates) in {Path} for the next start.",
                _failedBatches.Count, updates, _spillPath);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Could not keep {Updates} unwritten updates in {Path}; they are lost.", updates, _spillPath);
        }
    }

    private async Task RestoreSpilledBatchesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_spillPath)) return;
        List<List<StatsUpdate>> batches;
        try
        {
            batches = JsonSerializer.Deserialize<List<List<StatsUpdate>>>(File.ReadAllText(_spillPath)) ?? [];
            File.Delete(_spillPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the unwritten updates kept in {Path}; the file is left in place.", _spillPath);
            return;
        }

        foreach (var batch in batches) BufferFailedBatch(batch);
        _logger.LogInformation("Restored {Batches} unwritten persistence batches ({Updates} updates) from {Path}.",
            batches.Count, batches.Sum(batch => batch.Count), _spillPath);
        await ProcessRetryQueueAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(ulong? steamId = null, CancellationToken cancellationToken = default)
    {
        if (!_acceptingWrites) return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new FlushMessage(completion, steamId), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _acceptingWrites = false;
        try
        {
            // Without a running worker nothing would ever complete the flush (e.g. startup failed before it started).
            if (ExecuteTask is { IsCompleted: false })
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _channel.Writer.WriteAsync(new FlushMessage(completion, null), cancellationToken).ConfigureAwait(false);
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (_channel.Reader.Count > 0)
            {
                _logger.LogWarning("Stopped before the database was reachable; {Count} queued updates were not written.", _channel.Reader.Count);
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<StatsUpdate> batch, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessRetryQueueAsync(cancellationToken).ConfigureAwait(false);
            if (_failedBatches.Count > 0)
            {
                BufferFailedBatch([.. batch]);
                return;
            }

            await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process persistence batch after resilience retries. Buffering in memory.");
            BufferFailedBatch([.. batch]);
        }
    }

    // A deterministic error (bad data, a missing column or privilege) fails the same way on every retry; buffered, the
    // batch would block every later write behind it. Write such a batch one update at a time and drop only the updates
    // that fail that way; a server that refuses every LOAD DATA LOCAL (local_infile=OFF) fails each of them the same
    // way, so that batch is dropped whole. Throws only for errors a retry can fix, so the batch is buffered.
    private async Task WriteAsync(IReadOnlyList<StatsUpdate> batch, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessBatchCoreAsync(batch, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsPermanent(ex) && batch.Count > 1 && !ResiliencePolicies.IsLocalInfileDisabled(ex))
        {
            _logger.LogWarning(ex, "Persistence batch of {Count} updates failed permanently; writing its updates one at a time.", batch.Count);
        }
        catch (Exception ex) when (IsPermanent(ex))
        {
            if (batch.Count == 1) LogDropped(batch[0], ex);
            else _logger.LogCritical(ex, "Dropped {Count} updates: the server refuses LOAD DATA LOCAL (local_infile=OFF). Run SET GLOBAL local_infile = 1;", batch.Count);
            return;
        }

        foreach (var update in batch)
        {
            try
            {
                await ProcessBatchCoreAsync([update], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPermanent(ex))
            {
                LogDropped(update, ex);
            }
        }
    }

    private static bool IsPermanent(Exception ex) => !ResiliencePolicies.IsRetryable(ex);

    private void LogDropped(StatsUpdate update, Exception ex) =>
        _logger.LogCritical(ex, "Dropped {UpdateType} for {SteamId} in {Match}: it cannot be written.",
            update.GetType().Name, update.SteamId, update.MatchUuid);

    private async Task ProcessBatchCoreAsync(IReadOnlyList<StatsUpdate> batch, CancellationToken cancellationToken)
    {
        var playerUpdates = new List<PlayerSnapshot>();
        var matchSummaries = new List<PlayerSnapshot>();
        var weaponStats = new List<PlayerSnapshot>();
        var analytics = new List<PlayerAnalyticsSnapshot>();
        var roundStats = new List<PlayerSnapshot>();

        foreach (var update in batch)
        {
            switch (update)
            {
                case PlayerStatsUpdate player:
                    playerUpdates.Add(player.Snapshot);
                    break;
                case MatchPlayerStatsUpdate matchPlayer:
                    matchSummaries.Add(matchPlayer.Snapshot);
                    weaponStats.Add(matchPlayer.Snapshot);
                    analytics.Add(matchPlayer.Analytics);
                    break;
                case RoundStartUpdate roundStart:
                    await _repository.StartRoundAsync(roundStart.MatchId, roundStart.Number, cancellationToken).ConfigureAwait(false);
                    break;
                case PlayerRoundStatsUpdate playerRound:
                    roundStats.Add(playerRound.RoundDelta);
                    break;
                case RoundEndUpdate roundEnd:
                    await _repository.EndRoundAsync(roundEnd.MatchId, roundEnd.Number, roundEnd.WinnerTeam, roundEnd.WinReason, roundEnd.Bomb, cancellationToken).ConfigureAwait(false);
                    break;
                case RoundEconomyUpdate economy:
                    await _repository.UpdateRoundEconomyAsync(economy.MatchId, economy.Number, economy.Economy, cancellationToken).ConfigureAwait(false);
                    break;
                case RoundOfficialEndUpdate officialEnd:
                    await _repository.OfficiallyEndRoundAsync(officialEnd.MatchId, officialEnd.Number, cancellationToken).ConfigureAwait(false);
                    break;
                case RoundsVoidedUpdate voided:
                    // Rows of the voided rounds queued before the void would otherwise be written after its delete.
                    roundStats.RemoveAll(s => s.MatchUuid == voided.MatchId && s.RoundNumber >= voided.FromRound);
                    await _repository.VoidRoundsAsync(voided.MatchId, voided.FromRound, cancellationToken).ConfigureAwait(false);
                    break;
                case MatchStartUpdate matchStart:
                    await _repository.StartMatchAsync(matchStart.MapName, matchStart.MatchId, cancellationToken).ConfigureAwait(false);
                    break;
                case MatchEndUpdate matchEnd:
                    await _repository.EndMatchAsync(matchEnd.MatchId, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (playerUpdates.Count > 0) await _repository.UpsertPlayersAsync(playerUpdates, cancellationToken).ConfigureAwait(false);
        if (matchSummaries.Count > 0) await _repository.UpsertMatchSummariesAsync(matchSummaries, cancellationToken).ConfigureAwait(false);
        if (weaponStats.Count > 0) await _repository.UpsertMatchWeaponStatsAsync(weaponStats, cancellationToken).ConfigureAwait(false);
        if (analytics.Count > 0) await _repository.UpsertPlayerAnalyticsAsync(analytics, cancellationToken).ConfigureAwait(false);
        if (roundStats.Count > 0) await _repository.UpsertPlayerRoundStatsAsync(roundStats, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Processed persistence batch of {Count} items", batch.Count);
    }

    private void BufferFailedBatch(List<StatsUpdate> batch)
    {
        var estimatedSize = batch.Count * 1024L;
        while (_failedBatches.Count > 0 && _currentBufferSize + estimatedSize > MaxBufferSize)
        {
            var oldest = _failedBatches.Dequeue();
            _currentBufferSize -= oldest.Count * 1024L;
            _logger.LogCritical("Persistence retry buffer full. Dropped the oldest batch of {Count} updates.", oldest.Count);
        }

        if (estimatedSize > MaxBufferSize) return;

        _failedBatches.Enqueue(batch);
        _currentBufferSize += estimatedSize;
    }

    private async Task ProcessRetryQueueAsync(CancellationToken cancellationToken)
    {
        var batchesToProcess = _failedBatches.Count;
        for (var i = 0; i < batchesToProcess; i++)
        {
            var batch = _failedBatches.Peek();
            try
            {
                await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                _failedBatches.Dequeue();
                _currentBufferSize -= batch.Count * 1024L;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persistence retry remains unavailable; preserving buffered update order.");
                break;
            }
        }
    }
}
