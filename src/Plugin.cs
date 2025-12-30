using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.Registry;
using Serilog;
using Serilog.Events;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;
using statsCollector.Infrastructure.Database;
using statsCollector.Services;

namespace statsCollector;

public sealed class Plugin(ILogger<Plugin> logger) : BasePlugin, IPluginConfig<PluginConfig>
{
    private IServiceProvider? _serviceProvider;
    private readonly ILogger<Plugin> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private PluginConfig _config = new();

    private IPlayerSessionService? _playerSessions;
    private IStatsRepository? _statsRepository;
    private ICombatEventProcessor? _combatProcessor;
    private IUtilityEventProcessor? _utilityProcessor;
    private IBombEventProcessor? _bombProcessor;
    private IEconomyEventProcessor? _economyProcessor;
    private IPositionPersistenceService? _positionPersistence;
    private IStatsPersistenceService? _statsPersistence;
    private IMatchTrackingService? _matchTracker;
    private IScrimManager? _scrimManager;
    private IPauseService? _pauseService;

    private CounterStrikeSharp.API.Modules.Timers.Timer? _autoSaveTimer;

    private MeterProvider? _meterProvider;
    private TracerProvider? _tracerProvider;

    private IPositionTrackingService? _positionTracking;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public override string ModuleName => "statsCollector";
    public override string ModuleVersion => Instrumentation.ServiceVersion;
    public override string ModuleAuthor => "Anders Giske Hagen";
    public override string ModuleDescription => "High-performance CS2 stats collection with late 2025 observability";

    public PluginConfig Config { get; set; } = new();

    public void OnConfigParsed(PluginConfig config)
    {
        try
        {
            config = config.WithEnvironmentOverrides();
            config.Validate();
            _config = config;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(GetSerilogLevel(config.LogLevel))
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Version", Instrumentation.ServiceVersion)
                .WriteTo.Console()
                .WriteTo.File("logs/statscollector-.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            _logger.LogInformation("Configuration validated and Serilog initialized. DB Host: {Host}, DB Port: {Port}", config.DatabaseHost, config.DatabasePort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration validation failed");
            throw;
        }
    }

    private static LogEventLevel GetSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };

    public override void Load(bool hotReload)
    {
        _logger.LogInformation("statsCollector v{Version} loading... HotReload: {HotReload}", Instrumentation.ServiceVersion, hotReload);

        // Run initialization in a separate task to avoid blocking the game thread
        Task.Run(async () =>
        {
            try
            {
                await InitializePluginAsync(hotReload, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize statsCollector plugin");
            }
        });
    }

    private async Task InitializePluginAsync(bool hotReload, CancellationToken ct)
    {
        var services = new ServiceCollection();

        // OTEL Setup
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(Instrumentation.ServiceName, serviceVersion: Instrumentation.ServiceVersion);
        
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .AddSource(Instrumentation.ServiceName)
            .AddSource("MySqlConnector")
            .AddOtlpExporter()
            .Build();

        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .AddMeter(Instrumentation.ServiceName)
            .AddRuntimeInstrumentation()
            .AddOtlpExporter()
            .Build();

        // DI Registration
        services.AddSingleton(Options.Create(_config));
        services.AddSingleton<IOptionsMonitor<PluginConfig>>(new StaticOptionsMonitor<PluginConfig>(_config));
        services.AddLogging(builder => { builder.ClearProviders(); builder.AddSerilog(Log.Logger); });
        services.AddSingleton(TimeProvider.System);
        
        // Resilience
        services.AddResiliencePipeline("database", (builder, context) => 
        {
            var logger = context.ServiceProvider.GetRequiredService<ILogger<Plugin>>();
            builder.AddPipeline(ResiliencePolicies.CreateDatabasePipeline(logger)); 
        });

        services.AddSingleton<IConnectionFactory, ConnectionFactory>();
        services.AddSingleton<IMatchTrackingService, MatchTrackingService>();
        services.AddSingleton<IPlayerSessionService, PlayerSessionService>();
        services.AddTransient<IStatsRepository, StatsRepository>();
        services.AddTransient<ICombatEventProcessor, CombatEventProcessor>();
        services.AddTransient<IUtilityEventProcessor, UtilityEventProcessor>();
        services.AddTransient<IBombEventProcessor, BombEventProcessor>();
        services.AddTransient<IEconomyEventProcessor, EconomyEventProcessor>();
        services.AddTransient<IPositionTrackingService, PositionTrackingService>();
        services.AddSingleton<IPositionPersistenceService, PositionPersistenceService>();
        services.AddSingleton<IStatsPersistenceService, StatsPersistenceService>();
        services.AddSingleton<IPauseService, PauseService>();
        services.AddSingleton<IScrimPersistenceService, ScrimPersistenceService>();
        services.AddSingleton<IConfigLoaderService, ConfigLoaderService>();
        services.AddSingleton<IMapDataService, MapDataService>();
        services.AddSingleton<IFlashEfficiencyService, FlashEfficiencyService>();
        services.AddSingleton<IRoundBackupService, RoundBackupService>();
        services.AddSingleton<IScrimManager, ScrimManager>();
        services.AddSingleton<IGameEventHandlerService, GameEventHandlerService>();

        _serviceProvider = services.BuildServiceProvider();

        // Resolve
        _playerSessions = _serviceProvider.GetRequiredService<IPlayerSessionService>();
        _statsRepository = _serviceProvider.GetRequiredService<IStatsRepository>();
        _combatProcessor = _serviceProvider.GetRequiredService<ICombatEventProcessor>();
        _utilityProcessor = _serviceProvider.GetRequiredService<IUtilityEventProcessor>();
        _bombProcessor = _serviceProvider.GetRequiredService<IBombEventProcessor>();
        _economyProcessor = _serviceProvider.GetRequiredService<IEconomyEventProcessor>();
        _matchTracker = _serviceProvider.GetRequiredService<IMatchTrackingService>();
        _statsPersistence = _serviceProvider.GetRequiredService<IStatsPersistenceService>();
        _positionPersistence = _serviceProvider.GetRequiredService<IPositionPersistenceService>();
        _scrimManager = _serviceProvider.GetRequiredService<IScrimManager>();
        _pauseService = _serviceProvider.GetRequiredService<IPauseService>();
        _positionTracking = _serviceProvider.GetRequiredService<IPositionTrackingService>();
        var eventHandler = _serviceProvider.GetRequiredService<IGameEventHandlerService>();
        
        await _statsPersistence.StartAsync(ct);
        await _positionPersistence.StartAsync(ct);

        // Initialize match tracking if server is already running
        if (hotReload && Server.MapName != null)
        {
            await _matchTracker.StartMatchAsync(Server.MapName);
        }

        // Register listeners on the game thread
        Server.NextFrame(() =>
        {
            eventHandler.RegisterEvents(this);

            RegisterListener<Listeners.OnMapStart>(mapName =>
            {
                _ = _matchTracker.StartMatchAsync(mapName);
            });

            RegisterListener<Listeners.OnClientAuthorized>((slot, steamId) =>
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player != null && !player.IsBot)
                {
                    _logger.LogInformation("Client authorized listener: {Name} (SteamID: {SteamID})", player.PlayerName, steamId);
                    _playerSessions?.EnsurePlayer(steamId.SteamId64, player.PlayerName);
                }
            });

            RegisterListener<Listeners.OnTick>(() =>
            {
                _positionTracking?.OnTick();
            });

            // Auto-save timer
            var autoSaveInterval = Math.Max(30, _config.AutoSaveSeconds);
            _autoSaveTimer = AddTimer(autoSaveInterval, () => 
            {
                _ = SaveAllStatsAsync();
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

            _logger.LogInformation("statsCollector plugin initialized successfully on game thread.");
        });

        _logger.LogInformation("statsCollector background initialization complete.");
    }

    public override void Unload(bool hotReload)
    {
        _logger.LogInformation("statsCollector plugin unloading... HotReload: {HotReload}", hotReload);

        try
        {
            _autoSaveTimer?.Kill();
            
            // For critical data persistence, we perform a synchronous flush if not hot-reloading
            // to ensure data isn't lost during server shutdown.
            if (!hotReload)
            {
                _logger.LogInformation("Performing synchronous shutdown flush...");
                CleanupAsync().GetAwaiter().GetResult();
            }
            else
            {
                _ = CleanupAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during plugin unload");
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token);

            await SaveAllStatsAsync();

            if (_matchTracker?.CurrentMatch != null)
            {
                await _matchTracker.EndMatchAsync(_matchTracker.CurrentMatch.MatchId, linkedCts.Token);
            }

            if (_statsPersistence != null) await _statsPersistence.StopAsync(TimeSpan.FromSeconds(5), linkedCts.Token);
            if (_positionPersistence != null) await _positionPersistence.StopAsync(TimeSpan.FromSeconds(3), linkedCts.Token);

            _cancellationTokenSource.Cancel();
            _tracerProvider?.Dispose();
            _meterProvider?.Dispose();
            
            _logger.LogInformation("statsCollector cleanup complete.");
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during plugin cleanup task");
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private async Task SaveAllStatsAsync()
    {
        if (_playerSessions == null || _statsPersistence == null) return;
        var matchId = _matchTracker?.CurrentMatch?.MatchId;
        var snapshots = _playerSessions.CaptureSnapshots(matchId);
        if (snapshots.Length > 0)
        {
            _logger.LogInformation("Saving {Count} player snapshots (MatchID: {MatchId})...", snapshots.Length, matchId);
            await _statsPersistence.EnqueueAsync(snapshots, CancellationToken.None);
        }
    }

    #region Player Lifecycle Events
    private async Task SavePlayerStatsOnDisconnectAsync(CCSPlayerController player)
    {
        if (_playerSessions == null || _statsRepository == null) return;
        var matchId = _matchTracker?.CurrentMatch?.MatchId;
        if (_playerSessions.TryGetSnapshot(player.SteamID, out var snapshot, matchId))
        {
            await _statsRepository.UpsertPlayerAsync(snapshot, _cancellationTokenSource.Token);
            _playerSessions.TryRemovePlayer(player.SteamID, out _);
        }
    }
    #endregion

    #region Round Events
    private async Task SaveStatsAtRoundEndAsync()
    {
        if (_playerSessions == null || _statsPersistence == null) return;
        var matchId = _matchTracker?.CurrentMatch?.MatchId;
        var snapshots = _playerSessions.CaptureSnapshots(matchId);
        if (snapshots.Length > 0) await _statsPersistence.EnqueueAsync(snapshots, _cancellationTokenSource.Token);
    }
    #endregion
}
