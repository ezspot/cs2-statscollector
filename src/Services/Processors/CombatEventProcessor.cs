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

using Microsoft.Extensions.ObjectPool;

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
    private readonly IDamageReportService _damageReport;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IGameScheduler _scheduler;

    private readonly ObjectPool<HashSet<ulong>> _hashSetPool;
    private readonly ObjectPool<Dictionary<ulong, DateTime>> _dateTimeDictPool;
    private readonly ObjectPool<Dictionary<ulong, int>> _intDictPool;
    private readonly ObjectPool<Dictionary<ulong, (ulong KillerId, DateTime Time)>> _killerDictPool;
    private readonly ObjectPool<Dictionary<ulong, List<(ulong TeammateId, DateTime Expiry)>>> _tradeDictPool;

    private int _currentRoundNumber;
    private DateTime _currentRoundStartUtc;
    private int _ctAliveAtStart;
    private int _tAliveAtStart;

    private HashSet<ulong> _playersAliveThisRound;
    private readonly Dictionary<PlayerTeam, DateTime> _lastTeamDeathTime = new()
    {
        [PlayerTeam.Terrorist] = DateTime.MinValue,
        [PlayerTeam.CounterTerrorist] = DateTime.MinValue
    };
    private Dictionary<ulong, DateTime> _lastDeathByPlayer;
    private Dictionary<ulong, DateTime> _firstKillTimes;
    private Dictionary<ulong, int> _roundKills;
    private Dictionary<ulong, (ulong KillerId, DateTime Time)> _lastKillerOfPlayer;
    private Dictionary<ulong, List<(ulong TeammateId, DateTime Expiry)>> _pendingTradeOpportunities;

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

        var policy = new DefaultObjectPoolProvider();
        _hashSetPool = policy.Create(new HashSetPooledObjectPolicy<ulong>());
        _dateTimeDictPool = policy.Create(new DictionaryPooledObjectPolicy<ulong, DateTime>());
        _intDictPool = policy.Create(new DictionaryPooledObjectPolicy<ulong, int>());
        _killerDictPool = policy.Create(new DictionaryPooledObjectPolicy<ulong, (ulong KillerId, DateTime Time)>());
        _tradeDictPool = policy.Create(new DictionaryPooledObjectPolicy<ulong, List<(ulong TeammateId, DateTime Expiry)>>());

        _playersAliveThisRound = _hashSetPool.Get();
        _lastDeathByPlayer = _dateTimeDictPool.Get();
        _firstKillTimes = _dateTimeDictPool.Get();
        _roundKills = _intDictPool.Get();
        _lastKillerOfPlayer = _killerDictPool.Get();
        _pendingTradeOpportunities = _tradeDictPool.Get();
    }

    private class HashSetPooledObjectPolicy<T> : IPooledObjectPolicy<HashSet<T>>
    {
        public HashSet<T> Create() => new();
        public bool Return(HashSet<T> obj) { obj.Clear(); return true; }
    }

    private class DictionaryPooledObjectPolicy<TKey, TValue> : IPooledObjectPolicy<Dictionary<TKey, TValue>> where TKey : notnull
    {
        public Dictionary<TKey, TValue> Create() => new();
        public bool Return(Dictionary<TKey, TValue> obj) { obj.Clear(); return true; }
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

            // Snapshot state on game thread before background tasks
            var victimState = PlayerControllerState.From(victim);
            var attackerState = PlayerControllerState.From(attacker);
            var assisterState = PlayerControllerState.From(assister);
            
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
                    
                    _ = _positionPersistence.EnqueueAsync(new DeathPositionEvent(
                        matchUuid,
                        victimState.SteamId,
                        victimState.Position.X,
                        victimState.Position.Y,
                        victimState.Position.Z,
                        weaponName,
                        isHeadshot,
                        (int)victimTeam,
                        Server.MapName,
                        _currentRoundNumber,
                        (int)(now - _currentRoundStartUtc).TotalSeconds
                    ), CancellationToken.None);
                }

                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Round.SurvivedThisRound = false;
                    stats.Combat.Deaths++;
                    stats.Combat.CurrentRoundDeaths++;
                    stats.CurrentTeam = victimTeam;
                });

                foreach (var teammateId in _playerSessions.GetActiveSteamIds())
                {
                    if (teammateId == victimState.SteamId) continue;
                    
                    _playerSessions.WithPlayer(teammateId, stats => 
                    {
                        if (stats.CurrentTeam == victimTeam && _playersAliveThisRound.Contains(teammateId))
                        {
                            // We need teammate position, but WithPlayer only gives stats.
                            // For trade distance, we'd ideally need a teammate state snapshot too.
                            // For production, we should have captured ALL active player states if we wanted true thread safety for this loop.
                            // However, we can use the cached positions if available or skip distance check if not critical.
                        }
                        return 0;
                    });
                }

                _playersAliveThisRound.Remove(victimState.SteamId);
                _lastTeamDeathTime[victimTeam] = now;
                _lastDeathByPlayer[victimState.SteamId] = now;

                if (attackerState.IsValid && !attackerState.IsBot)
                {
                    _lastKillerOfPlayer[victimState.SteamId] = (attackerState.SteamId, now);
                }
            }

            if (attackerState.IsValid && !attackerState.IsBot && attackerState.SteamId != victimState.SteamId)
            {
                var attackerTeam = attackerState.Team;
                var (ctAlive, tAlive) = GetAliveCounts();
                var enemyAlive = attackerTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                var teammatesAlive = attackerTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;

                // HLTV Rating 3.0: Eco-Adjusted Kills
                decimal killWeight = Math.Clamp((decimal)victimState.EquipmentValue / 5000m, 0.2m, 1.0m);

                // Round Swing Impact: bonus for kills that change round outcome
                // Simplified: Entry frags, clutch kills
                if (_firstKillTimes.Count == 0) killWeight += 0.15m; // Entry kill bonus
                if (teammatesAlive == 1 && enemyAlive >= 1) killWeight += 0.1m; // Clutch kill bonus
                bool isRevengeKill = false;
                if (victimState.IsValid && _lastKillerOfPlayer.TryGetValue(attackerState.SteamId, out var lastKiller) && lastKiller.KillerId == victimState.SteamId)
                {
                    isRevengeKill = true;
                    _lastKillerOfPlayer.Remove(attackerState.SteamId);
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

                    _ = _positionPersistence.EnqueueAsync(new KillPositionEvent(
                        matchUuid,
                        attackerState.SteamId,
                        victimState.SteamId,
                        attackerState.Position.X,
                        attackerState.Position.Y,
                        attackerState.Position.Z,
                        victimState.Position.X,
                        victimState.Position.Y,
                        victimState.Position.Z,
                        weaponName,
                        isHeadshot,
                        penetrated,
                        killDistance,
                        (int)attackerTeam,
                        (int)victimTeam,
                        Server.MapName,
                        _currentRoundNumber,
                        (int)(now - _currentRoundStartUtc).TotalSeconds
                    ), CancellationToken.None);
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

                    if (!_firstKillTimes.ContainsKey(attackerState.SteamId)) 
                    { 
                        _firstKillTimes[attackerState.SteamId] = now; 
                        stats.Combat.FirstKills++; 
                    }
                    
                    _roundKills[attackerState.SteamId] = _roundKills.GetValueOrDefault(attackerState.SteamId, 0) + 1;
                    var killsThisRound = _roundKills[attackerState.SteamId];
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
                        if (victim != null && victim.PlayerPawn.Value != null)
                        {
                            var blindRemaining = victim.PlayerPawn.Value.FlashDuration;
                            stats.Utility.FlashAssistDuration += (int)(blindRemaining * 1000);
                        }
                    }
                    if (penetrated) stats.Combat.WallbangKills++;

                    if (isGrenadeKill)
                    {
                        stats.Combat.NadeKills++;
                        var nadeKillsThisRound = _roundKills[attackerState.SteamId];
                        if (nadeKillsThisRound >= 2) stats.Combat.MultiKillNades++;
                    }

                    bool highImpact = killsThisRound >= 3 || (teammatesAlive == 1 && enemyAlive >= 1) || (_firstKillTimes.Count == 1 && _firstKillTimes.ContainsKey(attackerState.SteamId));
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
            
            var victimState = PlayerControllerState.From(victim);
            var attackerState = PlayerControllerState.From(attacker);
            
            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var damage = @event.GetIntValue("dmg_health", 0);
            var hitgroup = @event.GetIntValue("hitgroup", 0);

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
            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                _playersAliveThisRound.Add(playerState.SteamId);
                _playerSessions.MutatePlayer(playerState.SteamId, stats => { stats.Round.SurvivedThisRound = true; stats.CurrentTeam = playerState.Team; });
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
                if (missedCount > 0) _playerSessions.MutatePlayer(kvp.Key, stats => stats.Round.TradeWindowsMissed += missedCount);
            }
            
            // Return current objects to pool and get fresh ones
            _hashSetPool.Return(_playersAliveThisRound);
            _dateTimeDictPool.Return(_lastDeathByPlayer);
            _dateTimeDictPool.Return(_firstKillTimes);
            _intDictPool.Return(_roundKills);
            _killerDictPool.Return(_lastKillerOfPlayer);
            _tradeDictPool.Return(_pendingTradeOpportunities);

            _playersAliveThisRound = _hashSetPool.Get();
            _lastDeathByPlayer = _dateTimeDictPool.Get();
            _firstKillTimes = _dateTimeDictPool.Get();
            _roundKills = _intDictPool.Get();
            _lastKillerOfPlayer = _killerDictPool.Get();
            _pendingTradeOpportunities = _tradeDictPool.Get();

            _lastTeamDeathTime[PlayerTeam.Terrorist] = DateTime.MinValue; 
            _lastTeamDeathTime[PlayerTeam.CounterTerrorist] = DateTime.MinValue;
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
