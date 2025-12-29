using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stateless;
using statsCollector.Config;
using statsCollector.Domain;

namespace statsCollector.Services;

public class ScrimManager : IScrimManager
{
    private readonly ILogger<ScrimManager> _logger;
    private readonly PluginConfig _config;
    private readonly IConfigLoaderService _configLoader;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IScrimPersistenceService _persistence;
    private readonly StateMachine<ScrimState, ScrimTrigger> _machine;

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

    private enum ScrimTrigger
    {
        StartLobby,
        StopScrim,
        AllReady,
        CaptainsAssigned,
        MapSelected,
        PickingFinished,
        KnifeRoundFinished,
        MatchStarted,
        MatchFinished,
        RecoverState
    }

    public ScrimState CurrentState => _machine.State;

    public ScrimManager(
        ILogger<ScrimManager> logger,
        IOptions<PluginConfig> config,
        IConfigLoaderService configLoader,
        IMatchTrackingService matchTracker,
        IScrimPersistenceService persistence)
    {
        _logger = logger;
        _config = config.Value;
        _configLoader = configLoader;
        _matchTracker = matchTracker;
        _persistence = persistence;

        _playersPerTeam = _config.Scrim.PlayersPerTeam;
        _minReadyPlayers = _config.Scrim.MinReadyPlayers;

        _machine = new StateMachine<ScrimState, ScrimTrigger>(ScrimState.Idle);
        ConfigureStateMachine();
    }

    private void ConfigureStateMachine()
    {
        _machine.OnTransitioned(transition => 
        {
            _logger.LogInformation("Scrim State Transition: {From} -> {To} via {Trigger}", 
                transition.Source, transition.Destination, transition.Trigger);
            _ = SaveCurrentStateAsync();
        });

        _machine.Configure(ScrimState.Idle)
            .Permit(ScrimTrigger.StartLobby, ScrimState.Lobby)
            .Permit(ScrimTrigger.RecoverState, ScrimState.Lobby); // Simple recovery entry

        _machine.Configure(ScrimState.Lobby)
            .OnEntryAsync(async () => await OnLobbyEntry())
            .Permit(ScrimTrigger.AllReady, ScrimState.CaptainSetup)
            .Permit(ScrimTrigger.StopScrim, ScrimState.Idle);

        _machine.Configure(ScrimState.CaptainSetup)
            .OnEntry(() => Server.PrintToChatAll(" [Scrim] Ready players confirmed. Admins, please appoint captains (.scrim setcaptain team1/team2 <name>)."))
            .Permit(ScrimTrigger.CaptainsAssigned, ScrimState.MapVote)
            .Permit(ScrimTrigger.StopScrim, ScrimState.Idle);

        _machine.Configure(ScrimState.MapVote)
            .OnEntryAsync(async () => await OnMapVoteEntry())
            .Permit(ScrimTrigger.MapSelected, ScrimState.Picking)
            .Permit(ScrimTrigger.StopScrim, ScrimState.Idle);

        _machine.Configure(ScrimState.Picking)
            .OnEntry(() => StartPicking())
            .Permit(ScrimTrigger.PickingFinished, ScrimState.KnifeRound)
            .Permit(ScrimTrigger.StopScrim, ScrimState.Idle);

        _machine.Configure(ScrimState.KnifeRound)
            .Permit(ScrimTrigger.KnifeRoundFinished, ScrimState.Live)
            .Permit(ScrimTrigger.StopScrim, ScrimState.Idle);

        _machine.Configure(ScrimState.Live)
            .OnEntryAsync(async () => await OnLiveEntry())
            .Permit(ScrimTrigger.MatchFinished, ScrimState.Idle)
            .Permit(ScrimTrigger.StopScrim, ScrimState.Idle);
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
            DateTime.UtcNow
        );
        await _persistence.SaveStateAsync(data);
    }

    public async Task RecoverAsync()
    {
        var data = await _persistence.LoadStateAsync();
        if (data == null)
        {
            _logger.LogWarning("No scrim recovery data found.");
            Server.PrintToChatAll(" [Scrim] No recovery data found to restore.");
            return;
        }

        _logger.LogInformation("Recovering scrim from state: {State} (MatchID: {MatchId})", data.State, data.MatchId);
        
        // Restore rosters and captains
        _captains.Clear();
        foreach (var kvp in data.Captains) _captains[kvp.Key] = kvp.Value;
        
        _team1.Clear(); _team1.AddRange(data.Team1);
        _team2.Clear(); _team2.AddRange(data.Team2);
        _selectedMap = data.SelectedMap;
        
        if (data.MatchId.HasValue && _matchTracker.CurrentMatch == null)
        {
            _logger.LogInformation("Restoring match tracking for MatchID: {MatchId}", data.MatchId);
            // Internal match tracker recovery would go here if implemented
        }

        // Use FireAsync to transition state machine safely
        await _machine.FireAsync(ScrimTrigger.RecoverState);
        Server.PrintToChatAll($" [Scrim] Scrim recovered to state: {data.State}.");
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
        Server.PrintToChatAll(" [Scrim] Lobby started! Type .ready to prepare.");
    }

    private async Task OnMapVoteEntry()
    {
        _mapVotes.Clear();
        _playerVotes.Clear();
        Server.PrintToChatAll(" [Scrim] Map voting started! Type .vote <map>.");
    }

    private void StartPicking()
    {
        _pickPool.Clear();
        _pickPool.AddRange(_readyPlayers.Where(p => p.Value && !_captains.ContainsValue(p.Key)).Select(p => p.Key));
        
        if (_captains.TryGetValue(1, out var c1)) _team1.Add(c1);
        if (_captains.TryGetValue(2, out var c2)) _team2.Add(c2);

        _pickTurn = 0;
        _team1Picking = true;

        var captain1 = Utilities.GetPlayerFromSteamId(_captains[1]);
        Server.PrintToChatAll($" [Scrim] Picking phase started! Captain {captain1?.PlayerName} (Team 1) picks first.");
        NotifyPickTurn();
    }

    private void NotifyPickTurn()
    {
        var captainSteamId = _team1Picking ? _captains[1] : _captains[2];
        var captain = Utilities.GetPlayerFromSteamId(captainSteamId);
        captain?.PrintToChat(" [Scrim] It's YOUR turn to pick a player (.pick <name/index>).");
        
        string pool = string.Join(", ", _pickPool.Select(id => Utilities.GetPlayerFromSteamId(id)?.PlayerName ?? id.ToString()));
        captain?.PrintToChat($" [Scrim] Available players: {pool}");
    }

    private async Task OnLiveEntry()
    {
        // Move players to their respective teams
        foreach (var steamId in _team1)
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player != null) player.SwitchTeam(CsTeam.Terrorist); 
        }
        foreach (var steamId in _team2)
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player != null) player.SwitchTeam(CsTeam.CounterTerrorist);
        }

        if (_config.Scrim.KnifeRoundEnabled)
        {
            await _configLoader.LoadAndExecuteConfigAsync("knife.cfg");
            Server.PrintToChatAll(" [Scrim] Knife round started!");
        }
        else
        {
            var liveCfg = _playersPerTeam == 2 ? "live_wingman.cfg" : "live.cfg";
            await _configLoader.LoadAndExecuteConfigAsync(liveCfg);
            Server.PrintToChatAll(" [Scrim] Match is LIVE!");
            if (_selectedMap != null) await _matchTracker.StartMatchAsync(_selectedMap);
        }
    }

    public async Task StartScrimAsync()
    {
        if (CurrentState != ScrimState.Idle) return;
        await _machine.FireAsync(ScrimTrigger.StartLobby);
    }

    public async Task StopScrimAsync()
    {
        await _machine.FireAsync(ScrimTrigger.StopScrim);
        await _persistence.ClearStateAsync();
        Server.PrintToChatAll(" [Scrim] Scrim stopped by admin.");
    }

    public async Task SetReadyAsync(ulong steamId, bool ready)
    {
        if (CurrentState != ScrimState.Lobby) return;

        if (!_configLoader.IsPlayerWhitelisted(steamId))
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            player?.PrintToChat(" [Scrim] You are not whitelisted for this scrim.");
            return;
        }

        _readyPlayers[steamId] = ready;
        var readyCount = _readyPlayers.Values.Count(v => v);
        Server.PrintToChatAll($" [Scrim] Player {(ready ? "ready" : "unready")}. ({readyCount}/{_minReadyPlayers})");

        if (readyCount >= _minReadyPlayers)
        {
            await _machine.FireAsync(ScrimTrigger.AllReady);
        }
    }

    public Task SetCaptainAsync(int team, ulong steamId)
    {
        if (CurrentState != ScrimState.CaptainSetup) return Task.CompletedTask;
        
        if (team != 1 && team != 2) return Task.CompletedTask;
        
        _captains[team] = steamId;
        var player = Utilities.GetPlayerFromSteamId(steamId);
        Server.PrintToChatAll($" [Scrim] {player?.PlayerName} appointed as Captain for Team {team}.");

        if (_captains.Count == 2)
        {
            _machine.Fire(ScrimTrigger.CaptainsAssigned);
        }
        return Task.CompletedTask;
    }

    public async Task VoteMapAsync(ulong steamId, string mapName)
    {
        if (CurrentState != ScrimState.MapVote) return;
        
        var normalizedMap = mapName.ToLower();
        if (!_config.Scrim.MapPool.Contains(normalizedMap))
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            player?.PrintToChat($" [Scrim] Invalid map. Pool: {string.Join(", ", _config.Scrim.MapPool)}");
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
            Server.PrintToChatAll($" [Scrim] Map selected: {_selectedMap}");
            await _machine.FireAsync(ScrimTrigger.MapSelected);
        }
    }

    public async Task PickPlayerAsync(ulong steamId, ulong targetSteamId)
    {
        if (CurrentState != ScrimState.Picking) return;
        
        var activeCaptainId = _team1Picking ? _captains[1] : _captains[2];
        if (steamId != activeCaptainId) return;

        if (!_pickPool.Contains(targetSteamId)) return;

        _pickPool.Remove(targetSteamId);
        if (_team1Picking) _team1.Add(targetSteamId);
        else _team2.Add(targetSteamId);

        var player = Utilities.GetPlayerFromSteamId(targetSteamId);
        Server.PrintToChatAll($" [Scrim] Captain {(_team1Picking ? "1" : "2")} picked {player?.PlayerName}.");

        if (_pickPool.Count == 0)
        {
            await _machine.FireAsync(ScrimTrigger.PickingFinished);
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

    public async Task HandleKnifeRoundEnd(int winnerTeam)
    {
        if (CurrentState != ScrimState.KnifeRound) return;
        
        _knifeWinnerTeam = winnerTeam;
        var winnerName = winnerTeam == 2 ? "Terrorists" : "Counter-Terrorists";
        Server.PrintToChatAll($" [Scrim] {winnerName} won the knife round!");
        
        // Find a captain from the winning team to make the choice
        // If team1 (Terrorists initially) won, they pick.
        var winningCaptainId = winnerTeam == 2 ? _team1.FirstOrDefault() : _team2.FirstOrDefault();
        if (winningCaptainId == 0) 
        {
            // Fallback if no captains assigned (shouldn't happen in standard flow)
            await _machine.FireAsync(ScrimTrigger.KnifeRoundFinished);
            return;
        }

        var captain = Utilities.GetPlayerFromSteamId(winningCaptainId);
        captain?.PrintToChat(" [Scrim] You won! Type .ct or .t to choose your side. (30s timeout)");

        _sideSelectionTimer = new CounterStrikeSharp.API.Modules.Timers.Timer(30.0f, () => 
        {
            _logger.LogInformation("Side selection timed out. Auto-assigning sides.");
            Server.PrintToChatAll(" [Scrim] Side selection timed out. Proceeding with current sides.");
            _ = _machine.FireAsync(ScrimTrigger.KnifeRoundFinished);
        });
    }

    public async Task SelectSideAsync(ulong steamId, string side)
    {
        if (CurrentState != ScrimState.KnifeRound) return;

        var winningCaptainId = _knifeWinnerTeam == 2 ? _team1.FirstOrDefault() : _team2.FirstOrDefault();
        if (steamId != winningCaptainId) return;

        _sideSelectionTimer?.Kill();

        bool stay = (side.ToLower() == "t" && _knifeWinnerTeam == 2) || (side.ToLower() == "ct" && _knifeWinnerTeam == 3);
        
        if (!stay)
        {
            // Swap rosters to reflect new sides
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
        await _machine.FireAsync(ScrimTrigger.KnifeRoundFinished);
    }

    public void HandleDisconnect(ulong steamId)
    {
        if (_readyPlayers.TryGetValue(steamId, out var ready) && ready)
        {
            _readyPlayers[steamId] = false;
            if (CurrentState == ScrimState.Lobby)
            {
                Server.PrintToChatAll($" [Scrim] Player disconnected. ({_readyPlayers.Values.Count(v => v)}/{_minReadyPlayers})");
            }
            else if (CurrentState != ScrimState.Idle && CurrentState != ScrimState.Live)
            {
                Server.PrintToChatAll(" [Scrim] Critical player disconnected. Scrim aborted.");
                _machine.Fire(ScrimTrigger.StopScrim);
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
