using System;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Infrastructure;
using statsCollector.Infrastructure.Database;

namespace statsCollector.Services;

public sealed class PluginLifecycleService : IPluginLifecycleService
{
    private readonly ILogger<PluginLifecycleService> _logger;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IGameEventHandlerService _eventHandler;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IGameScheduler _scheduler;
    private readonly ISchemaInitializer _schemaInitializer;
    private readonly IMatchStatsService _matchStats;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _autoSaveTimer;
    private BasePlugin? _plugin;

    public PluginLifecycleService(
        ILogger<PluginLifecycleService> logger,
        IOptionsMonitor<PluginConfig> config,
        IMatchTrackingService matchTracker,
        IPositionPersistenceService positionPersistence,
        IPlayerSessionService playerSessions,
        IGameEventHandlerService eventHandler,
        IPersistenceChannel persistenceChannel,
        IGameScheduler scheduler,
        ISchemaInitializer schemaInitializer,
        IMatchStatsService matchStats)
    {
        _logger = logger;
        _config = config;
        _matchTracker = matchTracker;
        _positionPersistence = positionPersistence;
        _playerSessions = playerSessions;
        _eventHandler = eventHandler;
        _persistenceChannel = persistenceChannel;
        _scheduler = scheduler;
        _schemaInitializer = schemaInitializer;
        _matchStats = matchStats;
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
        // Apply the schema first (no-op unless AutoCreateSchema is enabled) so writes have tables.
        await _schemaInitializer.EnsureSchemaAsync(ct);

        await _positionPersistence.StartAsync(ct);

        // The plugin builds its own ServiceProvider (no generic Host), so the
        // BackgroundService that drains the stats channel must be started explicitly.
        if (_persistenceChannel is IHostedService hosted)
        {
            await hosted.StartAsync(ct);
        }

        _logger.LogInformation("Background persistence services started.");
    }

    public async Task StopAsync()
    {
        _autoSaveTimer?.Kill();

        await SaveAllStatsAsync();

        // Persist per-match summaries for the in-progress match, then close it. EndMatch enqueues a
        // MatchEnd with the correct match UUID (a direct write here passed an int MatchId, which the
        // channel handler ignored).
        if (_matchTracker.CurrentMatch != null)
        {
            _matchStats.FinalizeMatch();
            _matchTracker.EndMatch();
        }

        await _persistenceChannel.FlushAsync(CancellationToken.None);

        // Stops the channel worker and runs its final drain.
        if (_persistenceChannel is IHostedService hosted)
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        await _positionPersistence.StopAsync(TimeSpan.FromSeconds(3), CancellationToken.None);

        _logger.LogInformation("Plugin lifecycle stopped.");
    }

    private Task SaveAllStatsAsync()
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
        return Task.CompletedTask;
    }
}
