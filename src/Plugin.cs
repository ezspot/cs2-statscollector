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

    private CounterStrikeSharp.API.Modules.Timers.Timer? _autoSaveTimer;

    private MeterProvider? _meterProvider;
    private TracerProvider? _tracerProvider;

    private int _currentRoundNumber;
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

        try
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
            services.AddSingleton<IPlayerSessionService, PlayerSessionService>();
            services.AddTransient<IStatsRepository, StatsRepository>();
            services.AddTransient<ICombatEventProcessor, CombatEventProcessor>();
            services.AddTransient<IUtilityEventProcessor, UtilityEventProcessor>();
            services.AddTransient<IBombEventProcessor, BombEventProcessor>();
            services.AddTransient<IEconomyEventProcessor, EconomyEventProcessor>();
            services.AddTransient<IPositionTrackingService, PositionTrackingService>();
            services.AddSingleton<IStatsPersistenceService, StatsPersistenceService>();
            services.AddSingleton<IPositionPersistenceService, PositionPersistenceService>();

            _serviceProvider = services.BuildServiceProvider();

            // Resolve
            _playerSessions = _serviceProvider.GetRequiredService<IPlayerSessionService>();
            _statsRepository = _serviceProvider.GetRequiredService<IStatsRepository>();
            _combatProcessor = _serviceProvider.GetRequiredService<ICombatEventProcessor>();
            _utilityProcessor = _serviceProvider.GetRequiredService<IUtilityEventProcessor>();
            _bombProcessor = _serviceProvider.GetRequiredService<IBombEventProcessor>();
            _economyProcessor = _serviceProvider.GetRequiredService<IEconomyEventProcessor>();
            _statsPersistence = _serviceProvider.GetRequiredService<IStatsPersistenceService>();
            _positionPersistence = _serviceProvider.GetRequiredService<IPositionPersistenceService>();
            
            _statsPersistence.StartAsync(_cancellationTokenSource.Token).GetAwaiter().GetResult();
            _positionPersistence.StartAsync(_cancellationTokenSource.Token).GetAwaiter().GetResult();

            // Register listeners
            RegisterListener<Listeners.OnClientAuthorized>((slot, steamId) =>
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player != null && !player.IsBot)
                {
                    _logger.LogInformation("Client authorized listener: {Name} (SteamID: {SteamID})", player.PlayerName, steamId);
                    _playerSessions?.EnsurePlayer(steamId.SteamId64, player.PlayerName);
                }
            });

            // Auto-save timer
            var autoSaveInterval = Math.Max(30, _config.AutoSaveSeconds);
            _autoSaveTimer = AddTimer(autoSaveInterval, () => 
            {
                _ = SaveAllStatsAsync();
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

            _logger.LogInformation("statsCollector plugin loaded successfully with full observability. AutoSave interval: {Interval}s", autoSaveInterval);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load statsCollector plugin");
            throw;
        }
    }

    public override void Unload(bool hotReload)
    {
        _logger.LogInformation("statsCollector plugin unloading...");

        try
        {
            _autoSaveTimer?.Kill();
            SaveAllStatsAsync().GetAwaiter().GetResult();
            _statsPersistence?.StopAsync(TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
            _positionPersistence?.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();

            _cancellationTokenSource.Cancel();
            _tracerProvider?.Dispose();
            _meterProvider?.Dispose();
            Log.CloseAndFlush();
            _cancellationTokenSource.Dispose();

            _logger.LogInformation("statsCollector plugin unloaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during plugin unload");
        }
    }

    private async Task SaveAllStatsAsync()
    {
        if (_playerSessions == null || _statsPersistence == null) return;
        var snapshots = _playerSessions.CaptureSnapshots();
        if (snapshots.Length > 0)
        {
            _logger.LogInformation("Saving {Count} player snapshots on unload...", snapshots.Length);
            await _statsPersistence.EnqueueAsync(snapshots, CancellationToken.None);
        }
    }

    #region Player Lifecycle Events
    [GameEventHandler]
    public HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null)
        {
            _logger.LogInformation("Player connecting event: {Name} (SteamID: {SteamID}, IsBot: {IsBot})", 
                player.PlayerName, player.SteamID, player.IsBot);
            
            if (!player.IsBot && player.SteamID != 0)
            {
                _playerSessions?.EnsurePlayer(player.SteamID, player.PlayerName);
            }
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null)
        {
            _logger.LogInformation("Player disconnecting: {Name} (SteamID: {SteamID}, IsBot: {IsBot})", 
                player.PlayerName, player.SteamID, player.IsBot);
            
            if (!player.IsBot) _ = SavePlayerStatsOnDisconnectAsync(player);
        }
        return HookResult.Continue;
    }

    private async Task SavePlayerStatsOnDisconnectAsync(CCSPlayerController player)
    {
        if (_playerSessions == null || _statsRepository == null) return;
        if (_playerSessions.TryGetSnapshot(player.SteamID, out var snapshot))
        {
            await _statsRepository.UpsertPlayerAsync(snapshot, _cancellationTokenSource.Token);
            _playerSessions.TryRemovePlayer(player.SteamID, out _);
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player != null)
        {
            _logger.LogDebug("Player spawned: {Name} (SteamID: {SteamID}, Team: {Team})", 
                player.PlayerName, player.SteamID, player.TeamNum);

            if (!player.IsBot)
            {
                _playerSessions?.EnsurePlayer(player.SteamID, player.PlayerName);
                _playerSessions?.MutatePlayer(player.SteamID, stats =>
                {
                    stats.TotalSpawns++;
                    stats.PlaytimeSeconds = (int)(DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
                });
            }
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false })
        {
            var team = @event.GetIntValue("team", 0);
            _playerSessions?.MutatePlayer(player.SteamID, stats =>
            {
                if (team == 2) stats.TRounds++;
                else if (team == 3) stats.CtRounds++;
            });
        }
        return HookResult.Continue;
    }
    #endregion

    #region Combat Events
    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        _combatProcessor?.HandlePlayerDeath(@event);
        _bombProcessor?.HandlePlayerDeath(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        _combatProcessor?.HandlePlayerHurt(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        _combatProcessor?.HandleWeaponFire(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBulletImpact(EventBulletImpact @event, GameEventInfo info)
    {
        _combatProcessor?.HandleBulletImpact(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        _combatProcessor?.HandleRoundMvp(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerAvengedTeammate(EventPlayerAvengedTeammate @event, GameEventInfo info)
    {
        _combatProcessor?.HandlePlayerAvengedTeammate(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawned(EventPlayerSpawned @event, GameEventInfo info)
    {
        _combatProcessor?.HandlePlayerSpawned(@event);
        return HookResult.Continue;
    }
    #endregion

    #region Utility Events
    [GameEventHandler]
    public HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        _utilityProcessor?.HandlePlayerBlind(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event, GameEventInfo info)
    {
        _utilityProcessor?.HandleHegrenadeDetonate(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnFlashbangDetonate(EventFlashbangDetonate @event, GameEventInfo info)
    {
        _utilityProcessor?.HandleFlashbangDetonate(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        _utilityProcessor?.HandleSmokegrenadeDetonate(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnMolotovDetonate(EventMolotovDetonate @event, GameEventInfo info)
    {
        _utilityProcessor?.HandleMolotovDetonate(@event);
        return HookResult.Continue;
    }
    #endregion

    #region Economy Events
    [GameEventHandler]
    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        _economyProcessor?.HandleItemPurchase(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        _economyProcessor?.HandleItemPickup(@event);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemEquip(EventItemEquip @event, GameEventInfo info)
    {
        _economyProcessor?.HandleItemEquip(@event);
        return HookResult.Continue;
    }
    #endregion

    #region Round Events
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;

        _currentRoundNumber = @event.GetIntValue("round_number", _currentRoundNumber + 1);
        var roundStartUtc = DateTime.UtcNow;

        var ctAlive = 0;
        var tAlive = 0;
        _playerSessions?.ForEachPlayer(stats =>
        {
            if (stats.CurrentTeam == PlayerTeam.CounterTerrorist) ctAlive++;
            else if (stats.CurrentTeam == PlayerTeam.Terrorist) tAlive++;
        });

        _combatProcessor?.SetRoundContext(_currentRoundNumber, roundStartUtc, ctAlive, tAlive);
        _utilityProcessor?.SetRoundContext(_currentRoundNumber, roundStartUtc);
        _combatProcessor?.ResetRoundStats();
        _bombProcessor?.ResetBombState();

        _playerSessions?.ForEachPlayer(stats =>
        {
            stats.RoundNumber = _currentRoundNumber;
            stats.RoundStartUtc = roundStartUtc;
            stats.AliveOnTeamAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;
            stats.AliveEnemyAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
            stats.ResetRoundStats();
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_config.DeathmatchMode) return HookResult.Continue;

        Instrumentation.RoundsPlayedCounter.Add(1);
        var winningTeamInt = @event.GetIntValue("winner", 0);
        var winningTeam = winningTeamInt switch { 2 => PlayerTeam.Terrorist, 3 => PlayerTeam.CounterTerrorist, _ => PlayerTeam.Spectator };

        _combatProcessor?.UpdateClutchStats(winningTeam);
        _playerSessions?.ForEachPlayer(stats =>
        {
            stats.RoundsPlayed++;
            if (winningTeam != PlayerTeam.Spectator && stats.CurrentTeam == winningTeam) stats.RoundsWon++;
            if (stats.HadKillThisRound || stats.HadAssistThisRound || stats.SurvivedThisRound || stats.DidTradeThisRound) stats.KASTRounds++;
        });

        _bombProcessor?.HandleRoundEnd(@event);
        _ = SaveStatsAtRoundEndAsync();
        return HookResult.Continue;
    }

    private async Task SaveStatsAtRoundEndAsync()
    {
        if (_playerSessions == null || _statsPersistence == null) return;
        var snapshots = _playerSessions.CaptureSnapshots();
        if (snapshots.Length > 0) await _statsPersistence.EnqueueAsync(snapshots, _cancellationTokenSource.Token);
    }
    #endregion

    #region Bomb Events
    [GameEventHandler]
    public HookResult OnBombBeginplant(EventBombBeginplant @event, GameEventInfo info) { _bombProcessor?.HandleBombBeginplant(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombAbortplant(EventBombAbortplant @event, GameEventInfo info) { _bombProcessor?.HandleBombAbortplant(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info) { _bombProcessor?.HandleBombPlanted(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info) { _bombProcessor?.HandleBombDefused(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info) { _bombProcessor?.HandleBombExploded(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombDropped(EventBombDropped @event, GameEventInfo info) { _bombProcessor?.HandleBombDropped(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombPickup(EventBombPickup @event, GameEventInfo info) { _bombProcessor?.HandleBombPickup(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombBegindefuse(EventBombBegindefuse @event, GameEventInfo info) { _bombProcessor?.HandleBombBegindefuse(@event); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnBombAbortdefuse(EventBombAbortdefuse @event, GameEventInfo info) { _bombProcessor?.HandleBombAbortdefuse(@event); return HookResult.Continue; }
    #endregion

    #region Commands
    [ConsoleCommand("css_stats", "Show current stats")]
    public void OnStatsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null)
        {
            _logger.LogInformation("statsCollector: {Count} active players", _playerSessions?.GetActiveSteamIds().Count ?? 0);
            return;
        }

        if (_playerSessions != null && _playerSessions.TryGetSnapshot(player.SteamID, out var snapshot))
        {
            player.PrintToChat($"[statsCollector] K:{snapshot.Kills} D:{snapshot.Deaths} A:{snapshot.Assists} ADR:{snapshot.AverageDamagePerRound:F0} Rating:{snapshot.HLTVRating:F2} Rank:{snapshot.GetPlayerRank()}");
        }
    }
    #endregion
}
