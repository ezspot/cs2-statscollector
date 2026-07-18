using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public sealed class PauseService(
    ILogger<PauseService> logger,
    IOptions<PluginConfig> config,
    IGameScheduler scheduler) : IPauseService, IDisposable
{
    private readonly ILogger<PauseService> _logger = logger;
    private readonly PluginConfig _config = config.Value;
    private readonly IGameScheduler _scheduler = scheduler;

    private bool _isPaused;
    private PauseType _currentPauseType = PauseType.None;
    private readonly Dictionary<int, int> _tacticalPausesUsed = new() { { 2, 0 }, { 3, 0 } };
    private bool _pauseRequested;
    private int _requestingTeam;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _pauseTimer;

    public void RequestPause(CCSPlayerController? player, PauseType type)
    {
        if (_isPaused || _pauseRequested) return;

        if (player != null)
        {
            var team = player.TeamNum;
            if (team is < 2 or > 3) return;

            if (type == PauseType.Tactical)
            {
                if (_tacticalPausesUsed[team] >= (_config.Scrim.MaxTacticalPauses > 0 ? _config.Scrim.MaxTacticalPauses : 4))
                {
                    _scheduler.Schedule(() => player.PrintToChat(" [Scrim] Your team has no tactical pauses left."));
                    return;
                }
            }

            _requestingTeam = team;
        }

        _pauseRequested = true;
        _currentPauseType = type;

        var typeStr = type.ToString();
        var requester = player is null ? "server console" : $"Team {(_requestingTeam == 2 ? "T" : "CT")}";
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] {typeStr} pause requested by {requester}. It will take effect at the end of the round."));
    }

    public void RequestUnpause(CCSPlayerController? player)
    {
        if (!_isPaused) return;

        if (player == null || AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            Unpause();
            return;
        }

        player.PrintToChat(" [Scrim] An admin must unpause the match.");
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
        _scheduler.Schedule(() =>
        {
            _isPaused = true;
            Server.ExecuteCommand("mp_pause_match");

            _logger.LogInformation("Match paused. Type: {Type}", _currentPauseType);

            if (_currentPauseType == PauseType.Tactical)
            {
                _tacticalPausesUsed[_requestingTeam]++;

                var duration = _config.Scrim.TacticalPauseDuration > 0 ? _config.Scrim.TacticalPauseDuration : 30;
                _pauseTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(duration, () =>
                {
                    _scheduler.Schedule(() =>
                    {
                        Server.PrintToChatAll(" [Scrim] Tactical pause finished. Unpausing...");
                        Unpause();
                    });
                });
            }
        });
    }

    private void Unpause()
    {
        _scheduler.Schedule(() =>
        {
            _pauseTimer?.Kill();
            _pauseTimer = null;
            _isPaused = false;
            _pauseRequested = false;
            _currentPauseType = PauseType.None;
            _requestingTeam = 0;
            Server.ExecuteCommand("mp_unpause_match");
            _logger.LogInformation("Match unpaused.");
        });
    }

    public void Reset()
    {
        _pauseTimer?.Kill();
        _pauseTimer = null;
        if (_isPaused) _scheduler.Schedule(() => Server.ExecuteCommand("mp_unpause_match"));
        _isPaused = false;
        _pauseRequested = false;
        _currentPauseType = PauseType.None;
        _requestingTeam = 0;
        _tacticalPausesUsed[2] = 0;
        _tacticalPausesUsed[3] = 0;
    }

    public void Dispose() => _pauseTimer?.Kill();
}
