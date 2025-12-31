using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using System.Text.Json;
using System.IO;

namespace statsCollector.Services;

public enum UpdateType
{
    PlayerStats,
    MatchSummary,
    WeaponStats,
    RoundStart,
    RoundEnd,
    MatchStart,
    MatchEnd
}

public record StatsUpdate(UpdateType Type, object Data, string? MatchUuid = null, int? RoundNumber = null, ulong? SteamId = null);

public interface IPersistenceChannel
{
    bool TryWrite(StatsUpdate update);
    Task FlushAsync(CancellationToken ct = default);
}

public sealed class PersistenceChannel : BackgroundService, IPersistenceChannel
{
    private readonly Channel<StatsUpdate> _channel;
    private readonly ILogger<PersistenceChannel> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly string _recoveryPath = "pending_stats.json";
    private readonly List<StatsUpdate> _batch = new(100);

    private readonly Queue<List<StatsUpdate>> _failedBatches = new();
    private readonly long _maxBufferSize = 50 * 1024 * 1024; // 50MB
    private long _currentBufferSize = 0;

    public PersistenceChannel(
        ILogger<PersistenceChannel> logger,
        IServiceProvider serviceProvider,
        IOptionsMonitor<PluginConfig> config)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _config = config;

        var options = new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<StatsUpdate>(options);

        // Load pending stats if they exist (crash recovery)
        _ = Task.Run(async () => await LoadRecoveryStateAsync());
    }

    private async Task LoadRecoveryStateAsync()
    {
        try
        {
            if (!File.Exists(_recoveryPath)) return;

            var json = await File.ReadAllTextAsync(_recoveryPath);
            var recoveredUpdates = JsonSerializer.Deserialize<List<StatsUpdate>>(json);

            if (recoveredUpdates != null && recoveredUpdates.Count > 0)
            {
                _logger.LogInformation("Recovered {Count} pending stats updates from disk.", recoveredUpdates.Count);
                foreach (var update in recoveredUpdates)
                {
                    await _channel.Writer.WriteAsync(update);
                }
            }

            File.Delete(_recoveryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persistence recovery state");
        }
    }

    public bool TryWrite(StatsUpdate update)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("PersistenceChannel.TryWrite");
        activity?.SetTag("update.type", update.Type.ToString());
        activity?.SetTag("player.steamid", update.SteamId);

        if (_channel.Writer.TryWrite(update))
        {
            Instrumentation.StatsEnqueuedCounter.Add(1, new KeyValuePair<string, object?>("type", update.Type.ToString()));
            return true;
        }

        _logger.LogWarning("Persistence channel full! Dropped update for {SteamId} in {Match}", update.SteamId, update.MatchUuid);
        Instrumentation.StatsDroppedCounter.Add(1, new KeyValuePair<string, object?>("reason", "channel_full"));
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Persistence background worker started.");

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var recoveryTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        _ = Task.Run(async () =>
        {
            while (await recoveryTimer.WaitForNextTickAsync(stoppingToken))
            {
                await SaveRecoveryStateAsync();
            }
        }, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                try
                {
                    while (_batch.Count < 100 && await _channel.Reader.WaitToReadAsync(cts.Token))
                    {
                        if (_channel.Reader.TryRead(out var update))
                        {
                            _batch.Add(update);
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timer hit, proceed to flush batch
                }

                if (_batch.Count > 0)
                {
                    await ProcessBatchAsync(_batch, stoppingToken);
                    _batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Persistence worker stopping...");
        }
        finally
        {
            await FinalFlushAsync();
        }
    }

    private async Task ProcessBatchAsync(List<StatsUpdate> batch, CancellationToken ct)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("PersistenceChannel.ProcessBatch");
        activity?.SetTag("batch.size", batch.Count);

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStatsRepository>();

        var retryCount = 0;
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            try
            {
                // Group by type for efficiency
                var playerUpdates = new List<PlayerSnapshot>();
                var matchSummaries = new List<PlayerSnapshot>();
                var weaponStats = new List<PlayerSnapshot>();

                foreach (var update in batch)
                {
                    switch (update.Type)
                    {
                        case UpdateType.PlayerStats:
                            if (update.Data is PlayerSnapshot ps) playerUpdates.Add(ps);
                            break;
                        case UpdateType.MatchSummary:
                            if (update.Data is PlayerSnapshot ms) matchSummaries.Add(ms);
                            break;
                        case UpdateType.WeaponStats:
                            if (update.Data is PlayerSnapshot ws) weaponStats.Add(ws);
                            break;
                        case UpdateType.RoundStart:
                            if (update.Data is (string mUuid, int rNum)) 
                                await repository.StartRoundAsync(mUuid, rNum, ct);
                            break;
                        case UpdateType.RoundEnd:
                            if (update.Data is (string mUuid, int rNum, int wTeam, int reason))
                                await repository.EndRoundAsync(mUuid, rNum, wTeam, reason, ct);
                            break;
                        case UpdateType.MatchStart:
                            if (update.Data is (string map, string uuid, string? sUuid))
                                await repository.StartMatchAsync(map, uuid, sUuid, ct);
                            break;
                        case UpdateType.MatchEnd:
                            if (update.Data is string matchUuid)
                                await repository.EndMatchAsync(matchUuid, ct);
                            break;
                    }
                }

                if (playerUpdates.Count > 0) await repository.UpsertPlayersAsync(playerUpdates, ct);
                if (matchSummaries.Count > 0) await repository.UpsertMatchSummariesAsync(matchSummaries, ct);
                if (weaponStats.Count > 0) await repository.UpsertMatchWeaponStatsAsync(weaponStats, ct);
                
                _logger.LogDebug("Processed persistence batch of {Count} items", batch.Count);
                
                // If we have failed batches, try to process one now
                if (_failedBatches.Count > 0)
                {
                    _ = Task.Run(async () => await ProcessRetryQueueAsync(ct), ct);
                }
                
                return; // Success
            }
            catch (Exception ex) when (retryCount < 5)
            {
                retryCount++;
                _logger.LogWarning(ex, "Failed to process persistence batch. Retry {Count}/5 after {Delay}ms", retryCount, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromTicks(delay.Ticks * 2); // Exponential backoff
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process persistence batch after max retries. Buffering in-memory.");
                BufferFailedBatch(batch);
                return; // Buffer and continue
            }
        }
    }

    private void BufferFailedBatch(List<StatsUpdate> batch)
    {
        // Simple size estimation
        var estimatedSize = batch.Count * 1024; // ~1KB per update
        if (_currentBufferSize + estimatedSize > _maxBufferSize)
        {
            _logger.LogCritical("Persistence buffer exceeded 50MB! Dropping oldest batch.");
            if (_failedBatches.TryDequeue(out var oldest))
            {
                _currentBufferSize -= oldest.Count * 1024;
            }
        }

        _failedBatches.Enqueue(batch);
        _currentBufferSize += estimatedSize;
        Instrumentation.StatsDroppedCounter.Add(batch.Count, new KeyValuePair<string, object?>("reason", "db_error_buffered"));
    }

    private async Task ProcessRetryQueueAsync(CancellationToken ct)
    {
        if (_failedBatches.Count == 0) return;

        _logger.LogInformation("Attempting to process {Count} failed batches from buffer...", _failedBatches.Count);
        
        var batchesToProcess = _failedBatches.Count;
        for (int i = 0; i < batchesToProcess; i++)
        {
            if (!_failedBatches.TryDequeue(out var batch)) break;
            
            try
            {
                await ProcessBatchAsync(batch, ct);
                _currentBufferSize -= batch.Count * 1024;
            }
            catch
            {
                // Re-enqueue at back if failed again
                _failedBatches.Enqueue(batch);
                break; // Stop retry for now if DB still down
            }
        }
    }

    private async Task SaveRecoveryStateAsync()
    {
        try
        {
            var items = new List<StatsUpdate>();
            // Note: This is a bit tricky with Channel. We can't peek easily without reading.
            // For production, we'd maintain a side-list or use a different structure if recovery is critical.
            // Since we're refactoring for production, let's implement a simple recovery buffer.
            
            if (_batch.Count == 0) return;

            var json = JsonSerializer.Serialize(_batch, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_recoveryPath + ".tmp", json);
            if (File.Exists(_recoveryPath)) File.Delete(_recoveryPath);
            File.Move(_recoveryPath + ".tmp", _recoveryPath);
            
            _logger.LogDebug("Persistence channel recovery state saved to {Path}", _recoveryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save persistence recovery state");
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Manual flush requested for persistence channel.");
        while (_channel.Reader.TryRead(out var update))
        {
            _batch.Add(update);
            if (_batch.Count >= 100)
            {
                await ProcessBatchAsync(_batch, ct);
                _batch.Clear();
            }
        }

        if (_batch.Count > 0)
        {
            await ProcessBatchAsync(_batch, ct);
            _batch.Clear();
        }
    }

    private async Task FinalFlushAsync()
    {
        _logger.LogInformation("Executing final flush of persistence channel...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FlushAsync(cts.Token);
    }
}
