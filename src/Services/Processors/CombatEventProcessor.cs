using System;
using System.Collections.Generic;
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

    /// <summary>HP the player_hurt being dispatched removed from this victim (its dmg_health, capped at the HP they
    /// had). Valid for handlers that run after this processor's, which subscribes first.</summary>
    int GetHealthRemoved(int victimIndex, int reportedDamage);
}

public sealed class CombatEventProcessor : ICombatEventProcessor
{
    private static readonly HashSet<string> GrenadeWeaponNames =
    [
        "flashbang",
        "hegrenade",
        "smokegrenade",
        "molotov",
        "incgrenade",
        "inferno",
        "decoy",
        "tagrenade",
        "frag_grenade"
    ];

    // player_hurt reports these (no "weapon_" prefix) for grenade/fire damage. Used to attribute
    // utility damage and to keep gun-accuracy (ShotsHit) from being inflated by grenade hits.
    private static readonly HashSet<string> UtilityDamageWeapons =
    [
        "hegrenade",
        "molotov",
        "incgrenade",
        "inferno"
    ];

    private static readonly int MaxPlayers = 65; // CS2 max players + 1 for safety

    private readonly IPlayerSessionService _playerSessions;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly ILogger<CombatEventProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IDamageReportService _damageReport;

    private int _currentRoundNumber;
    private DateTime _currentRoundStartUtc;

    // EntityIndex-based state arrays for performance
    private readonly bool[] _isPlayerAlive = new bool[MaxPlayers];
    private readonly ulong[] _playerSteamIds = new ulong[MaxPlayers];
    // Last known team per entity index, so alive-count cleanup on disconnect does not depend on the
    // player session still existing (the lifecycle handler may have already removed it).
    private readonly PlayerTeam[] _playerTeams = new PlayerTeam[MaxPlayers];
    // Last known HP per entity index (0 = unknown), used to clamp player_hurt damage to what was actually removed.
    private readonly int[] _playerHealth = new int[MaxPlayers];
    private readonly int[] _playerRoundKills = new int[MaxPlayers];
    private readonly int[] _playerRoundNadeKills = new int[MaxPlayers];

    // Entry Kill Attempt tracking
    private bool _firstEngagementHappened;
    private int _firstEngagementAttackerIndex = -1;
    private int _firstEngagementVictimIndex = -1;

    // Tracks whether the first kill of the round has occurred (O(1) replacement for an array scan).
    private bool _firstKillHappened;

    // Per killer entity index (bots included): the player they last killed and when. Killing that enemy within
    // the trade window trades the death, however many other deaths happened in between.
    private readonly ulong[] _lastVictimOf = new ulong[MaxPlayers];
    private readonly DateTime[] _lastKillAt = new DateTime[MaxPlayers];
    private readonly Dictionary<PlayerTeam, (ulong SteamId, int Opponents)> _clutchCandidates = [];

    private int _ctAliveCount;
    private int _tAliveCount;
    private (int VictimIndex, int Damage) _lastHurt = (-1, 0);

    public CombatEventProcessor(
        IPlayerSessionService playerSessions,
        IOptionsMonitor<PluginConfig> config,
        ILogger<CombatEventProcessor> logger,
        TimeProvider timeProvider,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        IDamageReportService damageReport)
    {
        _playerSessions = playerSessions;
        _config = config;
        _logger = logger;
        _timeProvider = timeProvider;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _damageReport = damageReport;
    }

    public (int CtAlive, int TAlive) GetAliveCounts() => (_ctAliveCount, _tAliveCount);

    public int GetHealthRemoved(int victimIndex, int reportedDamage) =>
        _lastHurt.VictimIndex == victimIndex ? _lastHurt.Damage : reportedDamage;

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventPlayerDeath>((e, i) => { HandlePlayerDeath(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerHurt>((e, i) => { HandlePlayerHurt(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventWeaponFire>((e, i) => { HandleWeaponFire(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventRoundMvp>((e, i) => { HandleRoundMvp(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerAvengedTeammate>((e, i) => { HandlePlayerAvengedTeammate(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerSpawn>((e, i) => { HandlePlayerSpawn(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerDisconnect>((e, i) => { HandlePlayerDisconnected(e); return HookResult.Continue; });
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _currentRoundStartUtc = context.RoundStartUtc;
        ResetRoundStats();

        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            var player = Utilities.GetPlayerFromSteamId64(steamId);
            var state = PlayerControllerState.From(player);
            if (!state.IsValid || state.IsBot || player is null) continue;

            var index = (int)player.Index;
            if (index < 0 || index >= MaxPlayers) continue;

            _isPlayerAlive[index] = state.Health > 0;
            _playerHealth[index] = state.Health;
            _playerSteamIds[index] = state.SteamId;
            _playerTeams[index] = state.Team;
            if (!_isPlayerAlive[index]) continue;
            if (state.Team == PlayerTeam.CounterTerrorist) _ctAliveCount++;
            else if (state.Team == PlayerTeam.Terrorist) _tAliveCount++;
        }
    }

    private void ResetRoundStats()
    {
        Array.Clear(_isPlayerAlive);
        Array.Clear(_playerTeams);
        Array.Clear(_playerHealth);
        Array.Clear(_playerRoundKills);
        Array.Clear(_playerRoundNadeKills);

        _ctAliveCount = 0;
        _tAliveCount = 0;

        _firstEngagementHappened = false;
        _firstEngagementAttackerIndex = -1;
        _firstEngagementVictimIndex = -1;
        _firstKillHappened = false;

        Array.Clear(_lastVictimOf);
        _clutchCandidates.Clear();
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
            if (victimIndex < 0 || victimIndex >= MaxPlayers) return;

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

            var noscope = @event.GetBoolValue("noscope", false);
            // CS2 player_death keys are "thrusmoke"/"attackerblind" (no underscore); the underscored
            // forms silently returned false, leaving ThroughSmokeKills/BlindKills always 0.
            var thruSmoke = @event.GetBoolValue("thrusmoke", false);
            var attackerBlind = @event.GetBoolValue("attackerblind", false);
            var flashAssisted = @event.GetBoolValue("assistedflash", false);
            var penetrated = @event.GetIntValue("penetrated", 0) > 0;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var tradeWindowSeconds = _config.CurrentValue.TradeWindowSeconds;
            var victimTeam = victimState.Team;

            activity?.SetTag("victim.steamid", victimState.SteamId);
            activity?.SetTag("attacker.steamid", attackerState.SteamId);
            activity?.SetTag("weapon", weaponName);
            activity?.SetTag("headshot", isHeadshot);

            bool isGrenadeKill = GrenadeWeaponNames.Contains(NormalizeWeaponName(weaponName));
            // The entry duel is lost only to the player who opened it, as EntryKills counts it.
            bool killedByEngager = attacker is { IsValid: true } && (int)attacker.Index == _firstEngagementAttackerIndex;
            bool isFirstKillOfRound = !_firstKillHappened &&
                attackerState.IsValid && !attackerState.IsBot &&
                attackerState.SteamId != victimState.SteamId && attackerState.Team != victimTeam;

            if (victimState.IsValid && !victimState.IsBot)
            {
                _playerTeams[victimIndex] = victimTeam;

                var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
                // Round 0: no freeze end seen yet (loaded mid-round), so there is no round to attribute the position to.
                if (victimState.PawnHandle != 0 && _currentRoundNumber > 0 && _config.CurrentValue.EnablePositionTracking && !string.IsNullOrWhiteSpace(matchUuid))
                {
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

                    _positionPersistence.TryEnqueue(posEvent);
                }

                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Round.SurvivedThisRound = false;
                    stats.Combat.Deaths++;
                    stats.CurrentTeam = victimTeam;

                    // Entry Death tracking
                    if (_firstEngagementVictimIndex == victimIndex && killedByEngager)
                    {
                        stats.Combat.EntryDeaths++;
                    }
                });

                if (_isPlayerAlive[victimIndex])
                {
                    _isPlayerAlive[victimIndex] = false;
                    if (victimTeam == PlayerTeam.CounterTerrorist) _ctAliveCount--;
                    else if (victimTeam == PlayerTeam.Terrorist) _tAliveCount--;
                    CaptureClutchCandidate();
                }
            }

            if (attackerState.IsValid && !attackerState.IsBot && attackerState.SteamId != victimState.SteamId &&
                attackerState.Team != victimTeam && attackerState.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)
            {
                var attackerIndex = (int)attacker!.Index;
                if (attackerIndex < 0 || attackerIndex >= MaxPlayers) return;
                var attackerTeam = attackerState.Team;
                _playerTeams[attackerIndex] = attackerTeam;
                var (ctAlive, tAlive) = GetAliveCounts();
                var enemyAlive = attackerTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                var teammatesAlive = attackerTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;

                if (isFirstKillOfRound)
                {
                    _firstKillHappened = true;
                }

                // Counts are post-kill: the victim was a living enemy, so the kill that wins the clutch counts too.
                // A dead attacker (fire, grenade) is not the one clutching.
                bool isClutchKill = teammatesAlive == 1 && _isPlayerAlive[attackerIndex];

                // Round counters for this slot belong to the player who held it at the last reset; someone else in
                // the slot (a disconnect not delivered outside Live) starts from zero.
                if (_playerSteamIds[attackerIndex] != attackerState.SteamId)
                {
                    _playerSteamIds[attackerIndex] = attackerState.SteamId;
                    _playerRoundKills[attackerIndex] = 0;
                    _playerRoundNadeKills[attackerIndex] = 0;
                }

                var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
                if (attackerState.PawnHandle != 0 && victimState.PawnHandle != 0 && _currentRoundNumber > 0 && _config.CurrentValue.EnablePositionTracking && !string.IsNullOrWhiteSpace(matchUuid))
                {
                    var killDistance = @event.GetFloatValue("distance", 0f);
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

                    _positionPersistence.TryEnqueue(posEvent);
                }

                // Round Swing: how much this enemy kill shifted the attacker team's win probability,
                // using the alive-count win-probability table. (ctAlive/tAlive are post-kill counts;
                // the pre-kill state had one more player on the victim's team.) Credited at round end
                // only if the team wins the round.
                decimal swingDelta = 0m;
                if (!victimState.IsBot && attackerTeam != victimTeam && victimTeam is PlayerTeam.CounterTerrorist or PlayerTeam.Terrorist)
                {
                    var ctBefore = ctAlive;
                    var tBefore = tAlive;
                    if (victimTeam == PlayerTeam.CounterTerrorist) ctBefore++; else tBefore++;
                    var ctWinAfter = _matchTracker?.GetRoundWinProbability(ctAlive, tAlive) ?? 0f;
                    var ctWinBefore = _matchTracker?.GetRoundWinProbability(ctBefore, tBefore) ?? 0f;
                    var ctSwing = (decimal)(ctWinAfter - ctWinBefore);
                    swingDelta = attackerTeam == PlayerTeam.CounterTerrorist ? ctSwing : -ctSwing;
                }

                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    stats.Round.HadKillThisRound = true;
                    stats.Combat.Kills++;
                    if (swingDelta != 0m) stats.Combat.CurrentRoundSwing += swingDelta;
                    stats.Combat.Headshots += isHeadshot ? 1 : 0;
                    stats.Weapon.RecordKill(weaponName);
                    stats.CurrentTeam = attackerTeam;

                    if (isClutchKill) stats.Combat.ClutchKills++;

                    if (isFirstKillOfRound) stats.Combat.FirstKills++;

                    // Entry Kill tracking
                    if (_firstEngagementAttackerIndex == attackerIndex && _firstEngagementVictimIndex == victimIndex)
                    {
                        stats.Combat.EntryKills++;
                        stats.Combat.EntryKillAttemptWins++;
                    }

                    _playerRoundKills[attackerIndex]++;
                    var killsThisRound = _playerRoundKills[attackerIndex];

                    // Tiers are exact: a round moves up a tier with each kill, so a 3k is not also a 2k.
                    switch (killsThisRound)
                    {
                        case 2: stats.Combat.MultiKill2++; break;
                        case 3: stats.Combat.MultiKill2--; stats.Combat.MultiKill3++; break;
                        case 4: stats.Combat.MultiKill3--; stats.Combat.MultiKill4++; break;
                        case 5: stats.Combat.MultiKill4--; stats.Combat.MultiKill5++; break;
                    }

                    if (noscope) stats.Combat.Noscopes++;
                    if (thruSmoke) stats.Combat.ThroughSmokeKills++;
                    if (attackerBlind) stats.Combat.BlindKills++;
                    if (penetrated) stats.Combat.WallbangKills++;

                    if (isGrenadeKill)
                    {
                        stats.Combat.NadeKills++;
                        _playerRoundNadeKills[attackerIndex]++;
                        if (_playerRoundNadeKills[attackerIndex] == 2) stats.Combat.MultiKillNades++;
                    }

                    bool highImpact = killsThisRound >= 3 || isClutchKill || isFirstKillOfRound;
                    if (highImpact) stats.Combat.HighImpactKills++;
                    else if (teammatesAlive >= enemyAlive + 3) stats.Combat.LowImpactKills++;
                });

                // A trade: the enemy just killed had killed one of the attacker's teammates within the window.
                var tradedSteamId = _lastVictimOf[victimIndex];
                if (tradedSteamId != 0 && tradedSteamId != attackerState.SteamId &&
                    (now - _lastKillAt[victimIndex]).TotalSeconds <= tradeWindowSeconds)
                {
                    _playerSessions.MutatePlayer(attackerState.SteamId, stats => { stats.Combat.TradeKills++; stats.Round.DidTradeThisRound = true; });
                    _playerSessions.MutatePlayer(tradedSteamId, stats => { stats.Combat.TradedDeaths++; stats.Round.WasTradedThisRound = true; });
                    _lastVictimOf[victimIndex] = 0;
                }
            }

            if (victimState.IsValid && !victimState.IsBot && victimTeam is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)
            {
                if (isFirstKillOfRound)
                {
                    _playerSessions.MutatePlayer(victimState.SteamId, stats => stats.Combat.FirstDeaths++);
                }

                // Remember whom the enemy killer (a bot counts) took down, so killing it can trade this death.
                if (attacker is { IsValid: true } && (PlayerTeam)attacker.TeamNum != victimTeam && attacker.Index < MaxPlayers)
                {
                    _lastVictimOf[attacker.Index] = victimState.SteamId;
                    _lastKillAt[attacker.Index] = now;
                }
            }

            // The engine names the top damage dealer even when that is the victim's teammate, and does not score it.
            if (assisterState.IsValid && !assisterState.IsBot && assisterState.SteamId != victimState.SteamId &&
                assisterState.SteamId != attackerState.SteamId && assisterState.Team != victimTeam)
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

            var weapon = (@event.GetStringValue("weapon", "unknown") ?? "unknown").ToLowerInvariant();
            var damage = ClampToRemainingHealth(victimIndex, victimState.SteamId, @event.GetIntValue("dmg_health", 0), @event.GetIntValue("health", 0));
            _lastHurt = (victimIndex, damage);
            var damageArmor = @event.GetIntValue("dmg_armor", 0);
            var hitgroup = @event.GetIntValue("hitgroup", 0);
            var isUtilityDamage = UtilityDamageWeapons.Contains(weapon);

            // Entry Kill Attempt Tracking
            if (!_firstEngagementHappened && attackerState.IsValid && victimState.IsValid && attackerState.Team != victimState.Team && !attackerState.IsBot && !victimState.IsBot)
            {
                _firstEngagementHappened = true;
                _firstEngagementAttackerIndex = (int)attacker!.Index;
                _firstEngagementVictimIndex = victimIndex;

                _playerSessions.MutatePlayer(attackerState.SteamId, stats => stats.Combat.EntryKillAttempts++);
                _logger.LogDebug("First engagement of round {Round}: {Attacker} vs {Victim}", _currentRoundNumber, attackerState.PlayerName, victimState.PlayerName);
            }

            var isEnemyDamage = attackerState.IsValid && !attackerState.IsBot &&
                victimState.IsValid && !victimState.IsBot &&
                attackerState.SteamId != victimState.SteamId && attackerState.Team != victimState.Team &&
                attackerState.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist &&
                victimState.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist;

            if (isEnemyDamage)
            {
                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Combat.DamageTaken += damage;
                    if (isUtilityDamage) stats.Utility.UtilityDamageTaken += damage;
                });
            }

            if (isEnemyDamage)
            {
                _damageReport.RecordDamage(attackerState.SteamId, victimState.SteamId, damage);
                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    stats.Combat.DamageDealt += damage;
                    stats.Combat.DamageArmor += damageArmor;

                    if (isUtilityDamage)
                    {
                        // Utility damage is not a gun "hit"; keep it out of accuracy and weapon-hit stats.
                        stats.Utility.UtilityDamage += damage;
                        return;
                    }

                    stats.Combat.ShotsHit++;
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

            var playerState = PlayerControllerState.From(player);
            if (!playerState.IsValid || playerState.IsBot) return;

            var weapon = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var weaponLower = weapon.ToLowerInvariant();

            _playerSessions.MutatePlayer(playerState.SteamId, stats =>
            {
                // Grenade throws are not gun shots; counting them would inflate accuracy (ShotsFired
                // is the denominator for hit-rate, and a grenade can never register a ShotsHit). The
                // per-type thrown counts come from the dedicated grenade_thrown event
                // (UtilityEventProcessor); here we only exclude grenades from the gun-accuracy math.
                if (GrenadeWeaponNames.Contains(NormalizeWeaponName(weaponLower)))
                    return;

                stats.Combat.ShotsFired++;
                // weapon_fire reports "weapon_ak47"; player_hurt/player_death report "ak47". Key shots the same way
                // so a weapon's shots, hits and kills land in one match_weapon_stats row.
                stats.Weapon.RecordShot(NormalizeWeaponName(weaponLower));
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling weapon fire event"); }
    }

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
                // CS2 reasons (Valve hudwinpanel.js): 1 eliminations, 9 ace, 15/16 3k/4k; 2 plant, 3 defuse,
                // 13 plant and defend, 14 clutch defuse; 4 hostage rescue. 0 is undefined.
                switch (mvpReason)
                {
                    case 1 or 9 or 15 or 16: stats.Combat.MvpsEliminations++; break;
                    case 2 or 3 or 13 or 14: stats.Combat.MvpsBomb++; break;
                    case 4: stats.Combat.MvpsHostage++; break;
                }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling round MVP event"); }
    }

    private void HandlePlayerAvengedTeammate(EventPlayerAvengedTeammate @event)
    {
        try
        {
            var avenger = @event.AvengerId;
            var avengerState = PlayerControllerState.From(avenger);
            if (avengerState.IsValid && !avengerState.IsBot)
                _playerSessions.MutatePlayer(avengerState.SteamId, stats => stats.Combat.Revenges++);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player avenged teammate event"); }
    }

    // player_spawned is a CS:GO-era event CS2 does not fire; a player spawning after the freeze end (a joiner) would
    // otherwise stay out of the alive counts for the round.
    private void HandlePlayerSpawn(EventPlayerSpawn @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            if (player == null) return;
            var playerIndex = (int)player.Index;
            if (playerIndex < 0 || playerIndex >= MaxPlayers) return;

            var playerState = PlayerControllerState.From(player);
            if (playerState.IsValid && !playerState.IsBot)
            {
                if (!_isPlayerAlive[playerIndex])
                {
                    if (playerState.Team == PlayerTeam.CounterTerrorist) _ctAliveCount++;
                    else if (playerState.Team == PlayerTeam.Terrorist) _tAliveCount++;
                }

                _isPlayerAlive[playerIndex] = true;
                _playerHealth[playerIndex] = playerState.Health;
                _playerSteamIds[playerIndex] = playerState.SteamId;
                _playerTeams[playerIndex] = playerState.Team;
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
                    var team = _playerTeams[playerIndex];
                    if (team == PlayerTeam.CounterTerrorist) _ctAliveCount--;
                    else if (team == PlayerTeam.Terrorist) _tAliveCount--;
                }

                _isPlayerAlive[playerIndex] = false;
                _playerHealth[playerIndex] = 0;
                _lastVictimOf[playerIndex] = 0;
                _playerSteamIds[playerIndex] = 0;
                _playerTeams[playerIndex] = PlayerTeam.Spectator;
                _playerRoundKills[playerIndex] = 0;
                _playerRoundNadeKills[playerIndex] = 0;
                // A teammate leaving starts a 1vX as a death does.
                CaptureClutchCandidate();
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error handling player disconnected event"); }
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
        var winningTeam = winnerTeam switch { 2 => PlayerTeam.Terrorist, 3 => PlayerTeam.CounterTerrorist, _ => PlayerTeam.Spectator };

        // Round Swing is only credited to the team that won the round; everyone's per-round tally is
        // then cleared for the next round.
        _playerSessions.ForEachPlayer(stats =>
        {
            if (winningTeam != PlayerTeam.Spectator && stats.CurrentTeam == winningTeam)
                stats.Combat.CommitRoundSwing();
            stats.Combat.ResetRoundSwing();
        });

        UpdateClutchStats(winningTeam);
    }

    // A clutch is won when the clutcher's side wins the round, even if they died first (a plant that explodes). A
    // drawn or aborted round decides nothing.
    private void UpdateClutchStats(PlayerTeam winningTeam)
    {
        if (winningTeam == PlayerTeam.Spectator) return;
        try
        {
            foreach (var (team, candidate) in _clutchCandidates)
            {
                _playerSessions.MutatePlayer(candidate.SteamId, stats =>
                {
                    if (team == winningTeam)
                    {
                        var settings = _config.CurrentValue.ClutchSettings;
                        stats.Combat.ClutchesWon++;
                        stats.Combat.ClutchPoints += settings.BaseMultiplier + (candidate.Opponents * settings.DifficultyWeight);
                    }
                    else
                    {
                        stats.Combat.ClutchesLost++;
                    }
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error updating clutch statistics"); }
    }

    private void CaptureClutchCandidate()
    {
        CaptureClutchCandidate(PlayerTeam.CounterTerrorist, _ctAliveCount, _tAliveCount);
        CaptureClutchCandidate(PlayerTeam.Terrorist, _tAliveCount, _ctAliveCount);
    }

    private void CaptureClutchCandidate(PlayerTeam team, int alliesAlive, int opponentsAlive)
    {
        if (alliesAlive != 1 || opponentsAlive < 1 || _clutchCandidates.ContainsKey(team)) return;

        for (var i = 0; i < MaxPlayers; i++)
        {
            if (_isPlayerAlive[i] && _playerTeams[i] == team && _playerSteamIds[i] != 0)
            {
                _clutchCandidates[team] = (_playerSteamIds[i], opponentsAlive);
                return;
            }
        }
    }

    // player_hurt's dmg_health is not capped at the victim's remaining HP (a 110-damage headshot on a 30 HP
    // player reports 110). ADR counts only the HP actually removed, so clamp to the last known health.
    private int ClampToRemainingHealth(int victimIndex, ulong victimSteamId, int damage, int healthAfter)
    {
        if (victimIndex < 0 || victimIndex >= MaxPlayers) return damage;
        // HP recorded for a different player in this slot is not this victim's.
        var healthBefore = _playerSteamIds[victimIndex] == victimSteamId ? _playerHealth[victimIndex] : 0;
        _playerHealth[victimIndex] = healthAfter;
        return healthBefore > 0 ? Math.Min(damage, healthBefore) : damage;
    }

    private static string NormalizeWeaponName(string weapon) =>
        weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weapon[7..] : weapon;
}
