using System;
using CounterStrikeSharp.API;

namespace statsCollector.Infrastructure;

public interface IGameScheduler
{
    void Schedule(Action action);
}

public sealed class GameScheduler : IGameScheduler
{
    public void Schedule(Action action) => Server.NextWorldUpdate(action);
}
