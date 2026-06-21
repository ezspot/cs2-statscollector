using System.Collections.Concurrent;
using System.Collections.Immutable;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;

namespace statsCollector.Infrastructure;

public interface IEventDispatcher
{
    /// <summary>Must be called once, on the game thread, before any Subscribe call.</summary>
    void Initialize(BasePlugin plugin);
    void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent;
    HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent;
}

public sealed class EventDispatcher(ILogger<EventDispatcher> logger) : IEventDispatcher
{
    private readonly ConcurrentDictionary<Type, object> _handlers = new();
    private readonly ConcurrentDictionary<Type, byte> _hookedTypes = new();
    private readonly ILogger<EventDispatcher> _logger = logger;
    private BasePlugin? _plugin;

    // Cached per-type span name so dispatch (which runs on the game thread for high-frequency
    // events) does not allocate an interpolated string on every call.
    private static class EventName<T>
    {
        public static readonly string DispatchName = $"EventDispatcher.Dispatch.{typeof(T).Name}";
    }

    public void Initialize(BasePlugin plugin) => _plugin = plugin;

    public void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent
    {
        _handlers.AddOrUpdate(typeof(T),
            _ => ImmutableArray.Create(handler),
            (_, existing) => ((ImmutableArray<Func<T, GameEventInfo, HookResult>>)existing).Add(handler));

        // Hook the underlying game event exactly once per type, the first time anything subscribes.
        // Auto-hooking on subscribe makes "subscribed but never hooked" structurally impossible (the
        // class of bug where a processor handler silently never fired). TryAdd is the atomic gate.
        if (_hookedTypes.TryAdd(typeof(T), 0))
        {
            if (_plugin is null)
                throw new InvalidOperationException("EventDispatcher.Initialize(plugin) must be called before Subscribe.");

            _plugin.RegisterEventHandler<T>((e, i) => Dispatch(e, i));
        }

        _logger.LogTrace("Subscribed to event {EventType}", typeof(T).Name);
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
