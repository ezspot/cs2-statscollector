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
    private readonly ISchemaInitializer _schemaInitializer;
    private readonly IMatchStatsService _matchStats;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _autoSaveTimer;
    public PluginLifecycleService(
        ILogger<PluginLifecycleService> logger,
        IOptionsMonitor<PluginConfig> config,
        IMatchTrackingService matchTracker,
        IPositionPersistenceService positionPersistence,
        IPlayerSessionService playerSessions,
        IGameEventHandlerService eventHandler,
        IPersistenceChannel persistenceChannel,
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
        _schemaInitializer = schemaInitializer;
        _matchStats = matchStats;
    }

    public void Initialize(BasePlugin plugin)
    {
        _eventHandler.RegisterEvents(plugin);

        var autoSaveInterval = Math.Max(30, _config.CurrentValue.AutoSaveSeconds);
        _autoSaveTimer = plugin.AddTimer(autoSaveInterval, () =>
        {
            SaveAllStats();
        }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

        _logger.LogInformation("Plugin lifecycle initialized.");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _schemaInitializer.EnsureSchemaAsync(ct);

        await _positionPersistence.StartAsync(ct);

        if (_persistenceChannel is IHostedService hosted)
        {
            await hosted.StartAsync(ct);
        }

        _logger.LogInformation("Background persistence services started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _autoSaveTimer?.Kill();

        SaveAllStats();

        if (_matchTracker.CurrentMatch != null)
        {
            _matchStats.FinalizeMatch();
            _matchTracker.EndMatch();
        }

        if (_persistenceChannel is IHostedService hosted)
        {
            await hosted.StopAsync(cancellationToken);
        }
        else
        {
            await _persistenceChannel.FlushAsync(cancellationToken);
        }

        await _positionPersistence.StopAsync(TimeSpan.FromSeconds(10), cancellationToken);

        _logger.LogInformation("Plugin lifecycle stopped.");
    }

    private void SaveAllStats()
    {
        var match = _matchTracker.CurrentMatch;
        var snapshots = _playerSessions.CaptureSnapshots(true, match?.MatchUuid);

        foreach (var snapshot in snapshots)
        {
            if (_persistenceChannel.TryWrite(new PlayerStatsUpdate(snapshot)))
            {
                _playerSessions.MarkPersisted(snapshot.SteamId, snapshot.Version);
            }
        }
    }
}
