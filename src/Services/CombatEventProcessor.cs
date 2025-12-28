using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public interface ICombatEventProcessor
{
    void SetRoundContext(int roundNumber, DateTime roundStartUtc, int ctAlive, int tAlive);
    void HandlePlayerDeath(EventPlayerDeath @event);
    void HandlePlayerHurt(EventPlayerHurt @event);
    void HandleWeaponFire(EventWeaponFire @event);
    void HandleBulletImpact(EventBulletImpact @event);
    void HandleRoundMvp(EventRoundMvp @event);
    void HandlePlayerAvengedTeammate(EventPlayerAvengedTeammate @event);
    void HandlePlayerSpawned(EventPlayerSpawned @event);
    void ResetRoundStats();
    void UpdateClutchStats(PlayerTeam winningTeam);
    (int CtAlive, int TAlive) GetAliveCounts();
}

public sealed class CombatEventProcessor(
    IPlayerSessionService playerSessions,
    IOptionsMonitor<PluginConfig> config,
    ILogger<CombatEventProcessor> logger,
    TimeProvider timeProvider) : ICombatEventProcessor
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

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

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

    public void SetRoundContext(int roundNumber, DateTime roundStartUtc, int ctAlive, int tAlive)
    {
        _currentRoundNumber = roundNumber;
        _currentRoundStartUtc = roundStartUtc;
        _ctAliveAtStart = ctAlive;
        _tAliveAtStart = tAlive;
    }

    private PlayerTeam GetTeam(CCSPlayerController? player)
    {
        if (player is not { IsValid: true })
        {
            return PlayerTeam.Spectator;
        }

        return player.TeamNum switch
        {
            2 => PlayerTeam.Terrorist,
            3 => PlayerTeam.CounterTerrorist,
            _ => PlayerTeam.Spectator
        };
    }

    public void HandlePlayerDeath(EventPlayerDeath @event)
    {
        try
        {
            var victim = @event.GetPlayerOrDefault("userid");
            var attacker = @event.GetPlayerOrDefault("attacker");
            var assister = @event.GetPlayerOrDefault("assister");
            var weaponName = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var isHeadshot = @event.GetBoolValue("headshot", false);
            var damage = @event.GetIntValue("dmg_health", 0);
            
            // New CS2 specific fields
            var noscope = @event.GetBoolValue("noscope", false);
            var thruSmoke = @event.GetBoolValue("thru_smoke", false);
            var attackerBlind = @event.GetBoolValue("attacker_blind", false);
            var flashAssisted = @event.GetBoolValue("assistedflash", false);
            var penetrated = @event.GetIntValue("penetrated", 0) > 0;
            var distance = @event.GetFloatValue("distance", 0f);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var tradeWindowSeconds = config.CurrentValue.TradeWindowSeconds;
            var victimTeam = GetTeam(victim);

            if (victim is { IsBot: false })
            {
                playerSessions.MutatePlayer(victim.SteamID, stats =>
                {
                    stats.RoundNumber = _currentRoundNumber;
                    stats.RoundStartUtc = _currentRoundStartUtc;
                    stats.SurvivedThisRound = false;
                    stats.Deaths++;
                    stats.CurrentRoundDeaths++;
                    stats.CurrentTeam = victimTeam;
                });

                // Enhanced Trade Opportunity Logic: Proximity-based
                var victimPos = victim.PlayerPawn.Value?.AbsOrigin;
                if (victimPos != null)
                {
                    var teammates = playerSessions.GetActiveSteamIds()
                        .Where(id => id != victim.SteamID)
                        .ToList();
                    
                    foreach (var teammateId in teammates)
                    {
                        var teammate = Utilities.GetPlayerFromSteamId(teammateId);
                        if (teammate is { IsValid: true, PlayerPawn.Value: not null } 
                            && teammate.TeamNum == (int)victimTeam
                            && _playersAliveThisRound.Contains(teammateId))
                        {
                            var teammatePos = teammate.PlayerPawn.Value.AbsOrigin;
                            if (teammatePos != null)
                            {
                                var teammateDistance = CalculateDistance(victimPos, teammatePos);
                                // If teammate is within threshold (default 1000 units), it's an opportunity
                                if (teammateDistance <= config.CurrentValue.TradeDistanceThreshold)
                                {
                                    playerSessions.MutatePlayer(teammateId, stats =>
                                    {
                                        stats.TradeOpportunities++;
                                    });
                                }
                            }
                        }
                    }
                }

                _playersAliveThisRound.Remove(victim.SteamID);
                _lastTeamDeathTime[victimTeam] = now;
                _lastDeathByPlayer[victim.SteamID] = now;
            }

            // Update attacker stats
            if (attacker is { IsBot: false } && attacker != victim)
            {
                var attackerTeam = GetTeam(attacker);
                var (ctAlive, tAlive) = GetAliveCounts();
                var enemyAlive = attackerTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                var teammatesAlive = attackerTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;

                playerSessions.MutatePlayer(attacker.SteamID, stats =>
                {
                    stats.RoundNumber = _currentRoundNumber;
                    stats.RoundStartUtc = _currentRoundStartUtc;
                    stats.HadKillThisRound = true;
                    stats.Kills++;
                    stats.CurrentRoundKills++;
                    stats.DamageDealt += damage;
                    stats.Headshots += isHeadshot ? 1 : 0;
                    stats.WeaponKills[weaponName] = stats.WeaponKills.GetValueOrDefault(weaponName, 0) + 1;
                    stats.CurrentTeam = attackerTeam;

                    // High/Low Impact Logic
                    bool isHighImpact = false;
                    
                    // 1. Entry Kill
                    if (!_firstKillTimes.ContainsKey(attacker.SteamID))
                    {
                        _firstKillTimes[attacker.SteamID] = now;
                        stats.EntryKills++;
                        isHighImpact = true;
                    }

                    // 2. Multi-kill impact (3rd, 4th, 5th kills are high impact)
                    _roundKills[attacker.SteamID] = _roundKills.GetValueOrDefault(attacker.SteamID, 0) + 1;
                    var currentRoundKills = _roundKills[attacker.SteamID];
                    if (currentRoundKills >= 3) isHighImpact = true;
                    
                    if (currentRoundKills > stats.MultiKills)
                        stats.MultiKills = currentRoundKills;

                    // CS2 Specific Kill Metrics
                    if (noscope) stats.NoscopeKills++;
                    if (thruSmoke) stats.ThruSmokeKills++;
                    if (attackerBlind) stats.AttackerBlindKills++;
                    if (flashAssisted) stats.FlashAssistedKills++;
                    if (penetrated) stats.WallbangKills++;

                    // 3. Clutch kill (last man standing)
                    if (teammatesAlive == 1 && enemyAlive >= 1) isHighImpact = true;

                    // 4. Opening duel win
                    if (_firstKillTimes.Count == 1 && _firstKillTimes.ContainsKey(attacker.SteamID))
                    {
                        stats.OpeningDuelsWon++;
                        isHighImpact = true;
                    }

                    // Low Impact detection (hunting when up significantly)
                    if (teammatesAlive >= enemyAlive + 3 && currentRoundKills < 3)
                    {
                        stats.LowImpactKills++;
                    }
                    else if (isHighImpact)
                    {
                        stats.HighImpactKills++;
                    }

                });

                // Trade detection: if victim team lost a teammate within window
                if (victimTeam is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)
                {
                    if (_lastTeamDeathTime.TryGetValue(victimTeam, out var lastDeath)
                        && lastDeath > DateTime.MinValue
                        && lastDeath != now
                        && (now - lastDeath).TotalSeconds <= tradeWindowSeconds)
                    {
                        playerSessions.MutatePlayer(attacker.SteamID, stats =>
                        {
                            stats.TradeKills++;
                            stats.DidTradeThisRound = true;
                        });

                        if (victim is { IsBot: false })
                        {
                            playerSessions.MutatePlayer(victim.SteamID, stats =>
                            {
                                stats.TradedDeaths++;
                                stats.WasTradedThisRound = true;
                            });
                        }
                    }
                }

                logger.LogDebug("Player {AttackerSteamId} killed {VictimSteamId} with {Weapon} (Headshot: {Headshot})", 
                    attacker.SteamID, victim?.SteamID, weaponName, isHeadshot);
            }

            // Update assister stats
            if (assister is { IsBot: false } && assister != victim && assister != attacker)
            {
                var assisterTeam = GetTeam(assister);
                playerSessions.MutatePlayer(assister.SteamID, stats =>
                {
                    stats.RoundNumber = _currentRoundNumber;
                    stats.RoundStartUtc = _currentRoundStartUtc;
                    stats.HadAssistThisRound = true;
                    stats.Assists++;
                    stats.CurrentTeam = assisterTeam;
                    
                    // Track flash assists (could be enhanced with utility tracking)
                    if (weaponName.Contains("flashbang"))
                    {
                        stats.FlashAssists++;
                    }
                });

                logger.LogDebug("Player {AssisterSteamId} assisted in kill of {VictimSteamId}", assister.SteamID, victim?.SteamID);
            }

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling player death event");
        }
    }

    public void HandlePlayerHurt(EventPlayerHurt @event)
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
                playerSessions.MutatePlayer(victim.SteamID, stats =>
                {
                    stats.DamageTaken += damage;
                    stats.DamageArmor += damageArmor;
                });
            }

            if (attacker is { IsBot: false } && attacker != victim)
            {
                playerSessions.MutatePlayer(attacker.SteamID, stats =>
                {
                    stats.DamageDealt += damage;
                    stats.DamageArmor += damageArmor;
                    
                    // Track weapon hits
                    stats.WeaponHits[weapon] = stats.WeaponHits.GetValueOrDefault(weapon, 0) + 1;
                    
                    // Track hit groups for accuracy analysis
                    switch (hitgroup)
                    {
                        case 1:
                            stats.HeadshotsHit++;
                            break;
                        case 2:
                            stats.ChestHits++;
                            break;
                        case 3:
                            stats.StomachHits++;
                            break;
                        case 4:
                        case 5:
                            stats.ArmHits++;
                            break;
                        case 6:
                        case 7:
                            stats.LegHits++;
                            break;
                    }
                });
            }

            logger.LogTrace("Player {PlayerSteamId} took {Damage} damage from {AttackerSteamId} with {Weapon}", 
                victim?.SteamID, damage, attacker?.SteamID, weapon);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling player hurt event");
        }
    }

    public void HandleWeaponFire(EventWeaponFire @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is not { IsBot: false, IsValid: true }) return;

            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var weaponLower = weapon.ToLowerInvariant();

            playerSessions.MutatePlayer(player.SteamID, stats =>
            {
                stats.ShotsFired++;
                stats.CurrentRoundShotsFired++;

                if (!stats.WeaponShots.ContainsKey(weapon))
                {
                    stats.WeaponShots[weapon] = 0;
                }
                stats.WeaponShots[weapon]++;

                // Track grenade throws via weapon type
                if (GrenadeWeaponNames.Contains(weaponLower))
                {
                    stats.GrenadesThrown++;

                    switch (weaponLower)
                    {
                        case "weapon_flashbang":
                            stats.FlashesThrown++;
                            break;
                        case "weapon_smokegrenade":
                            stats.SmokesThrown++;
                            break;
                        case "weapon_molotov":
                        case "weapon_incgrenade":
                            stats.MolotovsThrown++;
                            break;
                        case "weapon_hegrenade":
                            stats.HeGrenadesThrown++;
                            break;
                        case "weapon_decoy":
                            stats.DecoysThrown++;
                            break;
                    }
                }
            });

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Player {SteamId} fired {Weapon}", player.SteamID, weapon);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling weapon fire event");
        }
    }

    public void HandleBulletImpact(EventBulletImpact @event)
    {
        try
        {
            var x = @event.GetFloatValue("x", 0);
            var y = @event.GetFloatValue("y", 0);
            var z = @event.GetFloatValue("z", 0);

            // Bullet impact events can be used for advanced statistics like wallbang detection
            // For now, we just log them for potential future analysis
            logger.LogTrace("Bullet impact at X:{X} Y:{Y} Z:{Z}", x, y, z);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling bullet impact event");
        }
    }

    public void HandleRoundMvp(EventRoundMvp @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var mvpReason = @event.GetIntValue("reason", 0);

            if (player is { IsBot: false })
            {
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.Mvps++;
                    
                    // Track MVP reasons (0 = most eliminations, 1 = bomb plant/defuse, etc.)
                    switch (mvpReason)
                    {
                        case 0:
                            stats.MvpsEliminations++;
                            break;
                        case 1:
                            stats.MvpsBomb++;
                            break;
                        case 2:
                            stats.MvpsHostage++;
                            break;
                    }
                });

                logger.LogDebug("Player {SteamId} received MVP (Reason: {Reason})", player.SteamID, mvpReason);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling round MVP event");
        }
    }

    public void HandlePlayerAvengedTeammate(EventPlayerAvengedTeammate @event)
    {
        try
        {
            var avenger = @event.GetPlayerOrDefault("avenger_userid");
            var avengedPlayer = @event.GetPlayerOrDefault("avenged_userid");
            var killedPlayer = @event.GetPlayerOrDefault("killed_userid");

            if (avenger is { IsBot: false })
            {
                playerSessions.MutatePlayer(avenger.SteamID, stats =>
                {
                    stats.Revenges++;
                });

                logger.LogDebug("Player {AvengerSteamId} avenged teammate {AvengedSteamId} by killing {KilledSteamId}", 
                    avenger.SteamID, avengedPlayer?.SteamID, killedPlayer?.SteamID);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling player avenged teammate event");
        }
    }

    public void HandlePlayerSpawned(EventPlayerSpawned @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player is { IsBot: false })
            {
                var team = GetTeam(player);

                // Add to alive players tracking
                _playersAliveThisRound.Add(player.SteamID);
                playerSessions.MutatePlayer(player.SteamID, stats =>
                {
                    stats.SurvivedThisRound = true;
                    stats.CurrentTeam = team;
                });

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Player {SteamId} spawned", player.SteamID);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling player spawned event");
        }
    }

    public void ResetRoundStats()
    {
        try
        {
            _firstKillTimes.Clear();
            _roundKills.Clear();
            _playersAliveThisRound.Clear();
            _lastDeathByPlayer.Clear();
            _lastTeamDeathTime[PlayerTeam.Terrorist] = DateTime.MinValue;
            _lastTeamDeathTime[PlayerTeam.CounterTerrorist] = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resetting round statistics");
        }
    }

    public void UpdateClutchStats(PlayerTeam winningTeam)
    {
        try
        {
            var (ctAlive, tAlive) = GetAliveCounts();

            foreach (var playerId in _playersAliveThisRound)
            {
                playerSessions.MutatePlayer(playerId, stats =>
                {
                    var team = stats.CurrentTeam;
                    var teamAlive = team == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;
                    var enemyAlive = team == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;

                    if (teamAlive == 1 && enemyAlive >= 1)
                    {
                        if (team == winningTeam)
                        {
                            var settings = config.CurrentValue.ClutchSettings;
                            var clutchImpact = settings.BaseMultiplier + (enemyAlive * settings.DifficultyWeight);
                            
                            stats.ClutchesWon++;
                            stats.ClutchPoints += clutchImpact;
                        }
                        else
                        {
                            stats.ClutchesLost++;
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating clutch statistics");
        }
    }

    public (int CtAlive, int TAlive) GetAliveCounts()
    {
        var ctAlive = 0;
        var tAlive = 0;
        foreach (var playerId in _playersAliveThisRound)
        {
            var team = playerSessions.WithPlayer(playerId, ps => ps.CurrentTeam, PlayerTeam.Spectator);
            if (team == PlayerTeam.CounterTerrorist) ctAlive++;
            else if (team == PlayerTeam.Terrorist) tAlive++;
        }

        return (ctAlive, tAlive);
    }

    private static float CalculateDistance(Vector pos1, Vector pos2)
    {
        var dx = pos2.X - pos1.X;
        var dy = pos2.Y - pos1.Y;
        var dz = pos2.Z - pos1.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
