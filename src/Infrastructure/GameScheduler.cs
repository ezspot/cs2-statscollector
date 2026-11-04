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
    // failure here (such as the late-load bootstrap) would otherwise be invisible.
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
