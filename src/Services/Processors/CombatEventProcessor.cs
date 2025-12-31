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

    private readonly IPlayerSessionService _playerSessions;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly ILogger<CombatEventProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;

    private int _currentRoundNumber;
    private DateTime _currentRoundStartUtc;
    private int _ctAliveAtStart;
    private int _tAliveAtStart;

    private readonly HashSet<ulong> _playersAliveThisRound = [];
    private readonly Dictionary<PlayerTeam, DateTime> _lastTeamDeathTime = new()
    {
        [PlayerTeam.Terrorist] = DateTime.MinValue,
        [PlayerTeam.CounterTerrorist] = DateTime.MinValue
    };
    private readonly Dictionary<ulong, DateTime> _lastDeathByPlayer = [];
    private readonly Dictionary<ulong, DateTime> _firstKillTimes = [];
    private readonly Dictionary<ulong, int> _roundKills = [];
    private readonly Dictionary<ulong, (ulong KillerId, DateTime Time)> _lastKillerOfPlayer = [];
    private readonly Dictionary<ulong, List<(ulong TeammateId, DateTime Expiry)>> _pendingTradeOpportunities = [];

    public CombatEventProcessor(
        IPlayerSessionService playerSessions,
        IOptionsMonitor<PluginConfig> config,
        ILogger<CombatEventProcessor> logger,
        TimeProvider timeProvider,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        IDamageReportService damageReport,
        ITaskTracker taskTracker)
    {
        _playerSessions = playerSessions;
        _config = config;
        _logger = logger;
        _timeProvider = timeProvider;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _damageReport = damageReport;
        _taskTracker = taskTracker;
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _currentRoundStartUtc = context.RoundStartUtc;
        _ctAliveAtStart = context.CtAliveAtStart;
        _tAliveAtStart = context.TAliveAtStart;
        ResetRoundStats();
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
        var winningTeam = winnerTeam switch { 2 => PlayerTeam.Terrorist, 3 => PlayerTeam.CounterTerrorist, _ => PlayerTeam.Spectator };
        UpdateClutchStats(winningTeam);
    }

    private PlayerTeam GetTeam(CCSPlayerController? player)
    {
        if (player is not { IsValid: true }) return PlayerTeam.Spectator;
        return player.TeamNum switch { 2 => PlayerTeam.Terrorist, 3 => PlayerTeam.CounterTerrorist, _ => PlayerTeam.Spectator };
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventPlayerDeath>((e, i) => { HandlePlayerDeath(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerHurt>((e, i) => { HandlePlayerHurt(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventWeaponFire>((e, i) => { HandleWeaponFire(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventBulletImpact>((e, i) => { HandleBulletImpact(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventRoundMvp>((e, i) => { HandleRoundMvp(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerAvengedTeammate>((e, i) => { HandlePlayerAvengedTeammate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerSpawned>((e, i) => { HandlePlayerSpawned(e); return HookResult.Continue; });
    }

    private void HandlePlayerDeath(EventPlayerDeath @event)
    {
        using var activity = Instrumentation.ActivitySource.StartActivity("HandlePlayerDeath");
        try
        {
            var victim = @event.GetPlayerOrDefault("userid");
            var attacker = @event.GetPlayerOrDefault("attacker");
            var assister = @event.GetPlayerOrDefault("assister");
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
            var victimTeam = GetTeam(victim);

            activity?.SetTag("victim.steamid", victim?.SteamID);
            activity?.SetTag("attacker.steamid", attacker?.SteamID);
            activity?.SetTag("weapon", weaponName);
            activity?.SetTag("headshot", isHeadshot);
            activity?.SetTag("noscope", noscope);
            activity?.SetTag("thru_smoke", thruSmoke);
            activity?.SetTag("attacker_blind", attackerBlind);
            activity?.SetTag("flash_assisted", flashAssisted);
            activity?.SetTag("penetrated", penetrated);

            bool isGrenadeKill = GrenadeWeaponNames.Contains(weaponName.ToLowerInvariant());

            if (victim is { IsBot: false })
            {
                Instrumentation.DeathsCounter.Add(1, 
                    new KeyValuePair<string, object?>("team", victimTeam.ToString()),
                    new KeyValuePair<string, object?>("map", Server.MapName));
                
                if (victim.PlayerPawn.Value != null)
                {
                    var matchId = _matchTracker?.CurrentMatch?.MatchId;
                    _taskTracker.Track("DeathPositionEnqueue", _positionPersistence.EnqueueAsync(new DeathPositionEvent(
                        matchId,
                        victim.SteamID,
                        victim.PlayerPawn.Value.AbsOrigin?.X ?? 0,
                        victim.PlayerPawn.Value.AbsOrigin?.Y ?? 0,
                        victim.PlayerPawn.Value.AbsOrigin?.Z ?? 0,
                        weaponName,
                        isHeadshot,
                        (int)victimTeam,
                        Server.MapName,
                        _currentRoundNumber,
                        (int)(now - _currentRoundStartUtc).TotalSeconds
                    ), CancellationToken.None));
                }

                _playerSessions.MutatePlayer(victim.SteamID, stats =>
                {
                    stats.Round.SurvivedThisRound = false;
                    stats.Combat.Deaths++;
                    stats.Combat.CurrentRoundDeaths++;
                    stats.CurrentTeam = victimTeam;
                });

                var victimPos = victim.PlayerPawn.Value?.AbsOrigin;
                if (victimPos != null)
                {
                    foreach (var teammateId in _playerSessions.GetActiveSteamIds())
                    {
                        if (teammateId == victim.SteamID) continue;
                        var teammate = Utilities.GetPlayerFromSteamId(teammateId);
                        if (teammate is { IsValid: true, PlayerPawn.Value: not null } && teammate.TeamNum == (int)victimTeam && _playersAliveThisRound.Contains(teammateId))
                        {
                            var teammatePos = teammate.PlayerPawn.Value.AbsOrigin;
                            if (teammatePos != null)
                            {
                                var distance = CalculateDistance(victimPos, teammatePos);
                                if (distance <= _config.CurrentValue.TradeDistanceThreshold)
                                {
                                    Instrumentation.TradeOpportunitiesCounter.Add(1);
                                    _playerSessions.MutatePlayer(teammateId, stats => stats.Combat.TradeKills++); // Using TradeKills as proxy for opportunity count if needed, or keeping it separate
                                    if (!_pendingTradeOpportunities.TryGetValue(teammateId, out var ops))
                                    {
                                        ops = [];
                                        _pendingTradeOpportunities[teammateId] = ops;
                                    }
                                    ops.Add((victim.SteamID, now.AddSeconds(tradeWindowSeconds)));
                                }
                            }
                        }
                    }
                }

                _playersAliveThisRound.Remove(victim.SteamID);
                _lastTeamDeathTime[victimTeam] = now;
                _lastDeathByPlayer[victim.SteamID] = now;

                if (attacker is { IsBot: false })
                {
                    _lastKillerOfPlayer[victim.SteamID] = (attacker.SteamID, now);
                }
            }

            if (attacker is { IsBot: false } && attacker != victim)
            {
                var attackerTeam = GetTeam(attacker);
                var (ctAlive, tAlive) = GetAliveCounts();
                var enemyAlive = attackerTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                var teammatesAlive = attackerTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;

                // Check for Revenge Kill
                bool isRevengeKill = false;
                if (victim != null && _lastKillerOfPlayer.TryGetValue(attacker.SteamID, out var lastKiller) && lastKiller.KillerId == victim.SteamID)
                {
                    isRevengeKill = true;
                    _lastKillerOfPlayer.Remove(attacker.SteamID);
                }

                Instrumentation.KillsCounter.Add(1, 
                    new KeyValuePair<string, object?>("team", attackerTeam.ToString()), 
                    new KeyValuePair<string, object?>("weapon", weaponName),
                    new KeyValuePair<string, object?>("map", Server.MapName),
                    new KeyValuePair<string, object?>("is_revenge", isRevengeKill));
                
                Instrumentation.DamageCounter.Add(damage, 
                    new KeyValuePair<string, object?>("team", attackerTeam.ToString()),
                    new KeyValuePair<string, object?>("map", Server.MapName));

                if (attacker.PlayerPawn.Value != null && victim?.PlayerPawn.Value != null)
                {
                    var attackerPos = attacker.PlayerPawn.Value.AbsOrigin ?? new Vector(0, 0, 0);
                    var victimPosKill = victim.PlayerPawn.Value.AbsOrigin ?? new Vector(0, 0, 0);
                    var killDistance = CalculateDistance(attackerPos, victimPosKill);
                    var matchId = _matchTracker?.CurrentMatch?.MatchId;

                    _taskTracker.Track("KillPositionEnqueue", _positionPersistence.EnqueueAsync(new KillPositionEvent(
                        matchId,
                        attacker.SteamID,
                        victim.SteamID,
                        attackerPos.X,
                        attackerPos.Y,
                        attackerPos.Z,
                        victimPosKill.X,
                        victimPosKill.Y,
                        victimPosKill.Z,
                        weaponName,
                        isHeadshot,
                        penetrated,
                        killDistance,
                        (int)attackerTeam,
                        (int)victimTeam,
                        Server.MapName,
                        _currentRoundNumber,
                        (int)(now - _currentRoundStartUtc).TotalSeconds
                    ), CancellationToken.None));
                }

                _playerSessions.MutatePlayer(attacker.SteamID, stats =>
                {
                    stats.Round.HadKillThisRound = true;
                    stats.Combat.Kills++;
                    stats.Combat.Headshots += isHeadshot ? 1 : 0;
                    stats.Weapon.RecordKill(weaponName);
                    stats.CurrentTeam = attackerTeam;

                    if (isRevengeKill) stats.Combat.RevengeKills++;

                    if (!_firstKillTimes.ContainsKey(attacker.SteamID)) 
                    { 
                        _firstKillTimes[attacker.SteamID] = now; 
                        stats.Combat.FirstKills++; 
                    }
                    
                    _roundKills[attacker.SteamID] = _roundKills.GetValueOrDefault(attacker.SteamID, 0) + 1;
                    var killsThisRound = _roundKills[attacker.SteamID];
                    stats.Combat.CurrentRoundKills = killsThisRound;
                    
                    // Multi-kill tracking logic
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
                    if (flashAssisted) stats.Utility.FlashAssists++; // Integrated into UtilityStats
                    if (penetrated) stats.Combat.WallbangKills++;

                    if (isGrenadeKill)
                    {
                        stats.Combat.NadeKills++;
                        var nadeKillsThisRound = _roundKills[attacker.SteamID];
                        if (nadeKillsThisRound >= 2) stats.Combat.MultiKillNades++;
                    }

                    bool highImpact = killsThisRound >= 3 || (teammatesAlive == 1 && enemyAlive >= 1) || (_firstKillTimes.Count == 1 && _firstKillTimes.ContainsKey(attacker.SteamID));
                    if (highImpact) stats.Combat.HighImpactKills++;
                    else if (teammatesAlive >= enemyAlive + 3) stats.Combat.LowImpactKills++;

                    if (isGrenadeKill)
                    {
                        // Logic for grenade kills can be expanded if needed
                    }
                });

                if (victimTeam != PlayerTeam.Spectator && _lastTeamDeathTime.TryGetValue(victimTeam, out var lastDeath) && lastDeath > DateTime.MinValue && lastDeath != now && (now - lastDeath).TotalSeconds <= tradeWindowSeconds)
                {
                    _playerSessions.MutatePlayer(attacker.SteamID, stats => { stats.Combat.TradeKills++; stats.Round.DidTradeThisRound = true; });
                    if (_pendingTradeOpportunities.TryGetValue(attacker.SteamID, out var ops)) ops.RemoveAll(o => o.Expiry > now);
                    if (victim is { IsBot: false }) _playerSessions.MutatePlayer(victim.SteamID, stats => { stats.Combat.TradedDeaths++; stats.Round.WasTradedThisRound = true; });
                }
            }

            if (assister is { IsBot: false } && assister != victim && assister != attacker)
            {
                _playerSessions.MutatePlayer(assister.SteamID, stats =>
                {
                    stats.Round.HadAssistThisRound = true;
                    stats.Combat.Assists++;
                    stats.CurrentTeam = GetTeam(assister);
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
            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var damage = @event.GetIntValue("dmg_health", 0);
            var damageArmor = @event.GetIntValue("dmg_armor", 0);
            var hitgroup = @event.GetIntValue("hitgroup", 0);

            if (victim is { IsBot: false })
            {
                _playerSessions.MutatePlayer(victim.SteamID, stats =>
                {
                    stats.Combat.DamageTaken += damage;
                });
            }

            if (attacker is { IsBot: false } && attacker != victim)
            {
                _damageReport.RecordDamage(attacker.SteamID, victim?.SteamID ?? 0, damage);
                _playerSessions.MutatePlayer(attacker.SteamID, stats =>
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
            if (player is not { IsBot: false, IsValid: true }) return;
            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var weaponLower = weapon.ToLowerInvariant();
            _playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.Combat.ShotsFired++;
                stats.Combat.CurrentRoundShotsFired++;
                
                if (player.PlayerPawn.Value != null)
                {
                    var velocity = player.PlayerPawn.Value.AbsVelocity.Length();
                    if (velocity < 15.0f) stats.Combat.ShotsFiredWhileStationary++;
                }

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
            var mvpReason = @event.GetIntValue("reason", 0);
            if (player is { IsBot: false })
            {
                _playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.Mvps++;
                    switch (mvpReason) { case 0: stats.MvpsEliminations++; break; case 1: stats.MvpsBomb++; break; case 2: stats.MvpsHostage++; break; }
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling round MVP event"); }
    }

    private void HandlePlayerAvengedTeammate(EventPlayerAvengedTeammate @event)
    {
        try
        {
            var avenger = @event.GetPlayerOrDefault("avenger_userid");
            if (avenger is { IsBot: false }) _playerSessions.MutatePlayer(avenger.SteamID, stats => stats.Revenges++);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player avenged teammate event"); }
    }

    private void HandlePlayerSpawned(EventPlayerSpawned @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                var team = GetTeam(player);
                _playersAliveThisRound.Add(player.SteamID);
                _playerSessions.MutatePlayer(player.SteamID, stats => { stats.SurvivedThisRound = true; stats.CurrentTeam = team; });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player spawned event"); }
    }

    public void ResetRoundStats()
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var kvp in _pendingTradeOpportunities)
            {
                var missedCount = kvp.Value.Count(o => o.Expiry <= now);
                if (missedCount > 0) _playerSessions.MutatePlayer(kvp.Key, stats => stats.TradeWindowsMissed += missedCount);
            }
            _firstKillTimes.Clear(); _roundKills.Clear(); _playersAliveThisRound.Clear(); _lastDeathByPlayer.Clear(); _pendingTradeOpportunities.Clear();
            _lastKillerOfPlayer.Clear();
            _lastTeamDeathTime[PlayerTeam.Terrorist] = DateTime.MinValue; _lastTeamDeathTime[PlayerTeam.CounterTerrorist] = DateTime.MinValue;
        }
        catch (Exception ex) { _logger.LogError(ex, "Error resetting round statistics"); }
    }

    public void UpdateClutchStats(PlayerTeam winningTeam)
    {
        try
        {
            var (ctAlive, tAlive) = GetAliveCounts();
            foreach (var playerId in _playersAliveThisRound)
            {
                _playerSessions.MutatePlayer(playerId, stats =>
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
                            stats.ClutchesWon++; stats.ClutchPoints += clutchImpact;
                        }
                        else stats.ClutchesLost++;
                    }
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error updating clutch statistics"); }
    }

    public (int CtAlive, int TAlive) GetAliveCounts()
    {
        var ctAlive = 0; var tAlive = 0;
        foreach (var playerId in _playersAliveThisRound)
        {
            var team = _playerSessions.WithPlayer(playerId, ps => ps.CurrentTeam, PlayerTeam.Spectator);
            if (team == PlayerTeam.CounterTerrorist) ctAlive++; else if (team == PlayerTeam.Terrorist) tAlive++;
        }
        return (ctAlive, tAlive);
    }

    private static float CalculateDistance(Vector pos1, Vector pos2)
    {
        var dx = pos2.X - pos1.X; var dy = pos2.Y - pos1.Y; var dz = pos2.Z - pos1.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
