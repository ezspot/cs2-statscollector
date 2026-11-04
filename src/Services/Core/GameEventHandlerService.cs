using System;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
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
    private readonly IEnumerable<IGameHandler> _handlers;
    private readonly IEnumerable<IEventProcessor> _processors;
    private readonly IEventDispatcher _dispatcher;

    private PluginConfig _config => _configMonitor.CurrentValue;

    public GameEventHandlerService(
        ILogger<GameEventHandlerService> logger,
        IPlayerSessionService playerSessions,
        IScrimManager scrimManager,
        IPauseService pauseService,
        IRoundBackupService roundBackup,
        IOptionsMonitor<PluginConfig> configMonitor,
        IAnalyticsService analytics,
        IEnumerable<IGameHandler> handlers,
        IEnumerable<IEventProcessor> processors,
        IEventDispatcher dispatcher)
    {
        _logger = logger;
        _playerSessions = playerSessions;
        _scrimManager = scrimManager;
        _pauseService = pauseService;
        _roundBackup = roundBackup;
        _configMonitor = configMonitor;
        _analytics = analytics;
        _handlers = handlers;
        _processors = processors;
        _dispatcher = dispatcher;
    }

    public void RegisterEvents(BasePlugin plugin)
    {
        foreach (var handler in _handlers)
        {
            handler.Register(plugin);
        }

        _dispatcher.Initialize(plugin);
        foreach (var processor in _processors)
        {
            processor.RegisterEvents(_dispatcher);
        }

        plugin.AddCommand("css_stats", "Show current stats", (player, info) => { if (ValidatePlayer(player)) OnStatsCommand(player!, info); });

        if (!_config.EnableScrim) return;

        plugin.AddCommand("css_scrim", "Scrim system admin command", (player, info) => { if (ValidatePlayer(player)) OnScrimCommand(player!, info); });
        plugin.AddCommand("css_ready", "Player ready command", (player, info) => { if (ValidatePlayer(player)) OnReadyCommand(player!, info); });
        plugin.AddCommand("css_unready", "Player unready command", (player, info) => { if (ValidatePlayer(player)) OnUnreadyCommand(player!, info); });
        plugin.AddCommand("css_vote", "Player vote command", (player, info) => { if (ValidatePlayer(player)) OnVoteCommand(player!, info); });
        plugin.AddCommand("css_pick", "Captain pick command", (player, info) => { if (ValidatePlayer(player)) OnPickCommand(player!, info); });
        plugin.AddCommand("css_ct", "Select CT side after knife round", (player, info) => { if (ValidatePlayer(player)) OnCtCommand(player!, info); });
        plugin.AddCommand("css_pause", "Pause the match", (player, info) => { if (ValidatePlayer(player)) OnPauseCommand(player, info); });
        plugin.AddCommand("css_unpause", "Unpause the match", (player, info) => { if (ValidatePlayer(player)) OnUnpauseCommand(player, info); });
        plugin.AddCommand("css_restore", "Restore a previous round", (player, info) => { if (ValidatePlayer(player)) OnRestoreCommand(player, info); });
        plugin.AddCommand("css_t", "Select T side", (player, info) => { if (ValidatePlayer(player)) OnTCommand(player!, info); });
        plugin.AddCommandListener("jointeam", OnJoinTeam);
    }

    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || player.IsBot || !int.TryParse(command.GetArg(1), out var team)) return HookResult.Continue;
        if (_scrimManager.CanJoinTeam(player.SteamID, team)) return HookResult.Continue;

        player.PrintToChat(" [Scrim] Picked players stay on their team's side.");
        return HookResult.Stop;
    }

    private static bool ValidatePlayer(CCSPlayerController? player)
    {
        if (player == null) return true;
        if (!player.IsValid) return false;
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

        var message = _playerSessions.WithPlayer(player.SteamID, stats =>
        {
            var adr = _analytics.CalculateADR(stats);
            var rating = _analytics.CalculateHLTVRating(stats);
            var perfScore = _analytics.CalculatePerformanceScore(stats);
            var rank = _analytics.GetPlayerRank(perfScore);

            return $" [statsCollector] K:{stats.Combat.Kills} D:{stats.Combat.Deaths} A:{stats.Combat.Assists} ADR:{adr:F0} Rating:{rating:F2} Rank:{rank}";
        }, string.Empty);
        if (message.Length > 0) player.PrintToChat(message);
    }


    private void OnScrimCommand(CCSPlayerController? player, CommandInfo command)
    {
        var subCommand = command.GetArg(1).ToLowerInvariant();
        if (subCommand is "start" or "stop" or "setcaptain" or "set" or "recover" or "practice")
        {
            if (player != null && !AdminUtils.HasPermission(player, "@css/root", "@css/admin", "@css/scrimadmin"))
            {
                player.PrintToChat(" [Scrim] You do not have permission to use this command.");
                return;
            }

            switch (subCommand)
            {
                case "start": RunCommand(_scrimManager.StartScrimAsync(), "start scrim"); break;
                case "stop": RunCommand(_scrimManager.StopScrimAsync(), "stop scrim"); break;
                case "recover": RunCommand(_scrimManager.RecoverAsync(), "recover scrim"); break;
                case "practice":
                    if (command.ArgCount < 3) return;
                    var enable = string.Equals(command.GetArg(2), "on", StringComparison.OrdinalIgnoreCase);
                    RunCommand(_scrimManager.SetPracticeModeAsync(enable), "set practice mode");
                    break;
                case "setcaptain":
                    if (command.ArgCount < 4) return;
                    if (!int.TryParse(command.GetArg(2), out var team) ||
                        !ulong.TryParse(command.GetArg(3), out var targetSteamId)) return;
                    var target = Utilities.GetPlayerFromSteamId64(targetSteamId);
                    if (target != null) RunCommand(_scrimManager.SetCaptainAsync(team, target.SteamID), "set captain");
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
            case "ready": RunCommand(_scrimManager.SetReadyAsync(player.SteamID, true), "ready player"); break;
            case "unready": RunCommand(_scrimManager.SetReadyAsync(player.SteamID, false), "unready player"); break;
            case "vote":
                if (command.ArgCount < 3) return;
                RunCommand(_scrimManager.VoteMapAsync(player.SteamID, command.GetArg(2)), "vote map");
                break;
            case "pick":
                if (command.ArgCount < 3) return;
                if (!ulong.TryParse(command.GetArg(2), out var pickTargetSteamId)) return;
                var pickTarget = Utilities.GetPlayerFromSteamId64(pickTargetSteamId);
                if (pickTarget != null) RunCommand(_scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID), "pick player");
                break;
            case "ct": RunCommand(_scrimManager.SelectSideAsync(player.SteamID, "ct"), "select CT side"); break;
            case "t": RunCommand(_scrimManager.SelectSideAsync(player.SteamID, "t"), "select T side"); break;
        }
    }

    private void OnReadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        RunCommand(_scrimManager.SetReadyAsync(player.SteamID, true), "ready player");
    }

    private void OnUnreadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        RunCommand(_scrimManager.SetReadyAsync(player.SteamID, false), "unready player");
    }

    private void OnVoteCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        RunCommand(_scrimManager.VoteMapAsync(player.SteamID, command.GetArg(1)), "vote map");
    }

    private void OnPickCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || command.ArgCount < 2) return;
        if (!ulong.TryParse(command.GetArg(1), out var targetSteamId)) return;
        var pickTarget = Utilities.GetPlayerFromSteamId64(targetSteamId);
        if (pickTarget != null) RunCommand(_scrimManager.PickPlayerAsync(player.SteamID, pickTarget.SteamID), "pick player");
    }

    private void OnCtCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        RunCommand(_scrimManager.SelectSideAsync(player.SteamID, "ct"), "select CT side");
    }

    private void OnTCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null) return;
        RunCommand(_scrimManager.SelectSideAsync(player.SteamID, "t"), "select T side");
    }

    private void OnPauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        var type = PauseType.Tactical;
        if (command.ArgCount > 1)
        {
            var arg = command.GetArg(1).ToLowerInvariant();
            type = arg switch
            {
                "tech" or "technical" => PauseType.Technical,
                "admin" => PauseType.Admin,
                _ => PauseType.Tactical
            };
        }

        if (player is null && type == PauseType.Tactical) type = PauseType.Admin;

        if (type == PauseType.Admin && player != null && !AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            player.PrintToChat(" [Scrim] You do not have permission for admin pauses.");
            return;
        }

        _pauseService.RequestPause(player, type);
    }

    private void OnUnpauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        _pauseService.RequestUnpause(player);
    }

    private void OnRestoreCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not null && !AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            command.ReplyToCommand(" [statsCollector] You do not have permission to restore rounds.");
            return;
        }

        if (command.ArgCount < 2)
        {
            var rounds = string.Join(", ", _roundBackup.GetAvailableRounds());
            command.ReplyToCommand($" [statsCollector] Usage: .restore <round>. Available rounds: {rounds}");
            return;
        }

        if (int.TryParse(command.GetArg(1), out var roundNumber))
        {
            if (_roundBackup.RestoreRound(roundNumber))
            {
                command.ReplyToCommand($" [statsCollector] Round {roundNumber} restore initiated.");
            }
            else
            {
                command.ReplyToCommand($" [statsCollector] No backup of round {roundNumber}: the plugin must have seen it start, and round backups need the -allow_round_backup_files server launch flag.");
            }
        }
    }

    private void RunCommand(Task command, string operation) => _ = ObserveCommandAsync(command, operation);

    private async Task ObserveCommandAsync(Task command, string operation)
    {
        try
        {
            await command.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Operation}", operation);
        }
    }
    #endregion
}
