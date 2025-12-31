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
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public record PositionEvent(string? MatchUuid = null);

public record KillPositionEvent(
    string? MatchUuid,
    ulong KillerSteamId, 
    ulong VictimSteamId, 
    float KillerX, float KillerY, float KillerZ,
    float VictimX, float VictimY, float VictimZ,
    string Weapon, bool IsHeadshot, bool IsWallbang,
    float Distance, int KillerTeam, int VictimTeam,
    string MapName, int RoundNumber, int RoundTime) : PositionEvent(MatchUuid);

public record DeathPositionEvent(
    string? MatchUuid,
    ulong SteamId, float X, float Y, float Z,
    string CauseOfDeath, bool IsHeadshot, int Team,
    string MapName, int RoundNumber, int RoundTime) : PositionEvent(MatchUuid);

public record UtilityPositionEvent(
    string? MatchUuid,
    ulong SteamId, float ThrowX, float ThrowY, float ThrowZ,
    float LandX, float LandY, float LandZ,
    int UtilityType, int OpponentsAffected, int TeammatesAffected,
    int Damage, string MapName, int RoundNumber, int RoundTime) : PositionEvent(MatchUuid);

public record PositionTickEvent(string MapName, string? MatchUuid, PlayerPositionSnapshot[] Positions) : PositionEvent(MatchUuid);

public interface IPositionPersistenceService : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task EnqueueAsync(PositionEvent @event, CancellationToken cancellationToken);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class PositionPersistenceService : IPositionPersistenceService
{
    private readonly IPositionTrackingService _repository;
    private readonly ILogger<PositionPersistenceService> _logger;
    private readonly Channel<PositionEvent> _channel;
    
    private readonly ConcurrentQueue<PositionEvent> _retryQueue = new();
    private readonly ActivitySource _activitySource = Instrumentation.ActivitySource;
    
    private Task? _processingTask;
    private CancellationTokenSource? _linkedCts;
    private volatile bool _started;

    public PositionPersistenceService(
        IPositionTrackingService repository,
        ILogger<PositionPersistenceService> logger,
        IOptionsMonitor<PluginConfig> config)
    {
        _repository = repository;
        _logger = logger;
        
        var capacity = Math.Max(100, config.CurrentValue.PersistenceChannelCapacity);
        _channel = Channel.CreateBounded<PositionEvent>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
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
        
        _logger.LogInformation("PositionPersistenceService started");
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(PositionEvent @event, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _logger.LogWarning("Attempted to enqueue position event but service is not started");
            return Task.CompletedTask;
        }

        using var activity = _activitySource.StartActivity("PositionPersistence.Enqueue", ActivityKind.Producer);
        activity?.SetTag("event.type", @event.GetType().Name);

        if (_channel.Writer.TryWrite(@event))
        {
            Instrumentation.PositionEventsEnqueuedCounter.Add(1);
        }
        else
        {
            Instrumentation.PositionEventsDroppedCounter.Add(1);
            _logger.LogWarning("Position event dropped: channel full. Type: {Type}", @event.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, "Channel full");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_started) return;
        
        _logger.LogInformation("Stopping PositionPersistenceService...");
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
            _logger.LogWarning("Position persistence stop timed out. {Pending} items lost.", _channel.Reader.Count);
        }
        finally
        {
            _started = false;
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        var retryDelay = TimeSpan.FromSeconds(5);
        var maxRetryDelay = TimeSpan.FromMinutes(5);

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var batch = new List<PositionEvent>();

                // 1. Drain retry queue first
                while (_retryQueue.TryDequeue(out var retryEvent) && batch.Count < 100)
                {
                    batch.Add(retryEvent);
                }

                // 2. Fill rest from channel
                while (batch.Count < 100 && reader.TryRead(out var @event))
                {
                    batch.Add(@event);
                }

                if (batch.Count == 0) continue;

                using var activity = _activitySource.StartActivity("PositionPersistence.ProcessBatch", ActivityKind.Consumer);
                activity?.SetTag("batch.size", batch.Count);

                try
                {
                    await ProcessBatchInternalAsync(batch, cancellationToken).ConfigureAwait(false);
                    
                    // Reset delay on success
                    retryDelay = TimeSpan.FromSeconds(5);
                    _logger.LogDebug("Successfully persisted position batch of {Count} items", batch.Count);
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    _logger.LogError(ex, "Failed to persist position batch. Buffering {Count} items for retry.", batch.Count);

                    // Re-queue for retry
                    foreach (var item in batch)
                    {
                        if (_retryQueue.Count < 5000) // Safety cap for in-memory buffer
                        {
                            _retryQueue.Enqueue(item);
                        }
                    }

                    // Exponential backoff
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in position persistence loop");
        }
    }

    private async Task ProcessBatchInternalAsync(List<PositionEvent> batch, CancellationToken ct)
    {
        var killBatch = batch.OfType<KillPositionEvent>().ToList();
        var deathBatch = batch.OfType<DeathPositionEvent>().ToList();
        var utilityBatch = batch.OfType<UtilityPositionEvent>().ToList();

        if (killBatch.Count > 0)
        {
            await _repository.BulkTrackKillPositionsAsync(killBatch, ct).ConfigureAwait(false);
        }

        if (deathBatch.Count > 0)
        {
            await _repository.BulkTrackDeathPositionsAsync(deathBatch, ct).ConfigureAwait(false);
        }

        if (utilityBatch.Count > 0)
        {
            await _repository.BulkTrackUtilityPositionsAsync(utilityBatch, ct).ConfigureAwait(false);
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
