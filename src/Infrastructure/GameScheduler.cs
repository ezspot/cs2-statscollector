using System;
using System.Threading;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace statsCollector.Infrastructure;

public interface IGameScheduler
{
    void Schedule(Action action);
}

public sealed class GameScheduler(ILogger<GameScheduler> logger, CancellationTokenSource cancellationTokenSource) : IGameScheduler
{
    private readonly CancellationToken _stoppingToken = cancellationTokenSource.Token;

    // The queue is CounterStrikeSharp's own static one (Server.cs: _onWorldUpdateTaskQueue), which outlives the plugin:
    // an action queued before unload would otherwise run against the disposed services afterwards. CounterStrikeSharp
    // also swallows exceptions thrown inside NextWorldUpdate callbacks without logging them, so a failure here (such as
    // the late-load bootstrap) would otherwise be invisible.
    public void Schedule(Action action) => Server.NextWorldUpdate(() =>
    {
        if (_stoppingToken.IsCancellationRequested) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled game-thread action failed");
        }
    });
}
