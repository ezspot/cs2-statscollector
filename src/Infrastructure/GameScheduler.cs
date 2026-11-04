using System;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace statsCollector.Infrastructure;

public interface IGameScheduler
{
    void Schedule(Action action);
}

public sealed class GameScheduler(ILogger<GameScheduler> logger) : IGameScheduler
{
    // CounterStrikeSharp swallows exceptions thrown inside NextWorldUpdate callbacks without logging them, so a
    // failure here (including the deferred registration of every event hook) would otherwise be invisible.
    public void Schedule(Action action) => Server.NextWorldUpdate(() =>
    {
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
