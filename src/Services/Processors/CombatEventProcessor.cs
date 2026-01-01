using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface ICombatEventProcessor : IEventProcessor
{
    (int CtAlive, int TAlive) GetAliveCounts();
}

public sealed class CombatEventProcessor : ICombatEventProcessor
{
    private static readonly HashSet<string> GrenadeWeaponNames = 
    [
        "weapon_flashbang",
        "weapon_hegrenade",
        "weapon_smokegrenade",
        "weapon_molotov",
        "weapon_incgrenade",
        "weapon_decoy",
        "weapon_tagrenade",
        "weapon_frag_grenade"
    ];

    private static readonly int MaxPlayers = 65; // CS2 max players + 1 for safety

    private readonly IPlayerSessionService _playerSessions;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly ILogger<CombatEventProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IDamageReportService _damageReport;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IGameScheduler _scheduler;

    private int _currentRoundNumber;
    private DateTime _currentRoundStartUtc;
    private int _ctAliveAtStart;
    private int _tAliveAtStart;

    // EntityIndex-based state arrays for performance
    private readonly bool[] _isPlayerAlive = new bool[MaxPlayers];
    private readonly ulong[] _playerSteamIds = new ulong[MaxPlayers];
    private readonly int[] _playerRoundKills = new int[MaxPlayers];
    private readonly DateTime[] _playerLastDeathTime = new DateTime[MaxPlayers];
    private readonly DateTime[] _playerFirstKillTime = new DateTime[MaxPlayers];
    private readonly (ulong KillerId, DateTime Time)[] _playerLastKiller = new (ulong, DateTime)[MaxPlayers];

    // Entry Kill Attempt tracking
    private bool _firstEngagementHappened;
    private int _firstEngagementAttackerIndex = -1;
    private int _firstEngagementVictimIndex = -1;

    private readonly Dictionary<PlayerTeam, DateTime> _lastTeamDeathTime = new()
    {
        [PlayerTeam.Terrorist] = DateTime.MinValue,
        [PlayerTeam.CounterTerrorist] = DateTime.MinValue
    };

    private readonly Dictionary<ulong, List<(ulong TeammateId, DateTime Expiry)>> _pendingTradeOpportunities = [];

    private int _ctAliveCount;
    private int _tAliveCount;

    public CombatEventProcessor(
        IPlayerSessionService playerSessions,
        IOptionsMonitor<PluginConfig> config,
        ILogger<CombatEventProcessor> logger,
        TimeProvider timeProvider,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        IDamageReportService damageReport,
        IPersistenceChannel persistenceChannel,
        IGameScheduler scheduler)
    {
        _playerSessions = playerSessions;
        _config = config;
        _logger = logger;
        _timeProvider = timeProvider;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _damageReport = damageReport;
        _persistenceChannel = persistenceChannel;
        _scheduler = scheduler;
    }

    public (int CtAlive, int TAlive) GetAliveCounts() => (_ctAliveCount, _tAliveCount);

        public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventPlayerDeath>((e, i) => { HandlePlayerDeath(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerHurt>((e, i) => { HandlePlayerHurt(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventWeaponFire>((e, i) => { HandleWeaponFire(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBulletImpact>((e, i) => { HandleBulletImpact(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventRoundMvp>((e, i) => { HandleRoundMvp(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerAvengedTeammate>((e, i) => { HandlePlayerAvengedTeammate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerSpawned>((e, i) => { HandlePlayerSpawned(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerDisconnect>((e, i) => { HandlePlayerDisconnected(e); return HookResult.Continue; });
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _currentRoundStartUtc = context.RoundStartUtc;
        _ctAliveAtStart = context.CtAliveAtStart;
        _tAliveAtStart = context.TAliveAtStart;
        ResetRoundStats();
    }

    private void ResetRoundStats()
    {
        Array.Clear(_isPlayerAlive);
        Array.Clear(_playerRoundKills);
        Array.Clear(_playerFirstKillTime);
        Array.Clear(_playerLastDeathTime);
        Array.Clear(_playerLastKiller);
        
        _ctAliveCount = 0;
        _tAliveCount = 0;

        _firstEngagementHappened = false;
        _firstEngagementAttackerIndex = -1;
        _firstEngagementVictimIndex = -1;

        _lastTeamDeathTime[PlayerTeam.Terrorist] = DateTime.MinValue;
        _lastTeamDeathTime[PlayerTeam.CounterTerrorist] = DateTime.MinValue;
        
        // Clean up trade opportunities expired from previous rounds
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var kvp in _pendingTradeOpportunities)
        {
            kvp.Value.RemoveAll(o => o.Expiry <= now);
        }
    }

    private void HandlePlayerDeath(EventPlayerDeath @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandlePlayerDeath");
        try
        {
            var victim = @event.GetPlayerOrDefault("userid");
            var attacker = @event.GetPlayerOrDefault("attacker");
            var assister = @event.GetPlayerOrDefault("assister");

            if (victim == null) return;
            var victimIndex = (int)victim.Index;

            // Snapshot state on game thread
            var victimState = PlayerControllerState.From(victim);
            var attackerState = PlayerControllerState.From(attacker);
            var assisterState = PlayerControllerState.From(assister);

            // Handle Entry Kill tracking for insta-kills (no player_hurt event preceded this)
            if (!_firstEngagementHappened && attackerState.IsValid && victimState.IsValid && attackerState.Team != victimState.Team && !attackerState.IsBot && !victimState.IsBot)
            {
                _firstEngagementHappened = true;
                _firstEngagementAttackerIndex = (int)attacker!.Index;
                _firstEngagementVictimIndex = victimIndex;
                _playerSessions.MutatePlayer(attackerState.SteamId, stats => stats.Combat.EntryKillAttempts++);
            }
            
            var weaponName = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var isHeadshot = @event.GetBoolValue("headshot", false);
            var damage = @event.GetIntValue("dmg_health", 0);
            
            var noscope = @event.GetBoolValue("noscope", false);
            var thruSmoke = @event.GetBoolValue("thru_smoke", false);
            var attackerBlind = @event.GetBoolValue("attacker_blind", false);
            var flashAssisted = @event.GetBoolValue("assistedflash", false);
            var penetrated = @event.GetIntValue("penetrated", 0) > 0;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var tradeWindowSeconds = _config.CurrentValue.TradeWindowSeconds;
            var victimTeam = victimState.Team;

            activity?.SetTag("victim.steamid", victimState.SteamId);
            activity?.SetTag("attacker.steamid", attackerState.SteamId);
            activity?.SetTag("weapon", weaponName);
            activity?.SetTag("headshot", isHeadshot);

            bool isGrenadeKill = GrenadeWeaponNames.Contains(weaponName.ToLowerInvariant());
            bool isFlashAssist = flashAssisted;

            if (victimState.IsValid && !victimState.IsBot)
            {
                Instrumentation.DeathsCounter.Add(1, 
                    new KeyValuePair<string, object?>("team", victimTeam.ToString()),
                    new KeyValuePair<string, object?>("map", Server.MapName));
                
                if (victimState.PawnHandle != 0)
                {
                    var matchUuid = _matchTracker?.CurrentMatch?.MatchUuid;
                    var posEvent = _positionPersistence.GetDeathEvent();

                    posEvent.MatchUuid = matchUuid;
                    posEvent.SteamId = victimState.SteamId;
                    posEvent.X = victimState.Position.X;
                    posEvent.Y = victimState.Position.Y;
                    posEvent.Z = victimState.Position.Z;
                    posEvent.CauseOfDeath = weaponName;
                    posEvent.IsHeadshot = isHeadshot;
                    posEvent.Team = (int)victimTeam;
                    posEvent.MapName = Server.MapName;
                    posEvent.RoundNumber = _currentRoundNumber;
                    posEvent.RoundTime = (int)(now - _currentRoundStartUtc).TotalSeconds;
                    
                    _ = _positionPersistence.EnqueueAsync(posEvent, CancellationToken.None);
                }

                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Round.SurvivedThisRound = false;
                    stats.Combat.Deaths++;
                    stats.Combat.CurrentRoundDeaths++;
                    stats.CurrentTeam = victimTeam;

                    // Entry Death tracking
                    if (_firstEngagementVictimIndex == victimIndex)
                    {
                        stats.Combat.EntryDeaths++;
                    }
                });

                _isPlayerAlive[victimIndex] = false;
                if (victimTeam == PlayerTeam.CounterTerrorist) _ctAliveCount--;
                else if (victimTeam == PlayerTeam.Terrorist) _tAliveCount--;

                _lastTeamDeathTime[victimTeam] = now;
                _playerLastDeathTime[victimIndex] = now;

                if (attackerState.IsValid && !attackerState.IsBot)
                {
                    _playerLastKiller[victimIndex] = (attackerState.SteamId, now);
                }
            }

            if (attackerState.IsValid && !attackerState.IsBot && attackerState.SteamId != victimState.SteamId)
            {
                var attackerIndex = (int)attacker!.Index;
                var attackerTeam = attackerState.Team;
                var (ctAlive, tAlive) = GetAliveCounts();
                var enemyAlive = attackerTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                var teammatesAlive = attackerTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;

                decimal killWeight = Math.Clamp((decimal)victimState.EquipmentValue / 5000m, 0.2m, 1.0m);

                bool isFirstKillOfRound = false;
                if (_playerFirstKillTime.All(t => t == DateTime.MinValue))
                {
                    isFirstKillOfRound = true;
                    killWeight += 0.15m;
                }

                if (teammatesAlive == 1 && enemyAlive >= 1) killWeight += 0.1m; 
                
                bool isRevengeKill = false;
                if (victimState.IsValid && _playerLastKiller[attackerIndex].KillerId == victimState.SteamId)
                {
                    isRevengeKill = true;
                    _playerLastKiller[attackerIndex] = default;
                }

                Instrumentation.KillsCounter.Add(1, 
                    new KeyValuePair<string, object?>("team", attackerTeam.ToString()), 
                    new KeyValuePair<string, object?>("weapon", weaponName),
                    new KeyValuePair<string, object?>("map", Server.MapName),
                    new KeyValuePair<string, object?>("is_revenge", isRevengeKill));
                
                Instrumentation.DamageCounter.Add(damage, 
                    new KeyValuePair<string, object?>("team", attackerTeam.ToString()),
                    new KeyValuePair<string, object?>("map", Server.MapName));

                if (attackerState.PawnHandle != 0 && victimState.PawnHandle != 0)
                {
                    var killDistance = CalculateDistance(attackerState.Position, victimState.Position);
                    var matchUuid = _matchTracker?.CurrentMatch?.MatchUuid;
                    var posEvent = _positionPersistence.GetKillEvent();

                    posEvent.MatchUuid = matchUuid;
                    posEvent.KillerSteamId = attackerState.SteamId;
                    posEvent.VictimSteamId = victimState.SteamId;
                    posEvent.KillerX = attackerState.Position.X;
                    posEvent.KillerY = attackerState.Position.Y;
                    posEvent.KillerZ = attackerState.Position.Z;
                    posEvent.VictimX = victimState.Position.X;
                    posEvent.VictimY = victimState.Position.Y;
                    posEvent.VictimZ = victimState.Position.Z;
                    posEvent.Weapon = weaponName;
                    posEvent.IsHeadshot = isHeadshot;
                    posEvent.IsWallbang = penetrated;
                    posEvent.Distance = killDistance;
                    posEvent.KillerTeam = (int)attackerTeam;
                    posEvent.VictimTeam = (int)victimTeam;
                    posEvent.MapName = Server.MapName;
                    posEvent.RoundNumber = _currentRoundNumber;
                    posEvent.RoundTime = (int)(now - _currentRoundStartUtc).TotalSeconds;

                    _ = _positionPersistence.EnqueueAsync(posEvent, CancellationToken.None);
                }

                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    stats.Round.HadKillThisRound = true;
                    stats.Combat.Kills++;
                    stats.Combat.WeightedKills += killWeight;
                    stats.Combat.Headshots += isHeadshot ? 1 : 0;
                    stats.Weapon.RecordKill(weaponName);
                    stats.CurrentTeam = attackerTeam;

                    if (isRevengeKill) stats.Combat.RevengeKills++;

                    if (_playerFirstKillTime[attackerIndex] == DateTime.MinValue) 
                    { 
                        _playerFirstKillTime[attackerIndex] = now; 
                        stats.Combat.FirstKills++; 
                    }

                    // Entry Kill tracking
                    if (_firstEngagementAttackerIndex == attackerIndex)
                    {
                        stats.Combat.EntryKills++;
                        stats.Combat.EntryKillAttemptWins++;
                    }
                    
                    _playerRoundKills[attackerIndex]++;
                    var killsThisRound = _playerRoundKills[attackerIndex];
                    stats.Combat.CurrentRoundKills = killsThisRound;
                    
                    switch(killsThisRound)
                    {
                        case 2: stats.Combat.MultiKill2++; break;
                        case 3: stats.Combat.MultiKill3++; break;
                        case 4: stats.Combat.MultiKill4++; break;
                        case 5: stats.Combat.MultiKill5++; break;
                    }

                    if (noscope) stats.Combat.Noscopes++;
                    if (thruSmoke) stats.Combat.ThroughSmokeKills++;
                    if (attackerBlind) stats.Combat.BlindKills++;
                    if (isFlashAssist) 
                    {
                        stats.Utility.FlashAssists++;
                        // Capture victim's blind duration at time of death for Flash Assist Duration
                        // Use IGameScheduler to safely read from the entity if needed, 
                        // but since we are on the game thread in HandlePlayerDeath, we can access it.
                        if (victim.PlayerPawn.Value != null)
                        {
                            stats.Utility.FlashAssistDuration += (int)(victim.PlayerPawn.Value.FlashDuration * 1000);
                        }
                    }
                    if (penetrated) stats.Combat.WallbangKills++;

                    if (isGrenadeKill)
                    {
                        stats.Combat.NadeKills++;
                        if (killsThisRound >= 2) stats.Combat.MultiKillNades++;
                    }

                    bool highImpact = killsThisRound >= 3 || (teammatesAlive == 1 && enemyAlive >= 1) || isFirstKillOfRound;
                    if (highImpact) stats.Combat.HighImpactKills++;
                    else if (teammatesAlive >= enemyAlive + 3) stats.Combat.LowImpactKills++;
                });

                if (victimTeam != PlayerTeam.Spectator && _lastTeamDeathTime.TryGetValue(victimTeam, out var lastDeath) && lastDeath > DateTime.MinValue && lastDeath != now && (now - lastDeath).TotalSeconds <= tradeWindowSeconds)
                {
                    _playerSessions.MutatePlayer(attackerState.SteamId, stats => { stats.Combat.TradeKills++; stats.Round.DidTradeThisRound = true; });
                    if (_pendingTradeOpportunities.TryGetValue(attackerState.SteamId, out var ops)) ops.RemoveAll(o => o.Expiry > now);
                    if (victimState.IsValid && !victimState.IsBot) _playerSessions.MutatePlayer(victimState.SteamId, stats => { stats.Combat.TradedDeaths++; stats.Round.WasTradedThisRound = true; });
                }
            }

            if (assisterState.IsValid && !assisterState.IsBot && assisterState.SteamId != victimState.SteamId && assisterState.SteamId != attackerState.SteamId)
            {
                _playerSessions.MutatePlayer(assisterState.SteamId, stats =>
                {
                    stats.Round.HadAssistThisRound = true;
                    stats.Combat.Assists++;
                    stats.CurrentTeam = assisterState.Team;
                    if (flashAssisted) 
                    {
                        stats.Utility.FlashAssists++;
                        stats.Round.WasFlashedForKill = true;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling player death event");
        }
    }

    private void HandlePlayerHurt(EventPlayerHurt @event)
    {
        try
        {
            var victim = @event.GetPlayerOrDefault("userid");
            var attacker = @event.GetPlayerOrDefault("attacker");
            
            if (victim == null) return;
            var victimIndex = (int)victim.Index;

            var victimState = PlayerControllerState.From(victim);
            var attackerState = PlayerControllerState.From(attacker);
            
            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var damage = @event.GetIntValue("dmg_health", 0);
            var hitgroup = @event.GetIntValue("hitgroup", 0);

            // Entry Kill Attempt Tracking
            if (!_firstEngagementHappened && attackerState.IsValid && victimState.IsValid && attackerState.Team != victimState.Team && !attackerState.IsBot && !victimState.IsBot)
            {
                _firstEngagementHappened = true;
                _firstEngagementAttackerIndex = (int)attacker!.Index;
                _firstEngagementVictimIndex = victimIndex;

                _playerSessions.MutatePlayer(attackerState.SteamId, stats => stats.Combat.EntryKillAttempts++);
                _logger.LogInformation("First engagement of round {Round}: {Attacker} vs {Victim}", _currentRoundNumber, attackerState.PlayerName, victimState.PlayerName);
            }

            if (victimState.IsValid && !victimState.IsBot)
            {
                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Combat.DamageTaken += damage;
                });
            }

            if (attackerState.IsValid && !attackerState.IsBot && attackerState.SteamId != victimState.SteamId)
            {
                _damageReport.RecordDamage(attackerState.SteamId, victimState.SteamId, damage);
                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    stats.Combat.DamageDealt += damage;
                    stats.Weapon.RecordHit(weapon);
                    
                    switch (hitgroup)
                    {
                        case 1: stats.Combat.HeadshotsHit++; break;
                        case 2: stats.Combat.ChestHits++; break;
                        case 3: stats.Combat.StomachHits++; break;
                        case 4:
                        case 5: stats.Combat.ArmHits++; break;
                        case 6:
                        case 7: stats.Combat.LegHits++; break;
                    }
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player hurt event"); }
    }

    private void HandleWeaponFire(EventWeaponFire @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player == null) return;
            var playerIndex = (int)player.Index;

            var playerState = PlayerControllerState.From(player);
            if (!playerState.IsValid || playerState.IsBot) return;
            
            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var weaponLower = weapon.ToLowerInvariant();
            
            _playerSessions.MutatePlayer(playerState.SteamId, stats =>
            {
                stats.Combat.ShotsFired++;
                stats.Combat.CurrentRoundShotsFired++;
                
                if (playerState.Velocity.Length() < 15.0f) 
                    stats.Combat.ShotsFiredWhileStationary++;

                stats.Weapon.RecordShot(weapon);
                if (GrenadeWeaponNames.Contains(weaponLower))
                {
                    switch (weaponLower)
                    {
                        case "weapon_flashbang": stats.Utility.FlashbangsThrown++; break;
                        case "weapon_smokegrenade": stats.Utility.SmokesThrown++; break;
                        case "weapon_molotov":
                        case "weapon_incgrenade": stats.Utility.MolotovsThrown++; break;
                        case "weapon_hegrenade": stats.Utility.HeGrenadesThrown++; break;
                        case "weapon_decoy": stats.Utility.DecoysThrown++; break;
                    }
                }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling weapon fire event"); }
    }

    private void HandleBulletImpact(EventBulletImpact @event) { }

    private void HandleRoundMvp(EventRoundMvp @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player == null) return;
            
            var playerState = PlayerControllerState.From(player);
            if (!playerState.IsValid || playerState.IsBot) return;

            var mvpReason = @event.GetIntValue("reason", 0);
            _playerSessions.MutatePlayer(playerState.SteamId, stats =>
            {
                stats.Combat.MVPs++;
                switch (mvpReason) 
                { 
                    case 0: stats.Combat.MvpsEliminations++; break; 
                    case 1: stats.Combat.MvpsBomb++; break; 
                    case 2: stats.Combat.MvpsHostage++; break; 
                }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling round MVP event"); }
    }

    private void HandlePlayerAvengedTeammate(EventPlayerAvengedTeammate @event)
    {
        try
        {
            var avenger = @event.GetPlayerOrDefault("avenger_userid");
            var avengerState = PlayerControllerState.From(avenger);
            if (avengerState.IsValid && !avengerState.IsBot) 
                _playerSessions.MutatePlayer(avengerState.SteamId, stats => stats.Combat.Revenges++);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player avenged teammate event"); }
    }

    private void HandlePlayerSpawned(EventPlayerSpawned @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player == null) return;
            var playerIndex = (int)player.Index;

            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                if (!_isPlayerAlive[playerIndex])
                {
                    if (playerState.Team == PlayerTeam.CounterTerrorist) _ctAliveCount++;
                    else if (playerState.Team == PlayerTeam.Terrorist) _tAliveCount++;
                }

                _isPlayerAlive[playerIndex] = true;
                _playerSteamIds[playerIndex] = playerState.SteamId;
                _playerSessions.MutatePlayer(playerState.SteamId, stats => { stats.Round.SurvivedThisRound = true; stats.CurrentTeam = playerState.Team; });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player spawned event"); }
    }

        private void HandlePlayerDisconnected(EventPlayerDisconnect @event)
    {
        try
        {
            var player = @event.Userid;
            if (player == null) return;
            var playerIndex = (int)player.Index;
            if (playerIndex >= 0 && playerIndex < MaxPlayers)
            {
                if (_isPlayerAlive[playerIndex])
                {
                    var steamId = _playerSteamIds[playerIndex];
                    var team = _playerSessions.WithPlayer(steamId, ps => ps.CurrentTeam, PlayerTeam.Spectator);
                    if (team == PlayerTeam.CounterTerrorist) _ctAliveCount--;
                    else if (team == PlayerTeam.Terrorist) _tAliveCount--;
                }

                _isPlayerAlive[playerIndex] = false;
                _playerSteamIds[playerIndex] = 0;
                _playerRoundKills[playerIndex] = 0;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player disconnected event"); }
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
        var winningTeam = winnerTeam switch { 2 => PlayerTeam.Terrorist, 3 => PlayerTeam.CounterTerrorist, _ => PlayerTeam.Spectator };
        UpdateClutchStats(winningTeam);
    }

    private void UpdateClutchStats(PlayerTeam winningTeam)
    {
        try
        {
            var (ctAlive, tAlive) = GetAliveCounts();
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (_isPlayerAlive[i])
                {
                    var steamId = _playerSteamIds[i];
                    if (steamId == 0) continue;

                    _playerSessions.MutatePlayer(steamId, stats =>
                    {
                        var team = stats.CurrentTeam;
                        var teamAlive = team == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;
                        var enemyAlive = team == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                        if (teamAlive == 1 && enemyAlive >= 1)
                        {
                            if (team == winningTeam)
                            {
                                var settings = _config.CurrentValue.ClutchSettings;
                                var clutchImpact = settings.BaseMultiplier + (enemyAlive * settings.DifficultyWeight);
                                stats.Combat.ClutchesWon++; 
                                stats.Combat.ClutchPoints += clutchImpact;
                            }
                            else stats.Combat.ClutchesLost++;
                        }
                    });
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error updating clutch statistics"); }
    }

    private static float CalculateDistance(Vector pos1, Vector pos2)
    {
        var dx = pos2.X - pos1.X; var dy = pos2.Y - pos1.Y; var dz = pos2.Z - pos1.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
