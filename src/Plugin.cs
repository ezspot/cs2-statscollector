using System;
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
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;
using statsCollector.Infrastructure.Database;
using statsCollector.Services;

namespace statsCollector;

public sealed class Plugin(ILogger<Plugin> logger) : BasePlugin, IPluginConfig<PluginConfig>
{
    internal const string StatsCollectorVersion = "1.0.0";

    public override string ModuleName => "statsCollector";
    public override string ModuleVersion => StatsCollectorVersion;
    public override string ModuleAuthor => "Anders Giske Hagen";
    public override string ModuleDescription => "Comprehensive CS2 stats collection to MySQL";

    private IServiceProvider? serviceProvider;
    private readonly ILogger<Plugin> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private PluginConfig config = new();

    private IPlayerSessionService? playerSessions;
    private IStatsRepository? statsRepository;
    private ICombatEventProcessor? combatProcessor;
    private IUtilityEventProcessor? utilityProcessor;
    private IBombEventProcessor? bombProcessor;
    private IEconomyEventProcessor? economyProcessor;
    private IPositionTrackingService? positionTracking;
    private IStatsPersistenceService? statsPersistence;

    private int currentRoundNumber;
    private readonly CancellationTokenSource cancellationTokenSource = new();

    public PluginConfig Config { get; set; } = new();

    public void OnConfigParsed(PluginConfig config)
    {
        try
        {
            config = config.WithEnvironmentOverrides();
            config.Validate();
            this.config = config;
            logger.LogInformation("Configuration validated successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Configuration validation failed");
            throw;
        }
    }

    public override void Load(bool hotReload)
    {
        logger.LogInformation("statsCollector v{Version} loading... HotReload: {HotReload}", StatsCollectorVersion, hotReload);

        try
        {
            // Build service provider with proper DI patterns
            var services = new ServiceCollection();

            // Add configuration
            services.AddSingleton(Options.Create(config));
            services.AddSingleton<IOptionsMonitor<PluginConfig>>(new StaticOptionsMonitor<PluginConfig>(config));

            // Add logging
            services.AddSingleton(Logger);
            services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(config.LogLevel));

            // Add infrastructure
            services.AddSingleton<IConnectionFactory, ConnectionFactory>();
            services.AddSingleton<IAsyncPolicy>(provider => 
                ResiliencePolicies.CreateDatabasePolicy(provider.GetRequiredService<ILogger<Plugin>>()));
            services.AddSingleton(TimeProvider.System);

            // Add services with proper lifetimes
            services.AddSingleton<IPlayerSessionService, PlayerSessionService>();
            services.AddTransient<IStatsRepository, StatsRepository>();
            services.AddTransient<ICombatEventProcessor, CombatEventProcessor>();
            services.AddTransient<IUtilityEventProcessor, UtilityEventProcessor>();
            services.AddTransient<IBombEventProcessor, BombEventProcessor>();
            services.AddTransient<IEconomyEventProcessor, EconomyEventProcessor>();
            services.AddTransient<IPositionTrackingService, PositionTrackingService>();
            services.AddSingleton<IStatsPersistenceService, StatsPersistenceService>();

            serviceProvider = services.BuildServiceProvider();

            // Resolve services
            playerSessions = serviceProvider.GetRequiredService<IPlayerSessionService>();
            statsRepository = serviceProvider.GetRequiredService<IStatsRepository>();
            combatProcessor = serviceProvider.GetRequiredService<ICombatEventProcessor>();
            utilityProcessor = serviceProvider.GetRequiredService<IUtilityEventProcessor>();
            bombProcessor = serviceProvider.GetRequiredService<IBombEventProcessor>();
            economyProcessor = serviceProvider.GetRequiredService<IEconomyEventProcessor>();
            positionTracking = serviceProvider.GetRequiredService<IPositionTrackingService>();
            statsPersistence = serviceProvider.GetRequiredService<IStatsPersistenceService>();
            statsPersistence.StartAsync(cancellationTokenSource.Token).GetAwaiter().GetResult();

            logger.LogInformation("statsCollector plugin loaded successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load statsCollector plugin");
            throw;
        }
    }

    public override void Unload(bool hotReload)
    {
        logger.LogInformation("statsCollector plugin unloading... HotReload: {HotReload}", hotReload);

        try
        {
            // Flush stats before cancellation to allow graceful drain
            SaveAllStatsAsync().GetAwaiter().GetResult();
            statsPersistence?.StopAsync(TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();

            cancellationTokenSource.Cancel();

            // Dispose services
            cancellationTokenSource.Dispose();

            logger.LogInformation("statsCollector plugin unloaded successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during plugin unload");
        }
    }

    private async Task SaveAllStatsAsync()
    {
        if (playerSessions == null || statsPersistence == null) return;

        try
        {
            var snapshots = playerSessions.CaptureSnapshots();
            if (snapshots.Length > 0)
            {
                logger.LogInformation("Saving {Count} player snapshots on unload...", snapshots.Length);
                await statsPersistence.EnqueueAsync(snapshots, CancellationToken.None);
                await statsPersistence.StopAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                logger.LogInformation("Stats saved successfully");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save stats during unload");
        }
    }

    #region Player Lifecycle Events
    [GameEventHandler]
    public HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
        try
        {
            logger.LogDebug("Player connected: {SteamId}", @event.GetPlayerOrDefault("userid")?.SteamID);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerConnect");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                // Save player stats when they disconnect
                _ = SavePlayerStatsOnDisconnectAsync(player);
            }
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerDisconnect");
            return HookResult.Continue;
        }
    }

    private async Task SavePlayerStatsOnDisconnectAsync(CCSPlayerController player)
    {
        if (playerSessions == null || statsRepository == null) return;

        try
        {
            var snapshot = playerSessions.TryGetSnapshot(player.SteamID, out var ps) ? ps : null;
            if (snapshot != null)
            {
                await statsRepository.UpsertPlayerAsync(snapshot, cancellationTokenSource.Token);
                logger.LogDebug("Saved stats for disconnecting player {SteamId}", player.SteamID);
            }

            playerSessions.TryRemovePlayer(player.SteamID, out _);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save stats for disconnecting player {SteamId}", player.SteamID);
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                playerSessions?.MutatePlayer(player.SteamID, stats =>
                {
                    stats.TotalSpawns++;
                    stats.PlaytimeSeconds = (int)(DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
                });
            }
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerSpawn");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                var team = @event.GetIntValue("team", 0);
                playerSessions?.MutatePlayer(player.SteamID, stats =>
                {
                    if (team == 2) // Terrorist
                        stats.TRounds++;
                    else if (team == 3) // Counter-Terrorist
                        stats.CtRounds++;
                });
            }
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerTeam");
            return HookResult.Continue;
        }
    }

    #endregion

    #region Combat Events

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        try
        {
            combatProcessor?.HandlePlayerDeath(@event);
            bombProcessor?.HandlePlayerDeath(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerDeath");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        try
        {
            combatProcessor?.HandlePlayerHurt(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerHurt");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        try
        {
            combatProcessor?.HandleWeaponFire(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnWeaponFire");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnBulletImpact(EventBulletImpact @event, GameEventInfo info)
    {
        try
        {
            combatProcessor?.HandleBulletImpact(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnBulletImpact");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        try
        {
            combatProcessor?.HandleRoundMvp(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnRoundMvp");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerAvengedTeammate(EventPlayerAvengedTeammate @event, GameEventInfo info)
    {
        try
        {
            combatProcessor?.HandlePlayerAvengedTeammate(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerAvengedTeammate");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawned(EventPlayerSpawned @event, GameEventInfo info)
    {
        try
        {
            combatProcessor?.HandlePlayerSpawned(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerSpawned");
            return HookResult.Continue;
        }
    }

    #endregion

    #region Utility Events

    [GameEventHandler]
    public HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        try
        {
            utilityProcessor?.HandlePlayerBlind(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnPlayerBlind");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event, GameEventInfo info)
    {
        try
        {
            utilityProcessor?.HandleHegrenadeDetonate(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnHegrenadeDetonate");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnFlashbangDetonate(EventFlashbangDetonate @event, GameEventInfo info)
    {
        try
        {
            utilityProcessor?.HandleFlashbangDetonate(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnFlashbangDetonate");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        try
        {
            utilityProcessor?.HandleSmokegrenadeDetonate(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnSmokegrenadeDetonate");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnMolotovDetonate(EventMolotovDetonate @event, GameEventInfo info)
    {
        try
        {
            utilityProcessor?.HandleMolotovDetonate(@event);
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnMolotovDetonate");
            return HookResult.Continue;
        }
    }

    #endregion

    #region Round Events

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        try
        {
            if (config.DeathmatchMode)
            {
                // In deathmatch we do not reset round-based state; keep aggregates only.
                logger.LogDebug("Deathmatch mode enabled: skipping round start resets");
                return HookResult.Continue;
            }

            currentRoundNumber = @event.GetIntValue("round_number", currentRoundNumber + 1);
            var roundStartUtc = DateTime.UtcNow;

            // Determine alive counts at start
            var ctAlive = 0;
            var tAlive = 0;
            playerSessions?.ForEachPlayer(stats =>
            {
                var team = stats.CurrentTeam;
                if (team == PlayerTeam.CounterTerrorist) ctAlive++;
                else if (team == PlayerTeam.Terrorist) tAlive++;
            });

            combatProcessor?.SetRoundContext(currentRoundNumber, roundStartUtc, ctAlive, tAlive);

            // Reset round-specific stats using combat processor
            combatProcessor?.ResetRoundStats();
            bombProcessor?.ResetBombState();

            // Reset player round stats
            playerSessions?.ForEachPlayer(stats =>
            {
                stats.RoundNumber = currentRoundNumber;
                stats.RoundStartUtc = roundStartUtc;
                stats.AliveOnTeamAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? ctAlive : stats.CurrentTeam == PlayerTeam.Terrorist ? tAlive : 0;
                stats.AliveEnemyAtRoundStart = stats.CurrentTeam == PlayerTeam.CounterTerrorist ? tAlive : stats.CurrentTeam == PlayerTeam.Terrorist ? ctAlive : 0;
                stats.ResetRoundStats();
            });

            logger.LogDebug("Round started, reset player round stats");
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnRoundStart");
            return HookResult.Continue;
        }
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        try
        {
            if (config.DeathmatchMode)
            {
                // No round-based accounting in deathmatch; skip.
                return HookResult.Continue;
            }

            var winningTeamInt = @event.GetIntValue("winner", 0);
            var winningTeam = winningTeamInt switch
            {
                2 => PlayerTeam.Terrorist,
                3 => PlayerTeam.CounterTerrorist,
                _ => PlayerTeam.Spectator
            };

            // Update clutch statistics using combat processor
            combatProcessor?.UpdateClutchStats(winningTeam);

            // KAST + rounds played update
            playerSessions?.ForEachPlayer(stats =>
            {
                stats.RoundsPlayed++;
                if (winningTeam != PlayerTeam.Spectator && stats.CurrentTeam == winningTeam)
                {
                    stats.RoundsWon++;
                }

                var contributedToKast = stats.HadKillThisRound || stats.HadAssistThisRound || stats.SurvivedThisRound || stats.DidTradeThisRound;
                if (contributedToKast)
                {
                    stats.KASTRounds++;
                }
            });

            // Handle bomb-related round end events
            bombProcessor?.HandleRoundEnd(@event);

            // Save stats at round end
            _ = SaveStatsAtRoundEndAsync();
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnRoundEnd");
            return HookResult.Continue;
        }
    }

    private async Task SaveStatsAtRoundEndAsync()
    {
        if (playerSessions == null || statsPersistence == null) return;

        try
        {
            var snapshots = playerSessions.CaptureSnapshots();
            if (snapshots.Length > 0)
            {
                await statsPersistence.EnqueueAsync(snapshots, cancellationTokenSource.Token);
                logger.LogDebug("Queued {Count} player stats at round end", snapshots.Length);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save stats at round end");
        }
    }

    #endregion

    #region Commands

    [ConsoleCommand("css_stats", "Show current stats collection status")]
    [ConsoleCommand("css_statscollector", "Show current stats collection status")]
    public void OnStatsCommand(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            if (player == null)
            {
                // Server console command
                var playerCount = playerSessions?.GetActiveSteamIds().Count ?? 0;
                Logger.LogInformation("statsCollector Status: {PlayerCount} active players being tracked", playerCount);
                return;
            }

            // Player command
            if (playerSessions != null)
            {
                var snapshot = playerSessions.TryGetSnapshot(player.SteamID, out var ps) ? ps : null;
                if (snapshot != null)
                {
                    var kdRatio = snapshot.KDRatio;
                    var adr = snapshot.AverageDamagePerRound;
                    var hltvRating = snapshot.HLTVRating;
                    var kast = snapshot.KASTPercentage;
                    var rank = snapshot.GetPlayerRank();
                    var bestWeapon = snapshot.GetBestWeaponByKills();
                    
                    var message = $"K:{snapshot.Kills} D:{snapshot.Deaths} A:{snapshot.Assists} H:{snapshot.Headshots} DMG:{snapshot.DamageDealt} K/D:{kdRatio:F2} ADR:{adr:F0} HLTV:{hltvRating:F2} KAST:{kast:F0}% Rank:{rank} Best:{bestWeapon}";
                    player.PrintToChat($"[statsCollector] {message}");
                }
                else
                {
                    player.PrintToChat("[statsCollector] No stats available yet");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OnStatsCommand");
            player?.PrintToChat("[statsCollector] Error retrieving stats");
        }
    }

    #endregion
}
