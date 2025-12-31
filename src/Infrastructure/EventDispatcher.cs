using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;

namespace statsCollector.Infrastructure;

public interface IEventHandler<in T> where T : GameEvent
{
    HookResult Handle(T @event, GameEventInfo info);
}

public interface IEventDispatcher
{
    void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent;
    void Subscribe<T>(IEventHandler<T> handler) where T : GameEvent;
    HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent;
}

public sealed class EventDispatcher(ILogger<EventDispatcher> logger) : IEventDispatcher
{
    private readonly ConcurrentDictionary<Type, object> _handlers = new();
    private readonly ILogger<EventDispatcher> _logger = logger;

    public void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent
    {
        _handlers.AddOrUpdate(typeof(T),
            _ => ImmutableArray.Create<Func<T, GameEventInfo, HookResult>>(handler),
            (_, existing) => ((ImmutableArray<Func<T, GameEventInfo, HookResult>>)existing).Add(handler));
        
        _logger.LogTrace("Subscribed to event {EventType} (Delegate)", typeof(T).Name);
    }

    public void Subscribe<T>(IEventHandler<T> handler) where T : GameEvent
    {
        Subscribe<T>(handler.Handle);
    }

    public HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent
    {
        using var activity = Instrumentation.ActivitySource.StartActivity($"EventDispatcher.Dispatch.{typeof(T).Name}");
        
        if (!_handlers.TryGetValue(typeof(T), out var handlersObj))
        {
            return HookResult.Continue;
        }

        // ImmutableArray provides lock-free iteration on the snapshot
        var handlers = (ImmutableArray<Func<T, GameEventInfo, HookResult>>)handlersObj;
        var result = HookResult.Continue;

        foreach (var handler in handlers)
        {
            try
            {
                var handlerResult = handler(@event, info);
                if (handlerResult is HookResult.Stop or HookResult.Handled)
                {
                    result = handlerResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching event {EventType}", typeof(T).Name);
            }
        }
        return result;
    }
}
