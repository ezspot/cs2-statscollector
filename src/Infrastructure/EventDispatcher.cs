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

public sealed class EventDispatcher : IEventDispatcher
{
    private readonly ConcurrentDictionary<Type, ImmutableArray<Delegate>> _handlers = new();
    private readonly ILogger<EventDispatcher> _logger;

    public EventDispatcher(ILogger<EventDispatcher> logger)
    {
        _logger = logger;
    }

    public void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent
    {
        _handlers.AddOrUpdate(typeof(T), 
            _ => ImmutableArray.Create<Delegate>(handler),
            (_, existing) => existing.Add(handler));
        _logger.LogTrace("Subscribed to event {EventType}", typeof(T).Name);
    }

    public HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var handlers))
        {
            return HookResult.Continue;
        }

        var result = HookResult.Continue;
        // handlers is an ImmutableArray, so iteration is thread-safe without locks
        foreach (var handler in handlers)
        {
            try
            {
                var handlerResult = ((Func<T, GameEventInfo, HookResult>)handler)(@event, info);
                if (handlerResult == HookResult.Stop || handlerResult == HookResult.Handled)
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
