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
    private CounterStrikeSharp.API.Modules.Timers.Timer? _sideSelectionTimer;

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
                StartPicking();
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

        await RunOnGameThreadAsync(() => RestoreState(data));
        _logger.LogInformation("Recovered scrim to state: {State}", data.State);
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Scrim successfully recovered to state: {data.State}."));
        await ApplyCurrentStateAsync();
    }

    public void HandleMatchEnd()
    {
        if (_state != ScrimState.Live) return;

        _state = ScrimState.Idle;
        _sideSelectionTimer?.Kill();
        _sideSelectionTimer = null;
        RunTransition(_recovery.ClearScrimStateAsync(), "clear completed scrim recovery state");
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
        _sideSelectionTimer?.Kill();
        _sideSelectionTimer = null;
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

    private void StartPicking()
    {
        _pickPool.Clear();
        _team1.Clear();
        _team2.Clear();
        _pickPool.AddRange(_readyPlayers.Where(p => p.Value && !_captains.ContainsValue(p.Key)).Select(p => p.Key));

        if (_captains.TryGetValue(1, out var c1)) _team1.Add(c1);
        if (_captains.TryGetValue(2, out var c2)) _team2.Add(c2);

        _pickTurn = 0;
        _team1Picking = true;

        _scheduler.Schedule(() =>
        {
            var captain1 = _captains.TryGetValue(1, out var cap1) ? Utilities.GetPlayerFromSteamId(cap1) : null;
            Server.PrintToChatAll($" [Scrim] Picking phase started! Captain {captain1?.PlayerName ?? "Team 1"} picks first.");
            NotifyPickTurn();
        });
    }

    private void NotifyPickTurn()
    {
        var captainSteamId = ActiveCaptainId();
        if (captainSteamId == 0) return;

        _scheduler.Schedule(() =>
        {
            var captain = Utilities.GetPlayerFromSteamId(captainSteamId);
            captain?.PrintToChat(" [Scrim] It's YOUR turn to pick a player (.pick <SteamID64>).");

            string pool = string.Join(", ", _pickPool.Select(id => Utilities.GetPlayerFromSteamId(id)?.PlayerName ?? id.ToString()));
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
                var player = Utilities.GetPlayerFromSteamId(steamId);
                player?.SwitchTeam(CsTeam.Terrorist);
            }
            foreach (var steamId in _team2)
            {
                var player = Utilities.GetPlayerFromSteamId(steamId);
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
        _sideSelectionTimer?.Kill();
        _sideSelectionTimer = null;
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
                var player = Utilities.GetPlayerFromSteamId(steamId);
                player?.PrintToChat(" [Scrim] You are not whitelisted for this scrim.");
            });
            return;
        }

        if (ready && !_readyPlayers.GetValueOrDefault(steamId) &&
            _readyPlayers.Values.Count(value => value) >= _playersPerTeam * 2)
        {
            _scheduler.Schedule(() => Utilities.GetPlayerFromSteamId(steamId)?
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
            var player = Utilities.GetPlayerFromSteamId(steamId);
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
                var player = Utilities.GetPlayerFromSteamId(steamId);
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
            var player = Utilities.GetPlayerFromSteamId(targetSteamId);
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
        if (_state != ScrimState.KnifeRound) return Task.CompletedTask;
        if (winnerTeam is not (2 or 3)) return Task.CompletedTask;

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

            var captain = Utilities.GetPlayerFromSteamId(winningCaptainId);
            captain?.PrintToChat(" [Scrim] You won! Type .ct or .t to choose your side. (30s timeout)");

            _sideSelectionTimer?.Kill();
            _sideSelectionTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(30.0f, () =>
            {
                _logger.LogInformation("Side selection timed out. Auto-assigning sides.");
                _scheduler.Schedule(() =>
                {
                    // The scrim may have been aborted or a side chosen since the timer started.
                    if (_state != ScrimState.KnifeRound) return;
                    Server.PrintToChatAll(" [Scrim] Side selection timed out. Proceeding with current sides.");
                    RunTransition(TransitionToAsync(ScrimState.Live), "start live match after side-selection timeout");
                });
            });
        });

        return Task.CompletedTask;
    }

    public Task SelectSideAsync(ulong steamId, string side)
    {
        if (_state != ScrimState.KnifeRound) return Task.CompletedTask;
        // Until the knife round has a winner there is no side choice (otherwise captain 2 could skip it).
        if (_knifeWinnerTeam is not (2 or 3)) return Task.CompletedTask;

        var winningCaptainId = _knifeWinnerTeam == 2 ? _team1.FirstOrDefault() : _team2.FirstOrDefault();
        if (steamId != winningCaptainId) return Task.CompletedTask;

        _scheduler.Schedule(() =>
        {
            // A second queued choice (or the timeout) must not swap again.
            if (_state != ScrimState.KnifeRound) return;
            _sideSelectionTimer?.Kill();

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
        if (_state is ScrimState.KnifeRound or ScrimState.Live)
        {
            RunTransition(ApplyCurrentStateAsync(), $"apply {_state} configuration on {mapName}");
        }
    }

    private async Task ApplyCurrentStateAsync()
    {
        if (_state is not (ScrimState.KnifeRound or ScrimState.Live)) return;

        MovePlayersToTeams();
        if (_state == ScrimState.KnifeRound)
        {
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
        _selectedMap = data.SelectedMap;
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

    public void Dispose() => _sideSelectionTimer?.Kill();
}
