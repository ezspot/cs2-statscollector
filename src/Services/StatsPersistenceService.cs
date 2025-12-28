using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface IStatsPersistenceService : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task EnqueueAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class StatsPersistenceService(
    IStatsRepository repository,
    ILogger<StatsPersistenceService> logger,
    IOptionsMonitor<PluginConfig> config) : IStatsPersistenceService
{
    private readonly Channel<PlayerSnapshot> channel = Channel.CreateBounded<PlayerSnapshot>(new BoundedChannelOptions(Math.Max(50, config.CurrentValue.PersistenceChannelCapacity))
    {
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    private Task? processingTask;
    private CancellationTokenSource? linkedCts;
    private volatile bool started;
    private int consecutiveFailures;
    private DateTime circuitOpenUntilUtc;
    private readonly TimeSpan circuitOpenDuration = TimeSpan.FromSeconds(30);
    private const int CircuitBreakThreshold = 5;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (started) return Task.CompletedTask;
        started = true;

        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        processingTask = Task.Run(() => ProcessLoopAsync(linkedCts.Token), linkedCts.Token);
        return Task.CompletedTask;
    }

    public async Task EnqueueAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        if (!started) throw new InvalidOperationException("StatsPersistenceService not started.");
        if (IsCircuitOpen())
        {
            logger.LogWarning("Stats persistence circuit open until {UntilUtc}, dropping {Count} snapshots", circuitOpenUntilUtc, snapshots.Count());
            return;
        }

        foreach (var snapshot in snapshots)
        {
            await channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
            await channel.Writer.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!started) return;

        channel.Writer.Complete();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            if (processingTask != null)
            {
                await processingTask.WaitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stats persistence stop timed out after {TimeoutSeconds}s with {Pending} pending items",
                timeout.TotalSeconds, channel.Reader.Count);
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        var reader = channel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var batch = new Dictionary<ulong, PlayerSnapshot>();
            while (reader.TryRead(out var snapshot))
            {
                batch[snapshot.SteamId] = snapshot; // keep latest per player
                if (batch.Count >= config.CurrentValue.FlushConcurrency)
                {
                    await FlushBatchAsync(batch.Values.ToArray(), cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch.Values.ToArray(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushBatchAsync(IReadOnlyCollection<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0) return;

        var maxDegree = Math.Max(1, config.CurrentValue.FlushConcurrency);
        await Parallel.ForEachAsync(
            snapshots,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = cancellationToken
            },
            async (snapshot, ct) =>
            {
                try
                {
                    await repository.UpsertPlayerAsync(snapshot, ct).ConfigureAwait(false);
                    Interlocked.Exchange(ref consecutiveFailures, 0);
                }
                catch (Exception ex)
                {
                    var failures = Interlocked.Increment(ref consecutiveFailures);
                    logger.LogError(ex, "Dead-letter: failed to persist snapshot for {SteamId}/{Name} (failure {FailureCount})",
                        snapshot.SteamId, snapshot.Name, failures);

                    if (failures >= CircuitBreakThreshold)
                    {
                        circuitOpenUntilUtc = DateTime.UtcNow.Add(circuitOpenDuration);
                        logger.LogWarning("Stats persistence circuit opened for {DurationSeconds}s after {FailureCount} failures",
                            circuitOpenDuration.TotalSeconds, failures);
                    }
                }
            }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (linkedCts != null)
        {
            linkedCts.Cancel();
        }
        if (processingTask != null)
        {
            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
        }
        linkedCts?.Dispose();
    }

    private bool IsCircuitOpen() => DateTime.UtcNow < circuitOpenUntilUtc;
}
