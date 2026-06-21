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
using Polly;
using Polly.Registry;
using Serilog;
using Serilog.Events;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;
using statsCollector.Infrastructure.Database;
using statsCollector.Services;
using statsCollector.Services.Handlers;

using statsCollector.Infrastructure.Observability;

namespace statsCollector;

public sealed class Plugin(ILogger<Plugin> logger) : BasePlugin, IPluginConfig<PluginConfig>
{
    private IServiceProvider? _serviceProvider;
    private readonly ILogger<Plugin> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private PluginConfig _config = new();

    private IPluginLifecycleService? _lifecycle;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public override string ModuleName => "statsCollector";
    public override string ModuleVersion => Instrumentation.ServiceVersion;
    public override string ModuleAuthor => "Anders Giske Hagen";
    public override string ModuleDescription => "CS2 player statistics collection to MySQL";

    public PluginConfig Config { get; set; } = new();

    public void OnConfigParsed(PluginConfig config)
    {
        try
        {
            config = config.WithEnvironmentOverrides();
            config.Validate();
            _config = config;

            Bootstrapper.InitializeSerilog(config);

            _logger.LogInformation("Configuration validated and Serilog initialized. DB Host: {Host}, DB Port: {Port}", config.DatabaseHost, config.DatabasePort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration validation failed");
            throw;
        }
    }

    public override void Load(bool hotReload)
    {
        _logger.LogInformation("statsCollector v{Version} loading... HotReload: {HotReload}", Instrumentation.ServiceVersion, hotReload);

        // Run initialization in a separate task to avoid blocking the game thread
        // BUT ensure any game thread interactions are correctly scheduled
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
        services.AddSingleton<ISchemaInitializer, SchemaInitializer>();
        services.AddSingleton<IEventDispatcher, EventDispatcher>();
        services.AddSingleton<IAnalyticsService, AnalyticsService>();
        services.AddSingleton<IMatchTrackingService, MatchTrackingService>();
        services.AddSingleton<IPlayerSessionService, PlayerSessionService>();
        services.AddTransient<IStatsRepository, StatsRepository>();
        
        // Event processors hold per-round state (alive counts, bomb timers, pending grenades) and
        // subscribe to the dispatcher exactly once, so each must be a single shared instance. Register
        // the concrete type as a singleton, then expose that same instance under every interface it is
        // consumed through (IEventProcessor for the pipeline, ICombatEventProcessor etc. for cross-refs).
        services.AddSingleton<CombatEventProcessor>();
        services.AddSingleton<UtilityEventProcessor>();
        services.AddSingleton<BombEventProcessor>();
        services.AddSingleton<EconomyEventProcessor>();
        services.AddSingleton<MovementEventProcessor>();
        services.AddSingleton<CommunicationEventProcessor>();

        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<CombatEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<UtilityEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<BombEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<EconomyEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<MovementEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<CommunicationEventProcessor>());

        services.AddSingleton<ICombatEventProcessor>(sp => sp.GetRequiredService<CombatEventProcessor>());
        services.AddSingleton<IUtilityEventProcessor>(sp => sp.GetRequiredService<UtilityEventProcessor>());
        services.AddSingleton<IBombEventProcessor>(sp => sp.GetRequiredService<BombEventProcessor>());
        services.AddSingleton<IEconomyEventProcessor>(sp => sp.GetRequiredService<EconomyEventProcessor>());

        services.AddSingleton<IPositionTrackingService, PositionTrackingService>();
        services.AddSingleton<IPositionPersistenceService, PositionPersistenceService>();
        services.AddSingleton<IGameScheduler, GameScheduler>();
        // Lifecycle (Start/Stop) is driven explicitly by PluginLifecycleService, not by a generic Host.
        services.AddSingleton<IPersistenceChannel, PersistenceChannel>();
        services.AddSingleton<IJsonRecoveryService, JsonRecoveryService>();
        services.AddSingleton<IConfigLoaderService, ConfigLoaderService>();
        services.AddSingleton<IRoundBackupService, RoundBackupService>();
        services.AddSingleton<IScrimManager, ScrimManager>();
        services.AddSingleton<IMatchStatsService, MatchStatsService>();
        services.AddSingleton<IDamageReportService, DamageReportService>();
        services.AddSingleton<IPauseService, PauseService>();
        services.AddSingleton<IMatchReadyService, MatchReadyService>();
        services.AddSingleton<IPluginLifecycleService, PluginLifecycleService>();
        services.AddSingleton<IGameEventHandlerService, GameEventHandlerService>();

        // Game Handlers (direct game-event hooks; combat/bomb/utility/economy events flow through the
        // EventDispatcher pipeline instead, wired in GameEventHandlerService).
        services.AddSingleton<IGameHandler, MatchFlowHandler>();
        services.AddSingleton<IGameHandler, PlayerLifecycleHandler>();

        _serviceProvider = services.BuildServiceProvider();

        // Resolve
        _lifecycle = _serviceProvider.GetRequiredService<IPluginLifecycleService>();
        var matchTracker = _serviceProvider.GetRequiredService<IMatchTrackingService>();
        var scheduler = _serviceProvider.GetRequiredService<IGameScheduler>();
        
        await _lifecycle.StartAsync(ct);

        // Initialize match tracking if server is already running
        if (hotReload)
        {
            scheduler.Schedule(() => 
            {
                if (Server.MapName != null)
                {
                    matchTracker.StartMatch(Server.MapName);
                }
            });
        }

        // Register listeners on the game thread
        scheduler.Schedule(() =>
        {
            _lifecycle.Initialize(this);

            RegisterListener<Listeners.OnMapStart>(mapName =>
            {
                matchTracker.StartMatch(mapName);
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
            // Use synchronous wait for cleanup to ensure it finishes before host process terminates
            CleanupAsync().GetAwaiter().GetResult();
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

    [ConsoleCommand("css_recover_scrim", "Recover the last active scrim state after a server crash")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnRecoverScrimCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player != null && !AdminUtils.HasPermission(player, "@css/admin"))
        {
            player.PrintToChat(" [Scrim] You do not have permission to run this command.");
            return;
        }

        if (_serviceProvider == null) return;
        var scrimManager = _serviceProvider.GetRequiredService<IScrimManager>();
        
        Task.Run(async () => 
        {
            try
            {
                await scrimManager.RecoverAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scrim recovery command");
            }
        });
    }
}
