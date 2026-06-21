using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;

namespace statsCollector.Infrastructure;

public interface IEventDispatcher
{
    void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent;
    HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent;
}

public sealed class EventDispatcher(ILogger<EventDispatcher> logger) : IEventDispatcher
{
    private readonly ConcurrentDictionary<Type, object> _handlers = new();
    private readonly ILogger<EventDispatcher> _logger = logger;

    // Cached per-type span name so dispatch (which runs on the game thread for high-frequency
    // events) does not allocate an interpolated string on every call.
    private static class EventName<T>
    {
        public static readonly string DispatchName = $"EventDispatcher.Dispatch.{typeof(T).Name}";
    }

    public void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent
    {
        _handlers.AddOrUpdate(typeof(T),
            _ => ImmutableArray.Create<Func<T, GameEventInfo, HookResult>>(handler),
            (_, existing) => ((ImmutableArray<Func<T, GameEventInfo, HookResult>>)existing).Add(handler));

        _logger.LogTrace("Subscribed to event {EventType} (Delegate)", typeof(T).Name);
    }

    public HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent
    {
        using var activity = Instrumentation.ActivitySource.StartActivity(EventName<T>.DispatchName);

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
