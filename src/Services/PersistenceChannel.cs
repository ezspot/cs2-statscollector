using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public abstract record StatsUpdate(string? MatchUuid = null, int? RoundNumber = null, ulong? SteamId = null);

public sealed record PlayerStatsUpdate(PlayerSnapshot Snapshot)
    : StatsUpdate(Snapshot.MatchUuid, Snapshot.RoundNumber, Snapshot.SteamId);

public sealed record MatchPlayerStatsUpdate(PlayerSnapshot Snapshot, PlayerAnalyticsSnapshot Analytics)
    : StatsUpdate(Snapshot.MatchUuid, Snapshot.RoundNumber, Snapshot.SteamId);

public sealed record RoundStartUpdate(string MatchId, int Number)
    : StatsUpdate(MatchId, Number);

public sealed record RoundEndUpdate(string MatchId, int Number, int WinnerTeam, int WinReason)
    : StatsUpdate(MatchId, Number);

public sealed record RoundOfficialEndUpdate(string MatchId, int Number)
    : StatsUpdate(MatchId, Number);

public sealed record MatchStartUpdate(string MapName, string MatchId, string? SeriesUuid)
    : StatsUpdate(MatchId);

public sealed record MatchEndUpdate(string MatchId)
    : StatsUpdate(MatchId);

public interface IPersistenceChannel
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
    private const long MaxBufferSize = 50 * 1024 * 1024;
    private long _currentBufferSize;
    // Open from construction: the channel buffers writes made before the worker's first read.
    private volatile bool _acceptingWrites = true;

    public PersistenceChannel(
        ILogger<PersistenceChannel> logger,
        IStatsRepository repository,
        IOptionsMonitor<PluginConfig> config)
    {
        _logger = logger;
        _repository = repository;
        _channel = Channel.CreateBounded<PersistenceMessage>(new BoundedChannelOptions(config.CurrentValue.PersistenceChannelCapacity)
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
            foreach (var message in _channel.Reader.ReadAllRemaining())
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

            if (batch.Count > 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ProcessBatchAsync(batch, timeout.Token).ConfigureAwait(false);
            }
        }
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

            await ProcessBatchCoreAsync(batch, cancellationToken).ConfigureAwait(false);
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

    private async Task ProcessBatchCoreAsync(IReadOnlyList<StatsUpdate> batch, CancellationToken cancellationToken)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("PersistenceChannel.ProcessBatch");
        activity?.SetTag("batch.size", batch.Count);

        var playerUpdates = new List<PlayerSnapshot>();
        var matchSummaries = new List<PlayerSnapshot>();
        var weaponStats = new List<PlayerSnapshot>();
        var analytics = new List<PlayerAnalyticsSnapshot>();

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
                case RoundEndUpdate roundEnd:
                    await _repository.EndRoundAsync(roundEnd.MatchId, roundEnd.Number, roundEnd.WinnerTeam, roundEnd.WinReason, cancellationToken).ConfigureAwait(false);
                    break;
                case RoundOfficialEndUpdate officialEnd:
                    await _repository.OfficiallyEndRoundAsync(officialEnd.MatchId, officialEnd.Number, cancellationToken).ConfigureAwait(false);
                    break;
                case MatchStartUpdate matchStart:
                    await _repository.StartMatchAsync(matchStart.MapName, matchStart.MatchId, matchStart.SeriesUuid, cancellationToken).ConfigureAwait(false);
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
                await ProcessBatchCoreAsync(batch, cancellationToken).ConfigureAwait(false);
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

internal static class ChannelReaderExtensions
{
    public static IEnumerable<T> ReadAllRemaining<T>(this ChannelReader<T> reader)
    {
        while (reader.TryRead(out var item)) yield return item;
    }
}
