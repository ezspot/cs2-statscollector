using System;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using statsCollector.Config;
using statsCollector.Infrastructure;
using statsCollector.Infrastructure.Database;

namespace statsCollector.Services;

public sealed class PluginLifecycleService : IPluginLifecycleService
{
    private static readonly TimeSpan SchemaRetryDelay = TimeSpan.FromSeconds(15);

    private readonly ILogger<PluginLifecycleService> _logger;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IPlayerSessionService _playerSessions;
    private readonly IGameEventHandlerService _eventHandler;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly ISchemaInitializer _schemaInitializer;
    private readonly IMatchStatsService _matchStats;
    private readonly IStatsRepository _statsRepository;

    public PluginLifecycleService(
        ILogger<PluginLifecycleService> logger,
        IOptionsMonitor<PluginConfig> config,
        IMatchTrackingService matchTracker,
        IPositionPersistenceService positionPersistence,
        IPlayerSessionService playerSessions,
        IGameEventHandlerService eventHandler,
        IPersistenceChannel persistenceChannel,
        ISchemaInitializer schemaInitializer,
        IMatchStatsService matchStats,
        IStatsRepository statsRepository)
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
        _statsRepository = statsRepository;
    }

    public void Initialize(BasePlugin plugin)
    {
        _eventHandler.RegisterEvents(plugin);

        var autoSaveInterval = Math.Max(30, _config.CurrentValue.AutoSaveSeconds);
        // BasePlugin.Dispose kills it at unload.
        plugin.AddTimer(autoSaveInterval, SaveAllStats, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

        _logger.LogInformation("Plugin lifecycle initialized.");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);

        try
        {
            await _statsRepository.CheckLocalInfileAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not check the database server's local_infile setting.");
        }

        await _positionPersistence.StartAsync(ct);

        if (_persistenceChannel is IHostedService hosted)
        {
            // Not the plugin token: Unload cancels that before StopAsync, which would stop the worker ahead of
            // the final save. StopAsync flushes the worker and then stops it.
            await hosted.StartAsync(CancellationToken.None);
        }

        _logger.LogInformation("Background persistence services started.");
    }

    // A failed schema step used to abort startup, leaving the plugin loaded but recording nothing until the
    // next restart. Wait out an unreachable database (e.g. MySQL still starting after a host reboot) and another
    // server's schema lock (a TimeoutException from SchemaInitializer: it may be mid-ALTER); for anything else
    // (such as a missing privilege) carry on against the existing schema.
    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await _schemaInitializer.EnsureSchemaAsync(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is TimeoutException || (ex is MySqlException mysql && ResiliencePolicies.IsTransient(mysql))))
            {
                _logger.LogWarning(ex, "Database unreachable or locked for schema initialization; retrying in {Delay}s.", SchemaRetryDelay.TotalSeconds);
                await Task.Delay(SchemaRetryDelay, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Schema initialization failed; continuing with the existing schema.");
                return;
            }
        }
    }

    // Runs on a pool thread when unload had to wait for startup: no native calls here. The autosave timer cannot fire
    // while unload blocks the game thread, and BasePlugin.Dispose kills it afterwards.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        SaveAllStats();

        if (_matchTracker.CurrentMatch != null)
        {
            _matchStats.FinalizeMatch();
            _matchTracker.EndMatch();
        }

        try
        {
            if (_persistenceChannel is IHostedService hosted)
            {
                await hosted.StopAsync(cancellationToken);
            }
            else
            {
                await _persistenceChannel.FlushAsync(cancellationToken: cancellationToken);
            }
        }
        finally
        {
            // Position writes are independent of the stats channel; flush them even if its final flush failed, within
            // what is left of the one unload budget (Plugin.Unload blocks the game thread on it).
            await _positionPersistence.StopAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

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
