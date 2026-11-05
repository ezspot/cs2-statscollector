using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public sealed class ScrimManager : IScrimManager, IDisposable
{
    // After a map change the knife round or match waits in warmup until the picked players are back.
    private const float PlayerLoadTimeoutSeconds = 180f;

    private readonly ILogger<ScrimManager> _logger;
    private readonly IOptionsMonitor<PluginConfig> _configMonitor;
    private readonly IConfigLoaderService _configLoader;
    private readonly IJsonRecoveryService _recovery;
    private readonly IGameScheduler _scheduler;

    private PluginConfig _config => _configMonitor.CurrentValue;

    private readonly Dictionary<ulong, bool> _readyPlayers = [];
    private readonly Dictionary<int, ulong> _captains = [];
    private readonly Dictionary<string, int> _mapVotes = [];
    private readonly Dictionary<ulong, string> _playerVotes = [];

    private string? _selectedMap;
    private int _playersPerTeam;
    private int _minReadyPlayers;
    private readonly List<ulong> _team1 = [];
    private readonly List<ulong> _team2 = [];
    private readonly List<ulong> _pickPool = [];
    private bool _team1Picking = true;
    private int _pickTurn = 0;

    private int _knifeWinnerTeam;
    // CounterStrikeSharp frees a one-shot timer after it fires; the fields are cleared then, because killing the
    // stale pointer later could kill whichever timer was allocated at the same address.
    private CounterStrikeSharp.API.Modules.Timers.Timer? _sideSelectionTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _playerLoadTimer;
    private bool _waitingForPlayers;

    private volatile ScrimState _state = ScrimState.Idle;

    public ScrimState CurrentState => _state;

    public ScrimManager(
        ILogger<ScrimManager> logger,
        IOptionsMonitor<PluginConfig> configMonitor,
        IConfigLoaderService configLoader,
        IJsonRecoveryService recovery,
        IGameScheduler scheduler)
    {
        _logger = logger;
        _configMonitor = configMonitor;
        _configLoader = configLoader;
        _recovery = recovery;
        _scheduler = scheduler;

        _playersPerTeam = _config.Scrim.PlayersPerTeam;
        _minReadyPlayers = _config.Scrim.MinReadyPlayers;
    }

    private async Task TransitionToAsync(ScrimState target)
    {
        // Every transition starts on the game thread: killing a timer is a native call, and CounterStrikeSharp does not
        // guard natives against other threads (an off-thread call races the engine instead of failing).
        if (target is not (ScrimState.KnifeRound or ScrimState.Live)) ClearPhaseState();

        var from = _state;
        if (from == target) return;

        if (from == ScrimState.Practice)
        {
            await _configLoader.LoadAndExecuteConfigAsync(_config.Scrim.LiveConfigPath);
        }

        _state = target;
        _logger.LogInformation("Scrim state transition: {From} -> {To}", from, target);

        switch (target)
        {
            case ScrimState.Lobby:
                await OnLobbyEntry();
                break;
            case ScrimState.Practice:
                await _configLoader.LoadAndExecuteConfigAsync("practice.cfg");
                _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Practice Mode enabled! God, noclip, and infinite ammo active."));
                break;
            case ScrimState.CaptainSetup:
                _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Ready players confirmed. Admins, use .scrim setcaptain <1|2> <SteamID64>."));
                break;
            case ScrimState.MapVote:
                OnMapVoteEntry();
                break;
            case ScrimState.Picking:
                // Nobody to pick (every ready player is a captain): straight to the knife round or match.
                if (!StartPicking()) await TransitionToAsync(_config.Scrim.KnifeRoundEnabled ? ScrimState.KnifeRound : ScrimState.Live);
                break;
            case ScrimState.KnifeRound:
                await OnKnifeRoundEntry();
                break;
            case ScrimState.Live:
                await OnLiveEntry();
                // knife.cfg plays the knife round as live round 1; restart so the match starts at 0:0 with
                // live economy (queued after live.cfg's commands, and after the side swap moves players).
                if (from == ScrimState.KnifeRound)
                    _scheduler.Schedule(() => Server.ExecuteCommand("mp_restartgame 1"));
                break;
        }

        await SaveCurrentStateAsync();
    }

    private async Task SaveCurrentStateAsync()
    {
        ScrimRecoveryData? data = null;
        await RunOnGameThreadAsync(() => data = new ScrimRecoveryData(
            CurrentState,
            [.. _team1],
            [.. _team2],
            new Dictionary<int, ulong>(_captains),
            _selectedMap,
            DateTime.UtcNow,
            ReadyPlayers: new Dictionary<ulong, bool>(_readyPlayers),
            MapVotes: new Dictionary<string, int>(_mapVotes),
            PlayerVotes: new Dictionary<ulong, string>(_playerVotes),
            PickPool: [.. _pickPool],
            Team1Picking: _team1Picking,
            PickTurn: _pickTurn,
            KnifeWinnerTeam: _knifeWinnerTeam));
        await _recovery.SaveScrimStateAsync(data!);
    }

    public async Task RecoverAsync()
    {
        // Recovery is for a scrim the server lost (crash, restart); it must not replace one that is running.
        if (!_config.EnableScrim || _state != ScrimState.Idle)
        {
            _logger.LogWarning("Scrim recovery refused: scrims are disabled or one is already running ({State}).", _state);
            _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Recovery is only possible while no scrim is running."));
            return;
        }

        var data = await _recovery.LoadScrimStateAsync<ScrimRecoveryData>();
        if (data == null)
        {
            _logger.LogWarning("No scrim recovery data found.");
            _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] No recovery data found to restore."));
            return;
        }

        if (!Enum.IsDefined(data.State))
        {
            _logger.LogError("Scrim recovery contains invalid state value {State}.", (int)data.State);
            return;
        }

        if (data.State is not (ScrimState.KnifeRound or ScrimState.Live))
        {
            // After a restart nobody is connected, so the saved ready-ups, votes and picks are of players who may never
            // return and could never be completed. Those phases are quick to redo; practice re-applies its config.
            // The load above continued on a pool thread; the transition kills timers and clears the phase collections
            // the handlers read, so it starts on the game thread like every other one.
            if (data.State is not ScrimState.Idle)
            {
                var target = data.State == ScrimState.Practice ? ScrimState.Practice : ScrimState.Lobby;
                await RunOnGameThreadAsync(() => RunTransition(TransitionToAsync(target), $"restart recovered scrim {target}"));
            }
            _logger.LogInformation("Scrim recovery: {State} started again.", _state);
            return;
        }

        await RunOnGameThreadAsync(() =>
        {
            RestoreState(data);
            if (data.State is not (ScrimState.KnifeRound or ScrimState.Live)) return;
            // After a crash the server is usually on its default map: change to the scrim's, where HandleMapStart waits
            // for the players. Back on the scrim's map in warmup (a restart there), wait the same way. Otherwise the
            // knife round or match is still being played (the plugin was reloaded): leave teams and settings alone.
            if (!ScheduleSelectedMapChange() && GameRulesState.IsWarmup) HandleMapStart(Server.MapName);
        });
        _logger.LogInformation("Recovered scrim to state: {State}", data.State);
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Scrim successfully recovered to state: {data.State}."));
    }

    public void HandleMatchEnd()
    {
        if (_state != ScrimState.Live) return;

        _state = ScrimState.Idle;
        ClearPhaseState();
        RunTransition(_recovery.ClearScrimStateAsync(), "clear completed scrim recovery state");
    }

    // The wait after a map change and the side choice belong to the knife round and match. Game thread only: killing a
    // CounterStrikeSharp timer is a native call that is not guarded against other threads (it would race the engine, not
    // fail). Fields are cleared because the engine frees a one-shot timer once it fires, and killing that stale pointer
    // later could kill whichever timer was given the same address.
    private void ClearPhaseState()
    {
        _waitingForPlayers = false;
        _playerLoadTimer?.Kill();
        _playerLoadTimer = null;
        _sideSelectionTimer?.Kill();
        _sideSelectionTimer = null;
    }

    private async Task OnLobbyEntry()
    {
        _readyPlayers.Clear();
        _captains.Clear();
        _team1.Clear();
        _team2.Clear();
        _pickPool.Clear();
        _mapVotes.Clear();
        _playerVotes.Clear();
        _selectedMap = null;
        _knifeWinnerTeam = 0;
        await _configLoader.LoadAndExecuteConfigAsync("warmup.cfg");
        _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Lobby started! Type .ready to prepare."));
    }

    private void OnMapVoteEntry()
    {
        _mapVotes.Clear();
        _playerVotes.Clear();
        _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Map voting started! Type .vote <map>."));
    }

    private ulong ActiveCaptainId() =>
        _captains.GetValueOrDefault(_team1Picking ? 1 : 2);

    private bool StartPicking()
    {
        _pickPool.Clear();
        _team1.Clear();
        _team2.Clear();
        _pickPool.AddRange(_readyPlayers.Where(p => p.Value && !_captains.ContainsValue(p.Key)).Select(p => p.Key));

        if (_captains.TryGetValue(1, out var c1)) _team1.Add(c1);
        if (_captains.TryGetValue(2, out var c2)) _team2.Add(c2);

        _pickTurn = 0;
        _team1Picking = true;
        if (_pickPool.Count == 0) return false;

        _scheduler.Schedule(() =>
        {
            var captain1 = _captains.TryGetValue(1, out var cap1) ? Utilities.GetPlayerFromSteamId64(cap1) : null;
            Server.PrintToChatAll($" [Scrim] Picking phase started! Captain {captain1?.PlayerName ?? "Team 1"} picks first.");
            NotifyPickTurn();
        });
        return true;
    }

    private void NotifyPickTurn()
    {
        var captainSteamId = ActiveCaptainId();
        if (captainSteamId == 0) return;

        _scheduler.Schedule(() =>
        {
            var captain = Utilities.GetPlayerFromSteamId64(captainSteamId);
            captain?.PrintToChat(" [Scrim] It's YOUR turn to pick a player (.pick <SteamID64>).");

            string pool = string.Join(", ", _pickPool.Select(id => Utilities.GetPlayerFromSteamId64(id)?.PlayerName ?? id.ToString()));
            captain?.PrintToChat($" [Scrim] Available players: {pool}");
        });
    }

    private async Task OnKnifeRoundEntry()
    {
        MovePlayersToTeams();
        if (ScheduleSelectedMapChange()) return;

        await _configLoader.LoadAndExecuteConfigAsync(_config.Scrim.KnifeConfigPath);
        _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Knife round started!"));
    }

    private async Task OnLiveEntry()
    {
        MovePlayersToTeams();
        if (ScheduleSelectedMapChange()) return;

        await _configLoader.LoadAndExecuteConfigAsync(_config.Scrim.LiveConfigPath);
        _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Match is live!"));
    }

    private void MovePlayersToTeams()
    {
        _scheduler.Schedule(() =>
        {
            foreach (var steamId in _team1)
            {
                var player = Utilities.GetPlayerFromSteamId64(steamId);
                player?.SwitchTeam(CsTeam.Terrorist);
            }
            foreach (var steamId in _team2)
            {
                var player = Utilities.GetPlayerFromSteamId64(steamId);
                player?.SwitchTeam(CsTeam.CounterTerrorist);
            }
        });
    }

    private bool ScheduleSelectedMapChange()
    {
        if (string.IsNullOrWhiteSpace(_selectedMap) ||
            string.Equals(Server.MapName, _selectedMap, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _scheduler.Schedule(() => Server.ExecuteCommand($"changelevel {_selectedMap}"));
        return true;
    }

    public async Task StartScrimAsync()
    {
        if (_state != ScrimState.Idle) return;
        await TransitionToAsync(ScrimState.Lobby);
    }

    public async Task StopScrimAsync()
    {
        await TransitionToAsync(ScrimState.Idle);
        await _recovery.ClearScrimStateAsync();
        // After the awaits this runs on a thread-pool thread; chat must be sent from the game thread.
        _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Scrim stopped by admin."));
    }

    public async Task SetReadyAsync(ulong steamId, bool ready)
    {
        if (_state != ScrimState.Lobby) return;

        if (!_configLoader.IsPlayerWhitelisted(steamId))
        {
            _scheduler.Schedule(() =>
            {
                var player = Utilities.GetPlayerFromSteamId64(steamId);
                player?.PrintToChat(" [Scrim] You are not whitelisted for this scrim.");
            });
            return;
        }

        if (ready && !_readyPlayers.GetValueOrDefault(steamId) &&
            _readyPlayers.Values.Count(value => value) >= _playersPerTeam * 2)
        {
            _scheduler.Schedule(() => Utilities.GetPlayerFromSteamId64(steamId)?
                .PrintToChat(" [Scrim] The player slots are full."));
            return;
        }

        _readyPlayers[steamId] = ready;
        var readyCount = _readyPlayers.Values.Count(v => v);
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Player {(ready ? "ready" : "unready")}. ({readyCount}/{_minReadyPlayers})"));

        if (readyCount >= _minReadyPlayers)
        {
            await TransitionToAsync(ScrimState.CaptainSetup);
        }
        else
        {
            await SaveCurrentStateAsync();
        }
    }

    public async Task SetCaptainAsync(int team, ulong steamId)
    {
        if (_state != ScrimState.CaptainSetup) return;
        if (team != 1 && team != 2) return;
        if (!_readyPlayers.GetValueOrDefault(steamId) || _captains.Values.Contains(steamId)) return;

        _captains[team] = steamId;
        _scheduler.Schedule(() =>
        {
            var player = Utilities.GetPlayerFromSteamId64(steamId);
            Server.PrintToChatAll($" [Scrim] {player?.PlayerName} appointed as Captain for Team {team}.");
        });

        if (_captains.Count == 2)
        {
            await TransitionToAsync(ScrimState.MapVote);
        }
        else
        {
            await SaveCurrentStateAsync();
        }
    }

    public async Task VoteMapAsync(ulong steamId, string mapName)
    {
        if (_state != ScrimState.MapVote) return;
        if (!_readyPlayers.GetValueOrDefault(steamId)) return;

        var normalizedMap = mapName.ToLowerInvariant();
        if (!_config.Scrim.MapPool.Contains(normalizedMap))
        {
            _scheduler.Schedule(() =>
            {
                var player = Utilities.GetPlayerFromSteamId64(steamId);
                player?.PrintToChat($" [Scrim] Invalid map. Pool: {string.Join(", ", _config.Scrim.MapPool)}");
            });
            return;
        }

        if (_playerVotes.TryGetValue(steamId, out var oldVote))
        {
            _mapVotes[oldVote]--;
        }

        _playerVotes[steamId] = normalizedMap;
        if (!_mapVotes.ContainsKey(normalizedMap)) _mapVotes[normalizedMap] = 0;
        _mapVotes[normalizedMap]++;

        if (_playerVotes.Count >= _readyPlayers.Count(p => p.Value))
        {
            _selectedMap = _mapVotes.OrderByDescending(x => x.Value).First().Key;
            _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Map selected: {_selectedMap}"));
            await TransitionToAsync(ScrimState.Picking);
        }
        else
        {
            await SaveCurrentStateAsync();
        }
    }

    public async Task PickPlayerAsync(ulong steamId, ulong targetSteamId)
    {
        if (_state != ScrimState.Picking) return;

        if (steamId != ActiveCaptainId()) return;
        if (!_pickPool.Contains(targetSteamId)) return;

        _pickPool.Remove(targetSteamId);
        var pickedByTeam1 = _team1Picking;
        if (pickedByTeam1) _team1.Add(targetSteamId);
        else _team2.Add(targetSteamId);

        _scheduler.Schedule(() =>
        {
            var player = Utilities.GetPlayerFromSteamId64(targetSteamId);
            Server.PrintToChatAll($" [Scrim] Captain {(pickedByTeam1 ? "1" : "2")} picked {player?.PlayerName}.");
        });

        if (_pickPool.Count == 0)
        {
            await TransitionToAsync(_config.Scrim.KnifeRoundEnabled ? ScrimState.KnifeRound : ScrimState.Live);
            return;
        }

        _pickTurn++;
        if (_pickTurn == 1 || _pickTurn == 3)
        {
            _team1Picking = !_team1Picking;
        }
        if (_pickTurn == 4) _pickTurn = 0;

        await SaveCurrentStateAsync();
        NotifyPickTurn();
    }

    public Task HandleKnifeRoundEnd(int winnerTeam)
    {
        if (_state != ScrimState.KnifeRound || _waitingForPlayers) return Task.CompletedTask;
        if (winnerTeam is not (2 or 3)) return Task.CompletedTask;
        // knife.cfg keeps playing knife rounds while the winner chooses; a later one must not change the winner.
        if (_knifeWinnerTeam is 2 or 3) return Task.CompletedTask;

        _knifeWinnerTeam = winnerTeam;
        RunTransition(SaveCurrentStateAsync(), "save knife-round result");
        var winnerName = winnerTeam == 2 ? "Terrorists" : "Counter-Terrorists";

        _scheduler.Schedule(() =>
        {
            Server.PrintToChatAll($" [Scrim] {winnerName} won the knife round!");

            var winningCaptainId = winnerTeam == 2 ? _team1.FirstOrDefault() : _team2.FirstOrDefault();
            if (winningCaptainId == 0)
            {
                RunTransition(TransitionToAsync(ScrimState.Live), "start live match after knife round");
                return;
            }

            var captain = Utilities.GetPlayerFromSteamId64(winningCaptainId);
            captain?.PrintToChat(" [Scrim] You won! Type .ct or .t to choose your side. (30s timeout)");

            _sideSelectionTimer?.Kill();
            _sideSelectionTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(30.0f, () =>
            {
                _sideSelectionTimer = null;
                _logger.LogInformation("Side selection timed out. Auto-assigning sides.");
                _scheduler.Schedule(() =>
                {
                    // The scrim may have been aborted, a side chosen, or the map changed since the timer started.
                    if (_state != ScrimState.KnifeRound || _waitingForPlayers) return;
                    Server.PrintToChatAll(" [Scrim] Side selection timed out. Proceeding with current sides.");
                    RunTransition(TransitionToAsync(ScrimState.Live), "start live match after side-selection timeout");
                });
            });
        });

        return Task.CompletedTask;
    }

    public Task SelectSideAsync(ulong steamId, string side)
    {
        // During the wait after a map change the knife round is about to be played again.
        if (_state != ScrimState.KnifeRound || _waitingForPlayers) return Task.CompletedTask;
        // Until the knife round has a winner there is no side choice (otherwise captain 2 could skip it).
        if (_knifeWinnerTeam is not (2 or 3)) return Task.CompletedTask;

        var winningCaptainId = _knifeWinnerTeam == 2 ? _team1.FirstOrDefault() : _team2.FirstOrDefault();
        if (steamId != winningCaptainId) return Task.CompletedTask;

        _scheduler.Schedule(() =>
        {
            // A second queued choice (or the timeout) must not swap again.
            if (_state != ScrimState.KnifeRound) return;
            _sideSelectionTimer?.Kill();
            _sideSelectionTimer = null;

            bool stay = (string.Equals(side, "t", StringComparison.OrdinalIgnoreCase) && _knifeWinnerTeam == 2) ||
                (string.Equals(side, "ct", StringComparison.OrdinalIgnoreCase) && _knifeWinnerTeam == 3);

            if (!stay)
            {
                var temp = new List<ulong>(_team1);
                _team1.Clear();
                _team1.AddRange(_team2);
                _team2.Clear();
                _team2.AddRange(temp);

                if (_captains.TryGetValue(1, out var c1) && _captains.TryGetValue(2, out var c2))
                {
                    _captains[1] = c2;
                    _captains[2] = c1;
                }
                // No mp_swapteams: entering Live moves players to the swapped lists and restarts the match
                // itself, and a second swap from mp_swapteams (applied at its own restart) could undo that.
            }

            Server.PrintToChatAll($" [Scrim] Side selected: {side.ToUpperInvariant()}. Match starting...");
            RunTransition(TransitionToAsync(ScrimState.Live), "start live match after side selection");
        });

        return Task.CompletedTask;
    }

    public async Task SetPracticeModeAsync(bool enabled)
    {
        if (enabled && _state == ScrimState.Idle)
            await TransitionToAsync(ScrimState.Practice);
        else if (!enabled && _state == ScrimState.Practice)
            await TransitionToAsync(ScrimState.Idle);
    }

    public void HandleDisconnect(ulong steamId)
    {
        if (_readyPlayers.TryGetValue(steamId, out var ready) && ready)
        {
            _readyPlayers[steamId] = false;
            if (_state == ScrimState.Lobby)
            {
                _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Player disconnected. ({_readyPlayers.Values.Count(v => v)}/{_minReadyPlayers})"));
                RunTransition(SaveCurrentStateAsync(), "save disconnected player state");
            }
            else if (_state != ScrimState.Idle && _state != ScrimState.Live)
            {
                _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Critical player disconnected. Scrim aborted."));
                RunTransition(TransitionToAsync(ScrimState.Idle), "abort scrim after disconnect");
            }
        }
    }

    public void SetOverride(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "playersperteam":
                if (int.TryParse(value, out var p) && p is >= 1 and <= 32 && _minReadyPlayers <= p * 2) _playersPerTeam = p;
                break;
            case "minready":
                if (int.TryParse(value, out var m) && m is >= 2 and <= 64 && m <= _playersPerTeam * 2) _minReadyPlayers = m;
                break;
        }
        RunTransition(SaveCurrentStateAsync(), "save scrim override");
    }

    public void HandleMapStart(string mapName)
    {
        if (_state is not (ScrimState.KnifeRound or ScrimState.Live)) return;

        // Nobody is connected yet when the map starts: applying the knife/live config now would start the round
        // without the players and leave everyone on whatever team they pick. Hold warmup until they are back.
        _waitingForPlayers = true;
        RunTransition(_configLoader.LoadAndExecuteConfigAsync("warmup.cfg"), $"hold warmup on {mapName}");
        _playerLoadTimer?.Kill();
        _playerLoadTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(PlayerLoadTimeoutSeconds, () =>
        {
            _playerLoadTimer = null;
            StartAfterMapChange("timed out waiting for players");
        });
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Waiting for all players to load {mapName}..."));
    }

    public void HandlePlayerConnected(ulong steamId)
    {
        if (_state is not (ScrimState.KnifeRound or ScrimState.Live)) return;

        if (_waitingForPlayers)
        {
            if (_team1.Concat(_team2).All(id => id == steamId || Utilities.GetPlayerFromSteamId64(id) is { IsValid: true }))
                StartAfterMapChange("all players loaded");
            return;
        }

        _scheduler.Schedule(() => MoveToTeamSide(steamId));
    }

    // Picked players stay on their team during the knife round and the match (jointeam: 1 spectators, 2 T, 3 CT).
    public bool CanJoinTeam(ulong steamId, int team) =>
        _state is not (ScrimState.KnifeRound or ScrimState.Live) || _waitingForPlayers ||
        RosterSide(steamId) is not { } side || side == team;

    private void StartAfterMapChange(string reason)
    {
        if (!_waitingForPlayers) return;
        _waitingForPlayers = false;
        _playerLoadTimer?.Kill();
        _playerLoadTimer = null;
        _logger.LogInformation("Starting {State} after the map change: {Reason}.", _state, reason);
        RunTransition(ApplyCurrentStateAsync(), $"apply {_state} configuration after the map change");
    }

    // A picked player who rejoins goes back to their team.
    private void MoveToTeamSide(ulong steamId)
    {
        var player = Utilities.GetPlayerFromSteamId64(steamId);
        if (RosterSide(steamId) is not { } side || player is not { IsValid: true } || player.TeamNum == side) return;

        player.SwitchTeam((CsTeam)side);
        _logger.LogInformation("Moved rejoining scrim player {SteamId} back to team {Team}.", steamId, side);
    }

    // The side a picked player's team is on now. Sides swap at halftime, so this follows a teammate who is in the game
    // rather than the starting side; null when none is.
    private byte? RosterSide(ulong steamId)
    {
        var team = _team1.Contains(steamId) ? _team1 : _team2.Contains(steamId) ? _team2 : null;
        return team?.Where(id => id != steamId)
            .Select(Utilities.GetPlayerFromSteamId64)
            .FirstOrDefault(teammate => teammate is { IsValid: true, TeamNum: 2 or 3 })?.TeamNum;
    }

    private async Task ApplyCurrentStateAsync()
    {
        if (_state is not (ScrimState.KnifeRound or ScrimState.Live)) return;

        MovePlayersToTeams();
        if (_state == ScrimState.KnifeRound)
        {
            // The knife round is played again, so it decides the side choice again (an old timeout must not end it).
            _knifeWinnerTeam = 0;
            _sideSelectionTimer?.Kill();
            _sideSelectionTimer = null;
            await _configLoader.LoadAndExecuteConfigAsync(_config.Scrim.KnifeConfigPath);
        }
        else if (_state == ScrimState.Live)
        {
            await _configLoader.LoadAndExecuteConfigAsync(_config.Scrim.LiveConfigPath);
        }
    }

    private void RunTransition(Task task, string operation) => _ = ObserveTransitionAsync(task, operation);

    private async Task ObserveTransitionAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Operation}", operation);
        }
    }

    private void RestoreState(ScrimRecoveryData data)
    {
        _readyPlayers.Clear();
        foreach (var kvp in data.ReadyPlayers ?? []) _readyPlayers[kvp.Key] = kvp.Value;
        _captains.Clear();
        foreach (var kvp in data.Captains ?? []) _captains[kvp.Key] = kvp.Value;
        _team1.Clear();
        if (data.Team1 != null) _team1.AddRange(data.Team1);
        _team2.Clear();
        if (data.Team2 != null) _team2.AddRange(data.Team2);
        _mapVotes.Clear();
        foreach (var kvp in data.MapVotes ?? []) _mapVotes[kvp.Key] = kvp.Value;
        _playerVotes.Clear();
        foreach (var kvp in data.PlayerVotes ?? []) _playerVotes[kvp.Key] = kvp.Value;
        _pickPool.Clear();
        if (data.PickPool != null) _pickPool.AddRange(data.PickPool);
        // The file is trusted as far as the map pool: the name goes into a changelevel command.
        _selectedMap = data.SelectedMap is { } map && _config.Scrim.MapPool.Contains(map) ? map : null;
        _team1Picking = data.Team1Picking;
        _pickTurn = data.PickTurn;
        _knifeWinnerTeam = data.KnifeWinnerTeam;
        _state = data.State;
    }

    private Task RunOnGameThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.Schedule(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    // Plugin.Unload calls this on the game thread; the container's later call on a pool thread then finds nothing to kill.
    public void Dispose() => ClearPhaseState();
}
