using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public sealed class PauseService(
    ILogger<PauseService> logger,
    IOptions<PluginConfig> config) : IPauseService
{
    private readonly ILogger<PauseService> _logger = logger;
    private readonly PluginConfig _config = config.Value;

    private bool _isPaused;
    private PauseType _currentPauseType = PauseType.None;
    private readonly Dictionary<int, int> _tacticalPausesUsed = new() { { 2, 0 }, { 3, 0 } };
    private bool _pauseRequested;
    private int _requestingTeam;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _pauseTimer;

    public bool IsPaused => _isPaused;
    public PauseType CurrentPauseType => _currentPauseType;

    public Task RequestPauseAsync(CCSPlayerController? player, PauseType type)
    {
        if (_isPaused) return Task.CompletedTask;

        if (player != null)
        {
            var team = player.TeamNum;
            if (team is < 2 or > 3) return Task.CompletedTask;

            if (type == PauseType.Tactical)
            {
                if (_tacticalPausesUsed[team] >= (_config.Scrim.MaxTacticalPauses > 0 ? _config.Scrim.MaxTacticalPauses : 4))
                {
                    player.PrintToChat(" [Scrim] Your team has no tactical pauses left.");
                    return Task.CompletedTask;
                }
            }
            
            _requestingTeam = team;
        }

        _pauseRequested = true;
        _currentPauseType = type;
        
        var typeStr = type == PauseType.Technical ? "Technical" : "Tactical";
        Server.PrintToChatAll($" [Scrim] {typeStr} pause requested by Team {(_requestingTeam == 2 ? "T" : "CT")}. It will take effect at the end of the round.");
        
        return Task.CompletedTask;
    }

    public Task RequestUnpauseAsync(CCSPlayerController? player)
    {
        if (!_isPaused) return Task.CompletedTask;

        // Admin can always unpause
        if (player == null || AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            Unpause();
            return Task.CompletedTask;
        }

        // MatchZy logic: Unpause requires both teams to be ready or just the requesting team to unpause if it was tactical?
        // Typically, any team can unpause if both are ready.
        Server.PrintToChatAll($" [Scrim] Player {player.PlayerName} requested unpause. Waiting for admin or both teams.");
        return Task.CompletedTask;
    }

    public void OnRoundEnd()
    {
        if (_pauseRequested)
        {
            Pause();
            _pauseRequested = false;
        }
    }

    private void Pause()
    {
        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (gameRules == null)
        {
            _logger.LogError("Failed to find GameRules for pausing.");
            return;
        }

        _isPaused = true;
        
        // Use GameRules for a cleaner pause if available, otherwise fallback to command
        Server.ExecuteCommand("mp_pause_match");
        
        _logger.LogInformation("Match paused. Type: {Type}", _currentPauseType);
        
        if (_currentPauseType == PauseType.Tactical)
        {
            _tacticalPausesUsed[_requestingTeam]++;
            
            // Tactical pauses usually have a duration
            var duration = _config.Scrim.TacticalPauseDuration > 0 ? _config.Scrim.TacticalPauseDuration : 30;
            _pauseTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(duration, () => 
            {
                Server.PrintToChatAll(" [Scrim] Tactical pause finished. Unpausing...");
                Unpause();
            });
        }
    }

    private void Unpause()
    {
        _pauseTimer?.Kill();
        _isPaused = false;
        _currentPauseType = PauseType.None;
        Server.ExecuteCommand("mp_unpause_match");
        _logger.LogInformation("Match unpaused.");
    }
}
