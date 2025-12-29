using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IStatsPersistenceService : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task EnqueueAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class StatsPersistenceService : IStatsPersistenceService
{
    private readonly IStatsRepository _repository;
    private readonly ILogger<StatsPersistenceService> _logger;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly Channel<PlayerSnapshot> _channel;

    private Task? _processingTask;
    private CancellationTokenSource? _linkedCts;
    private volatile bool _started;

    public StatsPersistenceService(
        IStatsRepository repository,
        ILogger<StatsPersistenceService> logger,
        IOptionsMonitor<PluginConfig> config)
    {
        _repository = repository;
        _logger = logger;
        _config = config;

        var capacity = Math.Max(100, config.CurrentValue.PersistenceChannelCapacity);
        _channel = Channel.CreateBounded<PlayerSnapshot>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite, // Protect server performance
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessLoopAsync(_linkedCts.Token), _linkedCts.Token);
        
        _logger.LogInformation("StatsPersistenceService started");
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _logger.LogWarning("Attempted to enqueue {Count} snapshots but service is not started", snapshots.Count());
            return Task.CompletedTask;
        }

        var snapshotList = snapshots.ToList();
        _logger.LogDebug("Enqueuing {Count} snapshots for persistence", snapshotList.Count);

        foreach (var snapshot in snapshotList)
        {
            if (_channel.Writer.TryWrite(snapshot))
            {
                Instrumentation.StatsEnqueuedCounter.Add(1);
            }
            else
            {
                Instrumentation.StatsDroppedCounter.Add(1);
                _logger.LogWarning("Stats snapshot for player {SteamId} ({Name}) dropped: channel full", snapshot.SteamId, snapshot.Name);
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_started) return;

        _logger.LogInformation("Stopping StatsPersistenceService...");
        _channel.Writer.Complete();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            if (_processingTask != null)
            {
                await _processingTask.WaitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stats persistence stop timed out. {Pending} snapshots lost.", _channel.Reader.Count);
        }
        finally
        {
            _started = false;
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var batch = new Dictionary<ulong, PlayerSnapshot>();
                while (reader.TryRead(out var snapshot))
                {
                    batch[snapshot.SteamId] = snapshot; // Keep latest per player in this batch
                    if (batch.Count >= _config.CurrentValue.FlushConcurrency)
                    {
                        await FlushBatchAsync(batch.Values.ToList(), cancellationToken).ConfigureAwait(false);
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch.Values.ToList(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in stats persistence loop");
        }
    }

    private async Task FlushBatchAsync(List<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("FlushStatsBatch");
        activity?.SetTag("batch.size", snapshots.Count);

        _logger.LogInformation("Flushing batch of {Count} player snapshots to database...", snapshots.Count);

        try
        {
            await _repository.UpsertPlayersAsync(snapshots, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Successfully persisted batch of {Count} snapshots", snapshots.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist batch of {Count} snapshots. SteamIDs: {SteamIds}", 
                snapshots.Count, string.Join(", ", snapshots.Select(s => s.SteamId)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_linkedCts != null) await _linkedCts.CancelAsync().ConfigureAwait(false);
        if (_processingTask != null)
        {
            try { await _processingTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _linkedCts?.Dispose();
    }
}
