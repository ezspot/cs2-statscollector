using System;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;
using statsCollector.Services;
using statsCollector.Services.Handlers;

namespace statsCollector.Services;

public interface IGameEventHandlerService
{
    void RegisterEvents(BasePlugin plugin);
}

    public sealed class GameEventHandlerService : IGameEventHandlerService
    {
        private readonly ILogger<GameEventHandlerService> _logger;
        private readonly IPlayerSessionService _playerSessions;
        private readonly IScrimManager _scrimManager;
        private readonly IPauseService _pauseService;
        private readonly IRoundBackupService _roundBackup;
        private readonly IOptionsMonitor<PluginConfig> _configMonitor;
        private readonly IAnalyticsService _analytics;
        private readonly IMatchReadyService _matchReady;
        private readonly IEnumerable<IGameHandler> _handlers;
        private readonly IGameScheduler _scheduler;

        private PluginConfig _config => _configMonitor.CurrentValue;

        public GameEventHandlerService(
            ILogger<GameEventHandlerService> logger,
            IPlayerSessionService playerSessions,
            IScrimManager scrimManager,
            IPauseService pauseService,
            IRoundBackupService roundBackup,
            IOptionsMonitor<PluginConfig> configMonitor,
            IAnalyticsService analytics,
            IMatchReadyService matchReady,
            IEnumerable<IGameHandler> handlers,
            IGameScheduler scheduler)
        {
            _logger = logger;
            _playerSessions = playerSessions;
            _scrimManager = scrimManager;
            _pauseService = pauseService;
            _roundBackup = roundBackup;
            _configMonitor = configMonitor;
            _analytics = analytics;
            _matchReady = matchReady;
            _handlers = handlers;
            _scheduler = scheduler;
        }

        public void RegisterEvents(BasePlugin plugin)
        {
            // Register all modular handlers
            foreach (var handler in _handlers)
            {
                handler.Register(plugin);
            }

            // Commands
            plugin.AddCommand("css_stats", "Show current stats", (player, info) => { if (ValidatePlayer(player)) OnStatsCommand(player!, info); });
            plugin.AddCommand("css_scrim", "Scrim system admin command", (player, info) => { if (ValidatePlayer(player)) OnScrimCommand(player!, info); });
            plugin.AddCommand("css_ready", "Player ready command", (player, info) => { if (ValidatePlayer(player)) OnReadyCommand(player!, info); });
            plugin.AddCommand("css_unready", "Player unready command", (player, info) => { if (ValidatePlayer(player)) OnUnreadyCommand(player!, info); });
            plugin.AddCommand("css_vote", "Player vote command", (player, info) => { if (ValidatePlayer(player)) OnVoteCommand(player!, info); });
            plugin.AddCommand("css_pick", "Captain pick command", (player, info) => { if (ValidatePlayer(player)) OnPickCommand(player!, info); });
            plugin.AddCommand("css_ct", "Select CT side after knife round", (player, info) => { if (ValidatePlayer(player)) OnCtCommand(player!, info); });
            plugin.AddCommand("css_pause", "Pause the match", (player, info) => { if (ValidatePlayer(player)) OnPauseCommand(player, info); });
            plugin.AddCommand("css_unpause", "Unpause the match", (player, info) => { if (ValidatePlayer(player)) OnUnpauseCommand(player, info); });
            plugin.AddCommand("css_restore", "Restore a previous round", (player, info) => { if (ValidatePlayer(player)) OnRestoreCommand(player!, info); });
            plugin.AddCommand("css_t", "Select T side", (player, info) => { if (ValidatePlayer(player)) OnTCommand(player!, info); });
        }

        private bool ValidatePlayer(CCSPlayerController? player)
        {
            if (player == null) return true; // Console is allowed
            if (!player.IsValid) return false;
            // In CS2, a valid SteamID is required for persistent stats. 0 usually indicates a bot or unauthenticated player.
            if (player.SteamID == 0) return false;
            return true;
        }

        #region Command Handlers
    private void OnStatsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null)
        {
            _logger.LogInformation("statsCollector: {Count} active players", _playerSessions.GetActiveSteamIds().Count);
            return;
        }

        if (_playerSessions.TryGetPlayer(player.SteamID, out var stats))
        {
            var adr = _analytics.CalculateADR(stats);
            var rating = _analytics.CalculateHLTVRating(stats);
            var perfScore = _analytics.CalculatePerformanceScore(stats);
            var rank = _analytics.GetPlayerRank(perfScore);
            
            player.PrintToChat($" [statsCollector] K:{stats.Combat.Kills} D:{stats.Combat.Deaths} A:{stats.Combat.Assists} ADR:{adr:F0} Rating:{rating:F2} Rank:{rank}");
        }
    }


    private void OnScrimCommand(CCSPlayerController? player, CommandInfo command)
    {
        var subCommand = command.GetArg(1).ToLower();
        // Admin commands
        if (subCommand is "start" or "stop" or "setcaptain" or "set" or "recover" or "practice" or "veto")
        {
            if (player != null && !AdminUtils.HasPermission(player, "@css/root", "@css/admin", "@css/scrimadmin"))
            {
                player.PrintToChat(" [Scrim] You do not have permission to use this command.");
                return;
            }

            switch (subCommand)
            {
                case "start": _ = _scrimManager.StartScrimAsync(); break;
                case "stop": _ = _scrimManager.StopScrimAsync(); break;
                case "recover": _ = _scrimManager.RecoverAsync(); break;
                case "practice":
                    if (command.ArgCount < 3) return;
                    var enable = command.GetArg(2).ToLower() == "on";
                    _ = _scrimManager.SetPracticeModeAsync(enable);
                    break;
                case "veto":
                    _ = _scrimManager.StartVetoAsync();
                    break;
                case "setcaptain":
                    if (command.ArgCount < 4) return;
                    var team = int.Parse(command.GetArg(2));
                    var target = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(3)));
                    if (target != null) _ = _scrimManager.SetCaptainAsync(team, target.SteamID);
                    break;
                case "set":
                    if (command.ArgCount < 4) return;
                    _scrimManager.SetOverride(command.GetArg(2), command.GetArg(3));
                    break;
            }
            return;
        }

        if (player == null) return;

        switch (subCommand)
        {
            case "ready": _ = _scrimManager.SetReadyAsync(player.SteamID, true); break;
            case "unready": _ = _scrimManager.SetReadyAsync(player.SteamID, false); break;
            case "vote":
                if (command.ArgCount < 3) return;
                _ = _scrimManager.VoteMapAsync(player.SteamID, command.GetArg(2));
                break;
            case "pick":
                if (command.ArgCount < 3) return;
                var pickTarget = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(2)));
                if (pickTarget != null) _ = _scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID);
                break;
            case "ct": _ = _scrimManager.SelectSideAsync(player.SteamID, "ct"); break;
            case "t": _ = _scrimManager.SelectSideAsync(player.SteamID, "t"); break;
        }
    }

    private void OnReadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _matchReady.SetReady(player.SteamID, true);
        if (_matchReady.AreAllReady())
        {
            Server.PrintToChatAll($" {ChatColors.Green}[Ready]{ChatColors.Default} All players are ready! Match starting...");
            _ = _scrimManager.StartScrimAsync();
        }
    }

    private void OnUnreadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _matchReady.SetReady(player.SteamID, false);
    }

    private void OnVoteCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        _ = _scrimManager.VoteMapAsync(player.SteamID, command.GetArg(1));
    }

    private void OnPickCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        var pickTarget = Utilities.GetPlayerFromSteamId(ulong.Parse(command.GetArg(1)));
        if (pickTarget != null) _ = _scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID);
    }

    private void OnCtCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _ = _scrimManager.SelectSideAsync(player.SteamID, "ct");
    }

    private void OnTCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        _ = _scrimManager.SelectSideAsync(player.SteamID, "t");
    }

    private void OnPauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        var type = PauseType.Tactical;
        if (command.ArgCount > 1)
        {
            var arg = command.GetArg(1).ToLower();
            type = arg switch
            {
                "tech" or "technical" => PauseType.Technical,
                "admin" => PauseType.Admin,
                _ => PauseType.Tactical
            };
        }

        if (type == PauseType.Admin && player != null && !AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            player.PrintToChat(" [Scrim] You do not have permission for admin pauses.");
            return;
        }

        _ = _pauseService.RequestPauseAsync(player, type);
    }

    private void OnUnpauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        _ = _pauseService.RequestUnpauseAsync(player);
    }

    private void OnRestoreCommand(CCSPlayerController player, CommandInfo command)
    {
        if (!AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            player.PrintToChat(" [statsCollector] You do not have permission to restore rounds.");
            return;
        }

        if (command.ArgCount < 2)
        {
            var rounds = string.Join(", ", _roundBackup.GetAvailableRounds());
            player.PrintToChat($" [statsCollector] Usage: .restore <round>. Available rounds: {rounds}");
            return;
        }

        if (int.TryParse(command.GetArg(1), out var roundNumber))
        {
            if (_roundBackup.RestoreRound(roundNumber))
            {
                player.PrintToChat($" [statsCollector] Round {roundNumber} restore initiated.");
            }
            else
            {
                player.PrintToChat($" [statsCollector] Failed to restore round {roundNumber}.");
            }
        }
    }
    #endregion
}
