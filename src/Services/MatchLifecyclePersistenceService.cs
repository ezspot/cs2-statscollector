using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Polly;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface IMatchLifecyclePersistenceService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    Task EnqueueStartMatchAsync(string matchId, string? seriesUuid, CancellationToken cancellationToken = default);
    Task EnqueueEndMatchAsync(string matchId, int winnerTeam, CancellationToken cancellationToken = default);
    Task EnqueueStartRoundAsync(string matchId, int roundNumber, CancellationToken cancellationToken = default);
    Task EnqueueEndRoundAsync(int roundId, int winnerTeam, int winReason, CancellationToken cancellationToken = default);
}

public sealed class MatchLifecyclePersistenceService : IMatchLifecyclePersistenceService, IDisposable
{
    private readonly IMatchTrackingService _matchTracker;
    private readonly ILogger<MatchLifecyclePersistenceService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly Channel<LifecycleEvent> _eventChannel;
    private Task? _processingTask;
    private CancellationTokenSource? _cts;

    private const int ChannelCapacity = 500;

    public MatchLifecyclePersistenceService(
        IMatchTrackingService matchTracker,
        ILogger<MatchLifecyclePersistenceService> logger,
        ResiliencePipeline resiliencePipeline)
    {
        _matchTracker = matchTracker;
        _logger = logger;
        _resiliencePipeline = resiliencePipeline;
        _eventChannel = Channel.CreateBounded<LifecycleEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessEventsAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("MatchLifecyclePersistenceService started");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;

        _eventChannel.Writer.Complete();
        _cts.Cancel();

        if (_processingTask != null)
        {
            try
            {
                await _processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        _logger.LogInformation("MatchLifecyclePersistenceService stopped");
    }

    public async Task EnqueueStartMatchAsync(string matchId, string? seriesUuid, CancellationToken cancellationToken = default)
    {
        var evt = new LifecycleEvent
        {
            Type = LifecycleEventType.StartMatch,
            MatchId = matchId,
            SeriesUuid = seriesUuid,
            Timestamp = DateTime.UtcNow
        };

        if (!await _eventChannel.Writer.WaitToWriteAsync(cancellationToken))
        {
            _logger.LogWarning("Failed to enqueue StartMatch event - channel closed");
            return;
        }

        if (_eventChannel.Writer.TryWrite(evt))
        {
            Instrumentation.MatchLifecycleEventsCounter.Add(1, new("event_type", "start_match"));
        }
        else
        {
            _logger.LogWarning("Failed to enqueue StartMatch event - channel full");
        }
    }

    public async Task EnqueueEndMatchAsync(string matchId, int winnerTeam, CancellationToken cancellationToken = default)
    {
        var evt = new LifecycleEvent
        {
            Type = LifecycleEventType.EndMatch,
            MatchId = matchId,
            WinnerTeam = winnerTeam,
            Timestamp = DateTime.UtcNow
        };

        if (!await _eventChannel.Writer.WaitToWriteAsync(cancellationToken))
        {
            _logger.LogWarning("Failed to enqueue EndMatch event - channel closed");
            return;
        }

        if (_eventChannel.Writer.TryWrite(evt))
        {
            Instrumentation.MatchLifecycleEventsCounter.Add(1, new("event_type", "end_match"));
        }
        else
        {
            _logger.LogWarning("Failed to enqueue EndMatch event - channel full");
        }
    }

    public async Task EnqueueStartRoundAsync(string matchId, int roundNumber, CancellationToken cancellationToken = default)
    {
        var evt = new LifecycleEvent
        {
            Type = LifecycleEventType.StartRound,
            MatchId = matchId,
            RoundNumber = roundNumber,
            Timestamp = DateTime.UtcNow
        };

        if (!await _eventChannel.Writer.WaitToWriteAsync(cancellationToken))
        {
            _logger.LogWarning("Failed to enqueue StartRound event - channel closed");
            return;
        }

        if (_eventChannel.Writer.TryWrite(evt))
        {
            Instrumentation.MatchLifecycleEventsCounter.Add(1, new("event_type", "start_round"));
        }
        else
        {
            _logger.LogWarning("Failed to enqueue StartRound event - channel full");
        }
    }

    public async Task EnqueueEndRoundAsync(int roundId, int winnerTeam, int winReason, CancellationToken cancellationToken = default)
    {
        var evt = new LifecycleEvent
        {
            Type = LifecycleEventType.EndRound,
            RoundId = roundId,
            WinnerTeam = winnerTeam,
            WinReason = winReason,
            Timestamp = DateTime.UtcNow
        };

        if (!await _eventChannel.Writer.WaitToWriteAsync(cancellationToken))
        {
            _logger.LogWarning("Failed to enqueue EndRound event - channel closed");
            return;
        }

        if (_eventChannel.Writer.TryWrite(evt))
        {
            Instrumentation.MatchLifecycleEventsCounter.Add(1, new("event_type", "end_round"));
        }
        else
        {
            _logger.LogWarning("Failed to enqueue EndRound event - channel full");
        }
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    await ProcessEventAsync(evt, ct);
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing lifecycle event {EventType}", evt.Type);
                Instrumentation.MatchLifecycleErrorsCounter.Add(1, new("event_type", evt.Type.ToString()));
            }
        }
    }

    private async Task ProcessEventAsync(LifecycleEvent evt, CancellationToken cancellationToken)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity($"ProcessLifecycleEvent_{evt.Type}");
        
        switch (evt.Type)
        {
            case LifecycleEventType.StartMatch:
                await _matchTracker.StartMatchAsync(evt.MatchId!, evt.SeriesUuid);
                _logger.LogDebug("Processed StartMatch event for {MatchId}", evt.MatchId);
                break;

            case LifecycleEventType.EndMatch:
                await _matchTracker.EndMatchAsync(evt.MatchId!, evt.WinnerTeam);
                _logger.LogDebug("Processed EndMatch event for {MatchId}", evt.MatchId);
                break;

            case LifecycleEventType.StartRound:
                await _matchTracker.StartRoundAsync(evt.MatchId!, evt.RoundNumber);
                _logger.LogDebug("Processed StartRound event for {MatchId} Round {RoundNumber}", evt.MatchId, evt.RoundNumber);
                break;

            case LifecycleEventType.EndRound:
                await _matchTracker.EndRoundAsync(evt.RoundId, evt.WinnerTeam, evt.WinReason);
                _logger.LogDebug("Processed EndRound event for RoundId {RoundId}", evt.RoundId);
                break;

            default:
                _logger.LogWarning("Unknown lifecycle event type: {EventType}", evt.Type);
                break;
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _processingTask?.Dispose();
    }

    private sealed class LifecycleEvent
    {
        public LifecycleEventType Type { get; init; }
        public string? MatchId { get; init; }
        public string? SeriesUuid { get; init; }
        public int RoundNumber { get; init; }
        public int RoundId { get; init; }
        public int WinnerTeam { get; init; }
        public int WinReason { get; init; }
        public DateTime Timestamp { get; init; }
    }

    private enum LifecycleEventType
    {
        StartMatch,
        EndMatch,
        StartRound,
        EndRound
    }
}
