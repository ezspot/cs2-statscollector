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

        // Admin pauses may come from a spectating admin; the other kinds are charged to the requester's team.
        if (player != null && type != PauseType.Admin)
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
        var requester = player is null ? "server console" : type == PauseType.Admin ? "an admin" : $"Team {(_requestingTeam == 2 ? "T" : "CT")}";
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] {typeStr} pause requested by {requester}. It will take effect at the end of the round."));
    }

    public void RequestUnpause(CCSPlayerController? player)
    {
        // Admins can also end pauses this service did not start, such as the engine's pause after a round restore.
        if (player == null || AdminUtils.HasPermission(player, "@css/root", "@css/admin"))
        {
            Unpause();
            return;
        }

        if (!_isPaused) return;
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
                    // CounterStrikeSharp frees the timer after this callback; a later Kill of the stale pointer could
                    // hit another timer allocated at the same address.
                    _pauseTimer = null;
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

    // Plugin.Unload calls this on the game thread (killing a timer is a native call); the container's later call on a
    // pool thread then finds nothing to kill.
    public void Dispose()
    {
        _pauseTimer?.Kill();
        _pauseTimer = null;
    }
}
