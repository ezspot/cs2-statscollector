using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Services;

namespace statsCollector.Infrastructure;

public interface IEventDispatcher
{
    void Initialize(BasePlugin plugin);
    void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent;
    HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent;
}

public sealed class EventDispatcher(
    ILogger<EventDispatcher> logger,
    IOptionsMonitor<PluginConfig> config,
    IScrimManager scrimManager) : IEventDispatcher
{
    private readonly Dictionary<Type, object> _handlers = [];
    private readonly HashSet<Type> _hookedTypes = [];
    private readonly ILogger<EventDispatcher> _logger = logger;
    private readonly IOptionsMonitor<PluginConfig> _config = config;
    private readonly IScrimManager _scrimManager = scrimManager;
    private BasePlugin? _plugin;

    private static class EventName<T>
    {
        public static readonly string DispatchName = $"EventDispatcher.Dispatch.{typeof(T).Name}";
    }

    public void Initialize(BasePlugin plugin) => _plugin = plugin;

    public void Subscribe<T>(Func<T, GameEventInfo, HookResult> handler) where T : GameEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var existing))
        {
            existing = new List<Func<T, GameEventInfo, HookResult>>();
            _handlers.Add(typeof(T), existing);
        }
        ((List<Func<T, GameEventInfo, HookResult>>)existing).Add(handler);

        if (_hookedTypes.Add(typeof(T)))
        {
            if (_plugin is null)
                throw new InvalidOperationException("EventDispatcher.Initialize(plugin) must be called before Subscribe.");

            _plugin.RegisterEventHandler<T>((e, i) => Dispatch(e, i));
        }

        _logger.LogTrace("Subscribed to event {EventType}", typeof(T).Name);
    }

    public HookResult Dispatch<T>(T @event, GameEventInfo info) where T : GameEvent
    {
        var config = _config.CurrentValue;
        if (config.EnableScrim && _scrimManager.CurrentState is not ScrimState.Live)
        {
            return HookResult.Continue;
        }

        // Warmup is not part of a match. Deathmatch has no rounds: its kills, with no rounds to divide by, would
        // distort the lifetime ratings that competitive servers share.
        if (config.DeathmatchMode || GameRulesState.IsWarmup) return HookResult.Continue;

        using var activity = Instrumentation.ActivitySource.StartActivity(EventName<T>.DispatchName);

        if (!_handlers.TryGetValue(typeof(T), out var handlersObj))
        {
            return HookResult.Continue;
        }

        var handlers = (List<Func<T, GameEventInfo, HookResult>>)handlersObj;
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
