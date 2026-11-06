using System;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Serilog;
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
    private Serilog.Core.Logger? _serilog;

    private IPluginLifecycleService? _lifecycle;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _initializationTask;

    public const string Version = "1.0.0";

    public override string ModuleName => "statsCollector";
    public override string ModuleVersion => Version;
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

            _serilog?.Dispose();
            _serilog = Bootstrapper.CreateLogger(config, Path.Join(ModuleDirectory, "logs"));

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
        _logger.LogInformation("statsCollector v{Version} loading... HotReload: {HotReload}", Version, hotReload);

        IPluginLifecycleService lifecycle;
        try
        {
            // Services are built and their handlers registered here, on the game thread: CounterStrikeSharp natives
            // (Server.GameDirectory in two constructors, event and command registration) are not guarded against other
            // threads; called from the background task they would run unsynchronized against the engine.
            _serviceProvider = BuildServices();
            lifecycle = _serviceProvider.GetRequiredService<IPluginLifecycleService>();
            lifecycle.Initialize(this);
            _lifecycle = lifecycle;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize statsCollector plugin");
            // Swallowed, CounterStrikeSharp would report the plugin as loaded with nothing registered; thrown, it logs
            // the failure and unloads it (PluginContext.cs:233-254), which copes with the half-built state here.
            throw;
        }

        PickUpRunningMatch();

        // Schema and background writers need the database. Until they start, stats updates and position events wait in
        // their bounded queues; an unload before then discards them (there is nothing to write them to).
        _initializationTask = Task.Run(async () =>
        {
            try
            {
                await lifecycle.StartAsync(_cancellationTokenSource.Token);
                _logger.LogInformation("statsCollector background initialization complete.");
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                _logger.LogInformation("statsCollector initialization canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to start statsCollector persistence");
            }
        }, _cancellationTokenSource.Token);
    }

    // A load in the middle of a map (hot reload, or css_plugins load after replacing the DLL) misses the map start and
    // the connects of everyone already playing. On a cold start this runs on the first world update.
    private void PickUpRunningMatch()
    {
        var services = _serviceProvider!;
        services.GetRequiredService<IGameScheduler>().Schedule(() =>
        {
            var playerSessions = services.GetRequiredService<IPlayerSessionService>();
            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsBot && player.SteamID != 0 && PlayerLifecycleHandler.IsAuthorized(player))
                    playerSessions.EnsurePlayer(PlayerControllerState.From(player));
            }

            var matchTracker = services.GetRequiredService<IMatchTrackingService>();
            if (matchTracker.CurrentMatch is null && !string.IsNullOrEmpty(Server.MapName))
                matchTracker.StartMatch(Server.MapName);
        });
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(_config);
        services.AddLogging(builder => { builder.ClearProviders(); builder.AddSerilog(_serilog); });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_cancellationTokenSource);

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
        services.AddSingleton<IStatsRepository, StatsRepository>();

        // Each processor is shared across its concrete and interface registrations.
        services.AddSingleton<CombatEventProcessor>();
        services.AddSingleton<UtilityEventProcessor>();
        services.AddSingleton<BombEventProcessor>();
        services.AddSingleton<EconomyEventProcessor>();
        services.AddSingleton<MovementEventProcessor>();

        // Combat first: its player_hurt handler computes the overkill-free damage the utility handler reuses.
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<CombatEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<UtilityEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<BombEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<EconomyEventProcessor>());
        services.AddSingleton<IEventProcessor>(sp => sp.GetRequiredService<MovementEventProcessor>());

        services.AddSingleton<ICombatEventProcessor>(sp => sp.GetRequiredService<CombatEventProcessor>());
        services.AddSingleton<IBombEventProcessor>(sp => sp.GetRequiredService<BombEventProcessor>());
        services.AddSingleton<IPositionTrackingService, PositionTrackingService>();
        services.AddSingleton<IPositionPersistenceService, PositionPersistenceService>();
        services.AddSingleton<IGameScheduler, GameScheduler>();
        services.AddSingleton<IPersistenceChannel, PersistenceChannel>();
        services.AddSingleton<IJsonRecoveryService, JsonRecoveryService>();
        services.AddSingleton<IConfigLoaderService, ConfigLoaderService>();
        services.AddSingleton<IRoundBackupService, RoundBackupService>();
        services.AddSingleton<IScrimManager, ScrimManager>();
        services.AddSingleton<IMatchStatsService, MatchStatsService>();
        services.AddSingleton<IDamageReportService, DamageReportService>();
        services.AddSingleton<IPauseService, PauseService>();
        services.AddSingleton<IPluginLifecycleService, PluginLifecycleService>();
        services.AddSingleton<IGameEventHandlerService, GameEventHandlerService>();

        services.AddSingleton<IGameHandler, MatchFlowHandler>();
        services.AddSingleton<IGameHandler, PlayerLifecycleHandler>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    public override void Unload(bool hotReload)
    {
        _logger.LogInformation("statsCollector plugin unloading... HotReload: {HotReload}", hotReload);

        try
        {
            // Pending scrim and pause timers must be killed here, on the game thread; the container is disposed on a
            // pool thread, where that native call runs unguarded against the engine (CounterStrikeSharp has no thread
            // check) and the timer could fire into the unloaded plugin.
            (_serviceProvider?.GetService<IScrimManager>() as IDisposable)?.Dispose();
            (_serviceProvider?.GetService<IPauseService>() as IDisposable)?.Dispose();

            _cancellationTokenSource.Cancel();
            // One budget for the whole cleanup (startup wait, final flushes, container disposal): this blocks the game
            // thread, and with the database down every step would otherwise wait out its own timeout.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            CleanupAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during plugin unload");
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_initializationTask is not null)
            {
                try
                {
                    await _initializationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
                {
                }
            }

            if (_lifecycle != null)
            {
                await _lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("statsCollector cleanup complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during plugin cleanup task");
        }
        finally
        {
            try
            {
                if (_serviceProvider is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (_serviceProvider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing plugin services");
            }
            finally
            {
                _cancellationTokenSource.Dispose();
                _serilog?.Dispose();
            }
        }
    }
}
