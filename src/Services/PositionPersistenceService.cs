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

public record PositionEvent(int? MatchId = null);

public record KillPositionEvent(
    int? MatchId,
    ulong KillerSteamId, 
    ulong VictimSteamId, 
    float KillerX, float KillerY, float KillerZ,
    float VictimX, float VictimY, float VictimZ,
    string Weapon, bool IsHeadshot, bool IsWallbang,
    float Distance, int KillerTeam, int VictimTeam,
    string MapName, int RoundNumber, int RoundTime) : PositionEvent(MatchId);

public record DeathPositionEvent(
    int? MatchId,
    ulong SteamId, float X, float Y, float Z,
    string CauseOfDeath, bool IsHeadshot, int Team,
    string MapName, int RoundNumber, int RoundTime) : PositionEvent(MatchId);

public record UtilityPositionEvent(
    int? MatchId,
    ulong SteamId, float ThrowX, float ThrowY, float ThrowZ,
    float LandX, float LandY, float LandZ,
    int UtilityType, int OpponentsAffected, int TeammatesAffected,
    int Damage, string MapName, int RoundNumber, int RoundTime) : PositionEvent(MatchId);

public record PositionTickEvent(string MapName, PlayerPositionSnapshot[] Positions) : PositionEvent((int?)null);

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
            FullMode = BoundedChannelFullMode.DropWrite, // Drop if full to protect game performance
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

        if (_channel.Writer.TryWrite(@event))
        {
            Instrumentation.PositionEventsEnqueuedCounter.Add(1);
        }
        else
        {
            Instrumentation.PositionEventsDroppedCounter.Add(1);
            _logger.LogWarning("Position event dropped: channel full. Type: {Type}", @event.GetType().Name);
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
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var killBatch = new List<KillPositionEvent>();
                var deathBatch = new List<DeathPositionEvent>();
                var utilityBatch = new List<UtilityPositionEvent>();

                while (reader.TryRead(out var @event))
                {
                    switch (@event)
                    {
                        case KillPositionEvent k: killBatch.Add(k); break;
                        case DeathPositionEvent d: deathBatch.Add(d); break;
                        case UtilityPositionEvent u: utilityBatch.Add(u); break;
                        case PositionTickEvent t:
                            // For ticks, we might want to handle them immediately or batch them differently
                            // For now, let's just log or process if needed. 
                            // In a real scenario, you'd probably write these to a high-throughput store or another table.
                            _logger.LogTrace("Processing position tick for {Count} players on {Map}", t.Positions.Length, t.MapName);
                            break;
                    }

                    if (killBatch.Count + deathBatch.Count + utilityBatch.Count >= 50) break;
                }

                if (killBatch.Count > 0 || deathBatch.Count > 0 || utilityBatch.Count > 0)
                {
                    using var activity = Instrumentation.ActivitySource.StartActivity("ProcessPositionBatch");
                    activity?.SetTag("batch.kill_count", killBatch.Count);
                    activity?.SetTag("batch.death_count", deathBatch.Count);
                    activity?.SetTag("batch.utility_count", utilityBatch.Count);

                    _logger.LogInformation("Flushing position batch: {Kills} kills, {Deaths} deaths, {Utility} utility", 
                        killBatch.Count, deathBatch.Count, utilityBatch.Count);

                    try
                    {
                        if (killBatch.Count > 0) 
                        {
                            var matchId = killBatch[0].MatchId;
                            await _repository.BulkTrackKillPositionsAsync(matchId, killBatch, cancellationToken).ConfigureAwait(false);
                        }
                        if (deathBatch.Count > 0) 
                        {
                            var matchId = deathBatch[0].MatchId;
                            await _repository.BulkTrackDeathPositionsAsync(matchId, deathBatch, cancellationToken).ConfigureAwait(false);
                        }
                        if (utilityBatch.Count > 0) 
                        {
                            var matchId = utilityBatch[0].MatchId;
                            await _repository.BulkTrackUtilityPositionsAsync(matchId, utilityBatch, cancellationToken).ConfigureAwait(false);
                        }
                        
                        _logger.LogDebug("Successfully persisted position batch");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to persist batch of position events. Total: {Count}", 
                            killBatch.Count + deathBatch.Count + utilityBatch.Count);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in position persistence loop");
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
