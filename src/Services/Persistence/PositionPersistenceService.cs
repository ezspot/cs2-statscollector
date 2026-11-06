using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using statsCollector.Config;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public abstract class PositionEvent
{
    public string? MatchUuid { get; init; }
    public int RoundNumber { get; init; }
}

/// <summary>Queued at a round restore. The position rows of the voided rounds were enqueued before it, so they are
/// written by the time the writer reaches it and it removes them; the replay's rows follow it and stay.</summary>
public sealed class VoidRoundsMarker : PositionEvent
{
    public VoidRoundsMarker(string matchUuid, int fromRound)
    {
        MatchUuid = matchUuid;
        RoundNumber = fromRound;
    }

    public int FromRound => RoundNumber;
}

public sealed class KillPositionEvent : PositionEvent
{
    public ulong KillerSteamId { get; init; }
    public ulong VictimSteamId { get; init; }
    public float KillerX { get; init; }
    public float KillerY { get; init; }
    public float KillerZ { get; init; }
    public float VictimX { get; init; }
    public float VictimY { get; init; }
    public float VictimZ { get; init; }
    public string Weapon { get; init; } = string.Empty;
    public bool IsHeadshot { get; init; }
    public bool IsWallbang { get; init; }
    public float Distance { get; init; }
    public int KillerTeam { get; init; }
    public int VictimTeam { get; init; }
    public string MapName { get; init; } = string.Empty;
    public int RoundTime { get; init; }
}

public sealed class DeathPositionEvent : PositionEvent
{
    public ulong SteamId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public string CauseOfDeath { get; init; } = string.Empty;
    public bool IsHeadshot { get; init; }
    public int Team { get; init; }
    public string MapName { get; init; } = string.Empty;
    public int RoundTime { get; init; }
}

public sealed class UtilityPositionEvent : PositionEvent
{
    public ulong SteamId { get; init; }
    public float ThrowX { get; init; }
    public float ThrowY { get; init; }
    public float ThrowZ { get; init; }
    public float LandX { get; init; }
    public float LandY { get; init; }
    public float LandZ { get; init; }
    public int UtilityType { get; init; }
    public int EntityId { get; init; }
    public string LifecycleStartEvent { get; init; } = string.Empty;
    public string LifecycleEndEvent { get; init; } = string.Empty;
    public int LifecycleDurationMs { get; init; }
    public int OpponentsAffected { get; init; }
    public int TeammatesAffected { get; init; }
    public int Damage { get; init; }
    public string MapName { get; init; } = string.Empty;
    public int RoundTime { get; init; }
}

public interface IPositionPersistenceService : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    bool TryEnqueue(PositionEvent @event);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Removes the position rows of rounds <paramref name="fromRound"/> on, after everything queued so far.</summary>
    void VoidRounds(string matchUuid, int fromRound);
}

public sealed class PositionPersistenceService : IPositionPersistenceService
{
    private const int MaxRetryQueue = 5000;

    private readonly IPositionTrackingService _repository;
    private readonly ILogger<PositionPersistenceService> _logger;
    private readonly Channel<PositionEvent> _channel;
    private readonly ConcurrentQueue<PositionEvent> _retryQueue = new();

    private Task? _processingTask;
    private CancellationTokenSource? _linkedCts;
    private volatile bool _started;
    // A restore's void waits here until the database has taken it; nothing is written before it.
    private VoidRoundsMarker? _pendingVoid;

    public PositionPersistenceService(
        IPositionTrackingService repository,
        ILogger<PositionPersistenceService> logger,
        PluginConfig config)
    {
        _repository = repository;
        _logger = logger;

        var capacity = Math.Max(100, config.PersistenceChannelCapacity);
        _channel = Channel.CreateBounded<PositionEvent>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        _linkedCts = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessLoopAsync(_linkedCts.Token), _linkedCts.Token);

        _logger.LogInformation("PositionPersistenceService started");
        return Task.CompletedTask;
    }

    // Events recorded before StartAsync (startup waits for the schema) are buffered in the channel, like stats updates.
    public bool TryEnqueue(PositionEvent @event)
    {
        if (_channel.Writer.TryWrite(@event)) return true;

        _logger.LogWarning("Position event dropped: channel full or stopped. Type: {Type}", @event.GetType().Name);
        return false;
    }

    public void VoidRounds(string matchUuid, int fromRound)
    {
        if (!_channel.Writer.TryWrite(new VoidRoundsMarker(matchUuid, fromRound)))
            _logger.LogError("Position channel full: the position rows of rounds {FromRound}+ of match {Match} voided by a restore stay next to the replayed rounds.", fromRound, matchUuid);
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
            _logger.LogWarning("Position persistence stop was canceled. {Pending} queued items may be unflushed.", _channel.Reader.Count);
            if (_linkedCts is not null)
            {
                await _linkedCts.CancelAsync().ConfigureAwait(false);
            }
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
        // Near the breaker's 60 s: a longer wait after an outage only fills the channel.
        var maxRetryDelay = TimeSpan.FromMinutes(1);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_pendingVoid is { } pending)
                {
                    if (!await TryVoidAsync(pending, cancellationToken).ConfigureAwait(false))
                    {
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                        retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
                        continue;
                    }
                    _pendingVoid = null;
                }

                if (_retryQueue.IsEmpty && !await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) break;

                var batch = new List<PositionEvent>();

                // Fresh events first, so retries that keep failing cannot crowd them out. Count first: dequeuing
                // when the batch is already full would drop that event. A void marker ends the batch: the events
                // before it are written now, it is applied next, and only then are the events after it read.
                while (batch.Count < 100 && reader.TryRead(out var @event))
                {
                    if (@event is VoidRoundsMarker marker)
                    {
                        _pendingVoid = marker;
                        break;
                    }
                    batch.Add(@event);
                }

                while (batch.Count < 100 && _retryQueue.TryDequeue(out var retryEvent))
                {
                    batch.Add(retryEvent);
                }

                if (batch.Count == 0) continue;

                try
                {
                    var failed = await ProcessBatchInternalAsync(batch, cancellationToken).ConfigureAwait(false);
                    if (failed.Count == 0)
                    {
                        retryDelay = TimeSpan.FromSeconds(5);
                        _logger.LogDebug("Successfully persisted position batch of {Count} items", batch.Count);
                        continue;
                    }

                    foreach (var item in failed) Retry(item);

                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Unexpected position persistence failure");
                    foreach (var item in batch) Retry(item);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in position persistence loop");
        }
    }

    private void Retry(PositionEvent @event)
    {
        if (_retryQueue.Count < MaxRetryQueue) _retryQueue.Enqueue(@event);
        else _logger.LogCritical("Position retry buffer full. Dropped {Type}.", @event.GetType().Name);
    }

    private async Task<List<PositionEvent>> ProcessBatchInternalAsync(List<PositionEvent> batch, CancellationToken ct)
    {
        var failed = new List<PositionEvent>();

        // One write per match: its rows either all resolve the match row or none do, so a group that failed
        // because another match wasn't written yet can't leave committed rows behind to duplicate on retry.
        foreach (var match in batch.GroupBy(@event => @event.MatchUuid))
        {
            await PersistGroupAsync(match.OfType<KillPositionEvent>().ToList(), _repository.BulkTrackKillPositionsAsync, failed, ct).ConfigureAwait(false);
            await PersistGroupAsync(match.OfType<DeathPositionEvent>().ToList(), _repository.BulkTrackDeathPositionsAsync, failed, ct).ConfigureAwait(false);
            await PersistGroupAsync(match.OfType<UtilityPositionEvent>().ToList(), _repository.BulkTrackUtilityPositionsAsync, failed, ct).ConfigureAwait(false);
        }

        return failed;
    }

    private async Task PersistGroupAsync<T>(
        List<T> events,
        Func<IEnumerable<T>, CancellationToken, Task> persist,
        List<PositionEvent> failed,
        CancellationToken cancellationToken) where T : PositionEvent
    {
        if (events.Count == 0) return;

        try
        {
            await persist(events, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ResiliencePolicies.IsRetryable(ex))
        {
            // An outage keeps its events buffered however long it lasts.
            _logger.LogError(ex, "Failed to persist {PositionType} batch. Buffering {Count} items.", typeof(T).Name, events.Count);
            failed.AddRange(events);
            return;
        }
        catch (Exception ex) when (events.Count > 1)
        {
            // Rejected data fails the same way every time: find the rows and drop only those.
            _logger.LogWarning(ex, "{PositionType} batch of {Count} rejected; writing its rows one at a time.", typeof(T).Name, events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dropped a {PositionType} row of match {Match}: it cannot be written.", typeof(T).Name, events[0].MatchUuid);
            return;
        }

        foreach (var @event in events)
        {
            await PersistGroupAsync([@event], persist, failed, cancellationToken).ConfigureAwait(false);
        }
    }

    // The voided rounds' rows still waiting for a retry are dropped; those written are deleted. Only an outage is worth
    // waiting for (false): a delete that cannot work (no DELETE grant) is given up.
    private async Task<bool> TryVoidAsync(VoidRoundsMarker marker, CancellationToken cancellationToken)
    {
        var kept = new List<PositionEvent>();
        while (_retryQueue.TryDequeue(out var queued))
        {
            if (queued.MatchUuid != marker.MatchUuid || queued.RoundNumber < marker.FromRound) kept.Add(queued);
        }
        foreach (var queued in kept) _retryQueue.Enqueue(queued);

        try
        {
            await _repository.VoidRoundPositionsAsync(marker.MatchUuid!, marker.FromRound, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ResiliencePolicies.IsOutage(ex))
        {
            _logger.LogWarning(ex, "Database unavailable; the position rows of rounds {FromRound}+ of match {Match} voided by a restore are removed once it is back.", marker.FromRound, marker.MatchUuid);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the position rows of rounds {FromRound}+ of match {Match} voided by a restore; they stay next to the replayed rounds.", marker.FromRound, marker.MatchUuid);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_linkedCts != null) await _linkedCts.CancelAsync().ConfigureAwait(false);
        if (_processingTask != null)
        {
            try { await _processingTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _logger.LogCritical("Position persistence worker did not stop during disposal."); }
        }
        _linkedCts?.Dispose();
    }
}
