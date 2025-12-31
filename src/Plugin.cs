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

    private IPluginLifecycleService? _lifecycle;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private MeterProvider? _meterProvider;
    private TracerProvider? _tracerProvider;

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
        services.AddSingleton<IEventDispatcher, EventDispatcher>();
        services.AddSingleton<IAnalyticsService, AnalyticsService>();
        services.AddSingleton<IMatchTrackingService, MatchTrackingService>();
        services.AddSingleton<IPlayerSessionService, PlayerSessionService>();
        services.AddTransient<IStatsRepository, StatsRepository>();
        
        // Register Event Processors for automated discovery
        services.AddTransient<IEventProcessor, CombatEventProcessor>();
        services.AddTransient<IEventProcessor, UtilityEventProcessor>();
        services.AddTransient<IEventProcessor, BombEventProcessor>();
        services.AddTransient<IEventProcessor, EconomyEventProcessor>();
        services.AddTransient<IEventProcessor, MovementEventProcessor>();
        services.AddTransient<IEventProcessor, CommunicationEventProcessor>();

        // Specific interface registrations for direct usage if needed
        services.AddTransient<ICombatEventProcessor>(sp => (ICombatEventProcessor)sp.GetRequiredService<IEnumerable<IEventProcessor>>().First(p => p is CombatEventProcessor));
        services.AddTransient<IUtilityEventProcessor>(sp => (IUtilityEventProcessor)sp.GetRequiredService<IEnumerable<IEventProcessor>>().First(p => p is UtilityEventProcessor));
        services.AddTransient<IBombEventProcessor>(sp => (IBombEventProcessor)sp.GetRequiredService<IEnumerable<IEventProcessor>>().First(p => p is BombEventProcessor));
        services.AddTransient<IEconomyEventProcessor>(sp => (IEconomyEventProcessor)sp.GetRequiredService<IEnumerable<IEventProcessor>>().First(p => p is EconomyEventProcessor));

        services.AddTransient<IPositionTrackingService, PositionTrackingService>();
        services.AddSingleton<IPositionPersistenceService, PositionPersistenceService>();
        services.AddSingleton<IStatsPersistenceService, StatsPersistenceService>();
        services.AddSingleton<IMatchLifecyclePersistenceService, MatchLifecyclePersistenceService>();
        services.AddSingleton<IPauseService, PauseService>();
        services.AddSingleton<IScrimPersistenceService, ScrimPersistenceService>();
        services.AddSingleton<IConfigLoaderService, ConfigLoaderService>();
        services.AddSingleton<IMapDataService, MapDataService>();
        services.AddSingleton<IFlashEfficiencyService, FlashEfficiencyService>();
        services.AddSingleton<IRoundBackupService, RoundBackupService>();
        services.AddSingleton<IScrimManager, ScrimManager>();
        services.AddSingleton<IDamageReportService, DamageReportService>();
        services.AddSingleton<IMatchReadyService, MatchReadyService>();
        services.AddSingleton<IPluginLifecycleService, PluginLifecycleService>();
        services.AddSingleton<IGameEventHandlerService, GameEventHandlerService>();

        _serviceProvider = services.BuildServiceProvider();

        // Resolve
        _lifecycle = _serviceProvider.GetRequiredService<IPluginLifecycleService>();
        var matchTracker = _serviceProvider.GetRequiredService<IMatchTrackingService>();
        
        await _lifecycle.StartAsync(ct);

        // Initialize match tracking if server is already running
        if (hotReload && Server.MapName != null)
        {
            await matchTracker.StartMatchAsync(Server.MapName);
        }

        // Register listeners on the game thread
        Server.NextFrame(() =>
        {
            _lifecycle.Initialize(this);

            RegisterListener<Listeners.OnMapStart>(mapName =>
            {
                _ = matchTracker.StartMatchAsync(mapName);
            });

            RegisterListener<Listeners.OnTick>(() =>
            {
                _lifecycle.OnTick();
            });

            _logger.LogInformation("statsCollector plugin initialized successfully on game thread.");
        });

        _logger.LogInformation("statsCollector background initialization complete.");
    }

    public override void Unload(bool hotReload)
    {
        _logger.LogInformation("statsCollector plugin unloading... HotReload: {HotReload}", hotReload);

        try
        {
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
            if (_lifecycle != null) await _lifecycle.StopAsync();

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

    #region Player Lifecycle Events
    #endregion

    #region Round Events
    #endregion
}
