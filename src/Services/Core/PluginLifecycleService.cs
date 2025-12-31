using System;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;

namespace statsCollector.Services;

public sealed class PluginLifecycleService : IPluginLifecycleService
{
    private readonly ILogger<PluginLifecycleService> _logger;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IPositionTrackingService _positionTracking;
    private readonly IGameEventHandlerService _eventHandler;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IGameScheduler _scheduler;
    
    private CounterStrikeSharp.API.Modules.Timers.Timer? _autoSaveTimer;
    private BasePlugin? _plugin;

    public PluginLifecycleService(
        ILogger<PluginLifecycleService> logger,
        IOptionsMonitor<PluginConfig> config,
        IMatchTrackingService matchTracker,
        IPositionPersistenceService positionPersistence,
        IPlayerSessionService playerSessions,
        IPositionTrackingService positionTracking,
        IGameEventHandlerService eventHandler,
        IPersistenceChannel persistenceChannel,
        IGameScheduler scheduler)
    {
        _logger = logger;
        _config = config;
        _matchTracker = matchTracker;
        _positionPersistence = positionPersistence;
        _playerSessions = playerSessions;
        _positionTracking = positionTracking;
        _eventHandler = eventHandler;
        _persistenceChannel = persistenceChannel;
        _scheduler = scheduler;
    }

    public void Initialize(BasePlugin plugin)
    {
        _plugin = plugin;
        _eventHandler.RegisterEvents(plugin);
        
        // Auto-save timer
        var autoSaveInterval = Math.Max(30, _config.CurrentValue.AutoSaveSeconds);
        _autoSaveTimer = plugin.AddTimer(autoSaveInterval, () => 
        {
            _ = SaveAllStatsAsync();
        }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

        _logger.LogInformation("Plugin lifecycle initialized.");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _positionPersistence.StartAsync(ct);
        _logger.LogInformation("Background persistence services started.");
    }

    public async Task StopAsync()
    {
        _autoSaveTimer?.Kill();
        
        await SaveAllStatsAsync();

        if (_matchTracker.CurrentMatch != null)
        {
            _persistenceChannel.TryWrite(new StatsUpdate(UpdateType.MatchEnd, _matchTracker.CurrentMatch.MatchId));
        }

        await _persistenceChannel.FlushAsync(CancellationToken.None);
        await _positionPersistence.StopAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        
        _logger.LogInformation("Plugin lifecycle stopped.");
    }

    public void OnTick()
    {
        _positionTracking.OnTick();
    }

    private async Task SaveAllStatsAsync()
    {
        var match = _matchTracker.CurrentMatch;
        var snapshots = _playerSessions.CaptureSnapshots(true, match?.MatchId, match?.MatchUuid);
        
        foreach (var snapshot in snapshots)
        {
            _persistenceChannel.TryWrite(new StatsUpdate(
                UpdateType.PlayerStats, 
                snapshot, 
                match?.MatchUuid ?? "none", 
                snapshot.RoundNumber, 
                snapshot.SteamId));
        }
    }
}
