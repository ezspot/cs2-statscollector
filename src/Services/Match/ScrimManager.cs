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

public sealed class ScrimManager : IScrimManager
{
    private readonly ILogger<ScrimManager> _logger;
    private readonly IOptionsMonitor<PluginConfig> _configMonitor;
    private readonly IConfigLoaderService _configLoader;
    private readonly IMatchTrackingService _matchTracker;
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

    private ScrimState _state = ScrimState.Idle;

    public ScrimState CurrentState => _state;

    public ScrimManager(
        ILogger<ScrimManager> logger,
        IOptionsMonitor<PluginConfig> configMonitor,
        IConfigLoaderService configLoader,
        IMatchTrackingService matchTracker,
        IJsonRecoveryService recovery,
        IGameScheduler scheduler)
    {
        _logger = logger;
        _configMonitor = configMonitor;
        _configLoader = configLoader;
        _matchTracker = matchTracker;
        _recovery = recovery;
        _scheduler = scheduler;

        _playersPerTeam = _config.Scrim.PlayersPerTeam;
        _minReadyPlayers = _config.Scrim.MinReadyPlayers;
    }

    // Single point of state change: runs exit/entry side effects and persists recovery state.
    private async Task TransitionToAsync(ScrimState target)
    {
        var from = _state;
        if (from == target) return;

        // Exit actions
        if (from == ScrimState.Practice)
        {
            await _configLoader.LoadAndExecuteConfigAsync("live.cfg");
        }

        _state = target;
        _logger.LogInformation("Scrim state transition: {From} -> {To}", from, target);

        // Entry actions
        switch (target)
        {
            case ScrimState.Lobby:
                await OnLobbyEntry();
                break;
            case ScrimState.Veto:
                _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Map Veto started!"));
                break;
            case ScrimState.Practice:
                await _configLoader.LoadAndExecuteConfigAsync("practice.cfg");
                _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Practice Mode enabled! God, noclip, and infinite ammo active."));
                break;
            case ScrimState.CaptainSetup:
                _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Ready players confirmed. Admins, please appoint captains (.scrim setcaptain team1/team2 <name>)."));
                break;
            case ScrimState.MapVote:
                OnMapVoteEntry();
                break;
            case ScrimState.Picking:
                StartPicking();
                break;
            case ScrimState.Live:
                await OnLiveEntry();
                break;
        }

        _scheduler.Schedule(() => _ = SaveCurrentStateAsync());
    }

    private async Task SaveCurrentStateAsync()
    {
        var data = new ScrimRecoveryData(
            CurrentState,
            _matchTracker.CurrentMatch?.MatchId,
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
            KnifeWinnerTeam: _knifeWinnerTeam
        );
        await _recovery.SaveScrimStateAsync(data);
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

        // Validate match status in DB before restoring.
        if (data.MatchId.HasValue)
        {
            var status = await _matchTracker.GetMatchStatusAsync(data.MatchId.Value);
            if (status is "COMPLETED" or "CANCELLED")
            {
                _logger.LogWarning("Recovery aborted: Match {MatchId} is already {Status}", data.MatchId, status);
                _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Recovery aborted: Match {data.MatchId} is already {status}."));
                await _recovery.ClearScrimStateAsync();
                return;
            }
        }

        // Restore in-memory state.
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

        // Set the state directly; entry side effects are intentionally not re-run on recovery.
        _state = data.State;
        _logger.LogInformation("Recovered scrim to state: {State}", data.State);
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Scrim successfully recovered to state: {data.State}."));
    }

    private async Task OnLobbyEntry()
    {
        _readyPlayers.Clear();
        _captains.Clear();
        _team1.Clear();
        _team2.Clear();
        _pickPool.Clear();
        await _configLoader.LoadAndExecuteConfigAsync("config.cfg");
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
            captain?.PrintToChat(" [Scrim] It's YOUR turn to pick a player (.pick <name/index>).");

            string pool = string.Join(", ", _pickPool.Select(id => Utilities.GetPlayerFromSteamId(id)?.PlayerName ?? id.ToString()));
            captain?.PrintToChat($" [Scrim] Available players: {pool}");
        });
    }

    private async Task OnLiveEntry()
    {
        // Move players to their respective teams.
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

        if (_config.Scrim.KnifeRoundEnabled)
        {
            await _configLoader.LoadAndExecuteConfigAsync(_config.Scrim.KnifeConfigPath);
            _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Knife round started!"));
        }
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
        Server.PrintToChatAll(" [Scrim] Scrim stopped by admin.");
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

        _readyPlayers[steamId] = ready;
        var readyCount = _readyPlayers.Values.Count(v => v);
        _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Player {(ready ? "ready" : "unready")}. ({readyCount}/{_minReadyPlayers})"));

        if (readyCount >= _minReadyPlayers)
        {
            await TransitionToAsync(ScrimState.CaptainSetup);
        }
    }

    public async Task SetCaptainAsync(int team, ulong steamId)
    {
        if (_state != ScrimState.CaptainSetup) return;
        if (team != 1 && team != 2) return;

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
    }

    public async Task VoteMapAsync(ulong steamId, string mapName)
    {
        if (_state != ScrimState.MapVote) return;

        var normalizedMap = mapName.ToLower();
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
    }

    public async Task PickPlayerAsync(ulong steamId, ulong targetSteamId)
    {
        if (_state != ScrimState.Picking) return;

        if (steamId != ActiveCaptainId()) return;
        if (!_pickPool.Contains(targetSteamId)) return;

        _pickPool.Remove(targetSteamId);
        if (_team1Picking) _team1.Add(targetSteamId);
        else _team2.Add(targetSteamId);

        _scheduler.Schedule(() =>
        {
            var player = Utilities.GetPlayerFromSteamId(targetSteamId);
            Server.PrintToChatAll($" [Scrim] Captain {(_team1Picking ? "1" : "2")} picked {player?.PlayerName}.");
        });

        if (_pickPool.Count == 0)
        {
            await TransitionToAsync(ScrimState.KnifeRound);
            return;
        }

        _pickTurn++;
        if (_pickTurn == 1 || _pickTurn == 3)
        {
            _team1Picking = !_team1Picking;
        }
        if (_pickTurn == 4) _pickTurn = 0;

        NotifyPickTurn();
    }

    public Task HandleKnifeRoundEnd(int winnerTeam)
    {
        if (_state != ScrimState.KnifeRound) return Task.CompletedTask;

        _knifeWinnerTeam = winnerTeam;
        var winnerName = winnerTeam == 2 ? "Terrorists" : "Counter-Terrorists";

        _scheduler.Schedule(async () =>
        {
            Server.PrintToChatAll($" [Scrim] {winnerName} won the knife round!");

            var winningCaptainId = winnerTeam == 2 ? _team1.FirstOrDefault() : _team2.FirstOrDefault();
            if (winningCaptainId == 0)
            {
                await TransitionToAsync(ScrimState.Live);
                return;
            }

            var captain = Utilities.GetPlayerFromSteamId(winningCaptainId);
            captain?.PrintToChat(" [Scrim] You won! Type .ct or .t to choose your side. (30s timeout)");

            _sideSelectionTimer?.Kill();
            _sideSelectionTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(30.0f, () =>
            {
                _logger.LogInformation("Side selection timed out. Auto-assigning sides.");
                _scheduler.Schedule(async () =>
                {
                    Server.PrintToChatAll(" [Scrim] Side selection timed out. Proceeding with current sides.");
                    await TransitionToAsync(ScrimState.Live);
                });
            });
        });

        return Task.CompletedTask;
    }

    public Task SelectSideAsync(ulong steamId, string side)
    {
        if (_state != ScrimState.KnifeRound) return Task.CompletedTask;

        var winningCaptainId = _knifeWinnerTeam == 2 ? _team1.FirstOrDefault() : _team2.FirstOrDefault();
        if (steamId != winningCaptainId) return Task.CompletedTask;

        _scheduler.Schedule(async () =>
        {
            _sideSelectionTimer?.Kill();

            bool stay = (side.ToLower() == "t" && _knifeWinnerTeam == 2) || (side.ToLower() == "ct" && _knifeWinnerTeam == 3);

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

                Server.ExecuteCommand("mp_swapteams");
            }

            Server.PrintToChatAll($" [Scrim] Side selected: {side.ToUpper()}. Match starting...");
            await TransitionToAsync(ScrimState.Live);
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

    public async Task StartVetoAsync()
    {
        if (_state == ScrimState.Lobby)
            await TransitionToAsync(ScrimState.Veto);
    }

    public void HandleDisconnect(ulong steamId)
    {
        if (_readyPlayers.TryGetValue(steamId, out var ready) && ready)
        {
            _readyPlayers[steamId] = false;
            if (_state == ScrimState.Lobby)
            {
                _scheduler.Schedule(() => Server.PrintToChatAll($" [Scrim] Player disconnected. ({_readyPlayers.Values.Count(v => v)}/{_minReadyPlayers})"));
            }
            else if (_state != ScrimState.Idle && _state != ScrimState.Live)
            {
                _scheduler.Schedule(() => Server.PrintToChatAll(" [Scrim] Critical player disconnected. Scrim aborted."));
                _ = TransitionToAsync(ScrimState.Idle);
            }
        }
    }

    public void SetOverride(string key, string value)
    {
        switch (key.ToLower())
        {
            case "playersperteam":
                if (int.TryParse(value, out var p)) _playersPerTeam = p;
                break;
            case "minready":
                if (int.TryParse(value, out var m)) _minReadyPlayers = m;
                break;
        }
    }
}
