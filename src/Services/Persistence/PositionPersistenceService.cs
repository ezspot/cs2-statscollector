using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public abstract class PositionEvent
{
    public string? MatchUuid { get; set; }
    public abstract void Reset();
}

public sealed class KillPositionEvent : PositionEvent
{
    public ulong KillerSteamId { get; set; }
    public ulong VictimSteamId { get; set; }
    public float KillerX { get; set; }
    public float KillerY { get; set; }
    public float KillerZ { get; set; }
    public float VictimX { get; set; }
    public float VictimY { get; set; }
    public float VictimZ { get; set; }
    public string Weapon { get; set; } = string.Empty;
    public bool IsHeadshot { get; set; }
    public bool IsWallbang { get; set; }
    public float Distance { get; set; }
    public int KillerTeam { get; set; }
    public int VictimTeam { get; set; }
    public string MapName { get; set; } = string.Empty;
    public int RoundNumber { get; set; }
    public int RoundTime { get; set; }

    public override void Reset()
    {
        MatchUuid = null;
        KillerSteamId = 0;
        VictimSteamId = 0;
        KillerX = KillerY = KillerZ = 0;
        VictimX = VictimY = VictimZ = 0;
        Weapon = string.Empty;
        IsHeadshot = false;
        IsWallbang = false;
        Distance = 0;
        KillerTeam = VictimTeam = 0;
        MapName = string.Empty;
        RoundNumber = 0;
        RoundTime = 0;
    }
}

public sealed class DeathPositionEvent : PositionEvent
{
    public ulong SteamId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string CauseOfDeath { get; set; } = string.Empty;
    public bool IsHeadshot { get; set; }
    public int Team { get; set; }
    public string MapName { get; set; } = string.Empty;
    public int RoundNumber { get; set; }
    public int RoundTime { get; set; }

    public override void Reset()
    {
        MatchUuid = null;
        SteamId = 0;
        X = Y = Z = 0;
        CauseOfDeath = string.Empty;
        IsHeadshot = false;
        Team = 0;
        MapName = string.Empty;
        RoundNumber = 0;
        RoundTime = 0;
    }
}

public sealed class UtilityPositionEvent : PositionEvent
{
    public ulong SteamId { get; set; }
    public float ThrowX { get; set; }
    public float ThrowY { get; set; }
    public float ThrowZ { get; set; }
    public float LandX { get; set; }
    public float LandY { get; set; }
    public float LandZ { get; set; }
    public int UtilityType { get; set; }
    public int OpponentsAffected { get; set; }
    public int TeammatesAffected { get; set; }
    public int Damage { get; set; }
    public string MapName { get; set; } = string.Empty;
    public int RoundNumber { get; set; }
    public int RoundTime { get; set; }

    public override void Reset()
    {
        MatchUuid = null;
        SteamId = 0;
        ThrowX = ThrowY = ThrowZ = 0;
        LandX = LandY = LandZ = 0;
        UtilityType = 0;
        OpponentsAffected = 0;
        TeammatesAffected = 0;
        Damage = 0;
        MapName = string.Empty;
        RoundNumber = 0;
        RoundTime = 0;
    }
}

public sealed class PositionTickEvent : PositionEvent
{
    public string MapName { get; set; } = string.Empty;
    public List<PlayerPositionSnapshot> Positions { get; } = new(64);

    public override void Reset()
    {
        MatchUuid = null;
        MapName = string.Empty;
        Positions.Clear();
    }
}

public interface IPositionPersistenceService : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task EnqueueAsync(PositionEvent @event, CancellationToken cancellationToken);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
    
    // Pooling methods
    KillPositionEvent GetKillEvent();
    DeathPositionEvent GetDeathEvent();
    UtilityPositionEvent GetUtilityEvent();
    PositionTickEvent GetTickEvent();
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

    private readonly ObjectPool<KillPositionEvent> _killPool;
    private readonly ObjectPool<DeathPositionEvent> _deathPool;
    private readonly ObjectPool<UtilityPositionEvent> _utilityPool;
    private readonly ObjectPool<PositionTickEvent> _tickPool;

    public PositionPersistenceService(
        IPositionTrackingService repository,
        ILogger<PositionPersistenceService> logger,
        IOptionsMonitor<PluginConfig> config)
    {
        _repository = repository;
        _logger = logger;
        
        var policy = new DefaultObjectPoolProvider();
        _killPool = policy.Create(new PositionEventPooledObjectPolicy<KillPositionEvent>());
        _deathPool = policy.Create(new PositionEventPooledObjectPolicy<DeathPositionEvent>());
        _utilityPool = policy.Create(new PositionEventPooledObjectPolicy<UtilityPositionEvent>());
        _tickPool = policy.Create(new PositionEventPooledObjectPolicy<PositionTickEvent>());

        var capacity = Math.Max(100, config.CurrentValue.PersistenceChannelCapacity);
        _channel = Channel.CreateBounded<PositionEvent>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public KillPositionEvent GetKillEvent() => _killPool.Get();
    public DeathPositionEvent GetDeathEvent() => _deathPool.Get();
    public UtilityPositionEvent GetUtilityEvent() => _utilityPool.Get();
    public PositionTickEvent GetTickEvent() => _tickPool.Get();

    private class PositionEventPooledObjectPolicy<T> : IPooledObjectPolicy<T> where T : PositionEvent, new()
    {
        public T Create() => new();
        public bool Return(T obj) { obj.Reset(); return true; }
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
            ReturnToPool(@event);
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
                    
                    // Return events to pool after successful processing
                    foreach (var @event in batch)
                    {
                        ReturnToPool(@event);
                    }

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

    private void ReturnToPool(PositionEvent @event)
    {
        switch (@event)
        {
            case KillPositionEvent k: _killPool.Return(k); break;
            case DeathPositionEvent d: _deathPool.Return(d); break;
            case UtilityPositionEvent u: _utilityPool.Return(u); break;
            case PositionTickEvent t: _tickPool.Return(t); break;
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
