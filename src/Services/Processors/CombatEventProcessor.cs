using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
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
    private readonly PluginConfig _config;
    private readonly ILogger<CombatEventProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IDamageReportService _damageReport;

    private int _currentRoundNumber;
    private DateTime _currentRoundStartUtc;
    // Between round_end and the next freeze end: the round's survivors were counted at its end.
    private bool _roundEnded;

    // EntityIndex-based state arrays for performance. Bots are in them too (SteamID 0): they are the opponents of a
    // clutch, the first kill of a round and the teammates a trade avenges, but no stat credit goes to them.
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

    // Per killer entity index (bots included): the player they last killed (SteamID, 0 for a bot) and when (default:
    // nobody). Killing that enemy within the trade window trades the death, however many other deaths happened in between.
    private readonly ulong[] _lastVictimOf = new ulong[MaxPlayers];
    private readonly DateTime[] _lastKillAt = new DateTime[MaxPlayers];
    private readonly Dictionary<PlayerTeam, (ulong SteamId, int Opponents)> _clutchCandidates = [];

    private int _ctAliveCount;
    private int _tAliveCount;
    private (int VictimIndex, int Damage) _lastHurt = (-1, 0);

    public CombatEventProcessor(
        IPlayerSessionService playerSessions,
        PluginConfig config,
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
        _roundEnded = false;
        ResetRoundStats();

        // Everyone connected, bots included: a session is not needed to be alive.
        foreach (var player in Utilities.GetPlayers())
        {
            var state = PlayerControllerState.From(player);
            if (!state.IsValid) continue;

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
        Array.Clear(_lastKillAt);
        _clutchCandidates.Clear();
    }

    /// <summary>How a death is scored, as the engine's PlayerKilled scores it: the bomb's kill is not scored at all; a
    /// death with no scorer, or with the victim as the scorer, is a suicide; a bot is a scorer like anyone else (its
    /// kill is an enemy kill, the first of the round if nobody's came before), but no credit goes to a bot, so only a
    /// human's kill of a teammate is a team kill.</summary>
    internal static (bool Suicide, bool TeamKill, bool EnemyKill) ClassifyKill(
        PlayerControllerState attacker, PlayerControllerState victim, bool isSelfKill, bool killedByBomb)
    {
        if (killedByBomb) return (false, false, false);
        var suicide = !attacker.IsValid || isSelfKill;
        var teamKill = !suicide && !attacker.IsBot && victim.IsValid && attacker.Team == victim.Team &&
            victim.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist;
        var enemyKill = !suicide && attacker.Team != victim.Team &&
            attacker.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist;
        return (suicide, teamKill, enemyKill);
    }

    private void HandlePlayerDeath(EventPlayerDeath @event)
    {
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
            // Bots have no SteamID, so the killer is told apart from the victim by entity index.
            var attackerIndex = attackerState.IsValid ? (int)attacker!.Index : -1;
            if (attackerIndex >= MaxPlayers) return;
            var isSelfKill = attackerIndex == victimIndex;

            var weaponName = @event.GetStringValue("weapon", "unknown") ?? "unknown";
            var isHeadshot = @event.GetBoolValue("headshot", false);
            // The planter is the attacker of the bomb's kills; the engine scores none of it (PlayerKilled skips it).
            var killedByBomb = weaponName == "planted_c4";
            var (isSuicide, isTeamKill, isEnemyKill) = ClassifyKill(attackerState, victimState, isSelfKill, killedByBomb);

            // Handle Entry Kill tracking for insta-kills (no player_hurt event preceded this)
            if (!_firstEngagementHappened && isEnemyKill && victimState.IsValid)
            {
                _firstEngagementHappened = true;
                _firstEngagementAttackerIndex = attackerIndex;
                _firstEngagementVictimIndex = victimIndex;
                if (!attackerState.IsBot) _playerSessions.MutatePlayer(attackerState.SteamId, stats => stats.Combat.EntryKillAttempts++);
            }

            var noscope = @event.GetBoolValue("noscope", false);
            // CS2 player_death keys are "thrusmoke"/"attackerblind" (no underscore); the underscored
            // forms silently returned false, leaving ThroughSmokeKills/BlindKills always 0.
            var thruSmoke = @event.GetBoolValue("thrusmoke", false);
            var attackerBlind = @event.GetBoolValue("attackerblind", false);
            var flashAssisted = @event.GetBoolValue("assistedflash", false);
            var penetrated = @event.GetIntValue("penetrated", 0) > 0;
            // Meters, as player_death reports it.
            var killDistance = @event.GetFloatValue("distance", 0f);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var tradeWindowSeconds = _config.TradeWindowSeconds;
            var victimTeam = victimState.Team;

            bool isGrenadeKill = GrenadeWeaponNames.Contains(NormalizeWeaponName(weaponName));
            // The entry duel is lost only to the player who opened it, as EntryKills counts it.
            bool killedByEngager = attackerIndex >= 0 && attackerIndex == _firstEngagementAttackerIndex;
            // Whoever made it, a bot too: the kills after it are not the round's first.
            bool isFirstKillOfRound = !_firstKillHappened && isEnemyKill;
            if (isFirstKillOfRound) _firstKillHappened = true;

            if (victimState.IsValid)
            {
                _playerTeams[victimIndex] = victimTeam;

                if (!victimState.IsBot)
                {
                    var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
                    // Round 0: no freeze end seen yet (loaded mid-round), so there is no round to attribute the position to.
                    if (victimState.HasPawn && _currentRoundNumber > 0 && _config.EnablePositionTracking && !string.IsNullOrWhiteSpace(matchUuid))
                    {
                        _positionPersistence.TryEnqueue(new DeathPositionEvent
                        {
                            MatchUuid = matchUuid,
                            SteamId = victimState.SteamId,
                            X = victimState.Position.X,
                            Y = victimState.Position.Y,
                            Z = victimState.Position.Z,
                            CauseOfDeath = weaponName,
                            IsHeadshot = isHeadshot,
                            Team = (int)victimTeam,
                            MapName = Server.MapName,
                            RoundNumber = _currentRoundNumber,
                            RoundTime = (int)(now - _currentRoundStartUtc).TotalSeconds
                        });
                    }

                    _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                    {
                        // The round's survivors were counted at its end; a death after that is not this round's.
                        if (!_roundEnded) stats.Round.SurvivedThisRound = false;
                        stats.Combat.Deaths++;
                        stats.Weapon.RecordDeath(weaponName);
                        if (victimTeam == PlayerTeam.CounterTerrorist) stats.Combat.CtDeaths++;
                        else if (victimTeam == PlayerTeam.Terrorist) stats.Combat.TDeaths++;
                        if (isSuicide) stats.Combat.Suicides++;
                        stats.CurrentTeam = victimTeam;

                        // Entry Death tracking
                        if (_firstEngagementVictimIndex == victimIndex && killedByEngager)
                        {
                            stats.Combat.EntryDeaths++;
                        }
                    });
                }

                if (_isPlayerAlive[victimIndex])
                {
                    _isPlayerAlive[victimIndex] = false;
                    if (victimTeam == PlayerTeam.CounterTerrorist) _ctAliveCount--;
                    else if (victimTeam == PlayerTeam.Terrorist) _tAliveCount--;
                    CaptureClutchCandidate();
                }
            }

            if (isTeamKill) _playerSessions.MutatePlayer(attackerState.SteamId, stats => stats.Combat.TeamKills++);

            // The kill's credit, to a human only.
            if (isEnemyKill && !attackerState.IsBot)
            {
                var attackerTeam = attackerState.Team;
                _playerTeams[attackerIndex] = attackerTeam;
                var (ctAlive, tAlive) = GetAliveCounts();
                var enemyAlive = attackerTeam == PlayerTeam.CounterTerrorist ? tAlive : ctAlive;
                var teammatesAlive = attackerTeam == PlayerTeam.CounterTerrorist ? ctAlive : tAlive;

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
                if (attackerState.HasPawn && victimState.HasPawn && _currentRoundNumber > 0 && _config.EnablePositionTracking && !string.IsNullOrWhiteSpace(matchUuid))
                {
                    _positionPersistence.TryEnqueue(new KillPositionEvent
                    {
                        MatchUuid = matchUuid,
                        KillerSteamId = attackerState.SteamId,
                        VictimSteamId = victimState.SteamId,
                        KillerX = attackerState.Position.X,
                        KillerY = attackerState.Position.Y,
                        KillerZ = attackerState.Position.Z,
                        VictimX = victimState.Position.X,
                        VictimY = victimState.Position.Y,
                        VictimZ = victimState.Position.Z,
                        Weapon = weaponName,
                        IsHeadshot = isHeadshot,
                        IsWallbang = penetrated,
                        Distance = killDistance,
                        KillerTeam = (int)attackerTeam,
                        VictimTeam = (int)victimTeam,
                        MapName = Server.MapName,
                        RoundNumber = _currentRoundNumber,
                        RoundTime = (int)(now - _currentRoundStartUtc).TotalSeconds
                    });
                }

                // Round Swing: how much this enemy kill shifted the attacker team's win probability,
                // using the alive-count win-probability table. (ctAlive/tAlive are post-kill counts;
                // the pre-kill state had one more player on the victim's team.) Credited at round end
                // only if the team wins the round.
                decimal swingDelta = 0m;
                if (victimTeam is PlayerTeam.CounterTerrorist or PlayerTeam.Terrorist)
                {
                    var ctBefore = ctAlive;
                    var tBefore = tAlive;
                    if (victimTeam == PlayerTeam.CounterTerrorist) ctBefore++; else tBefore++;
                    var ctWinAfter = _matchTracker.GetRoundWinProbability(ctAlive, tAlive);
                    var ctWinBefore = _matchTracker.GetRoundWinProbability(ctBefore, tBefore);
                    var ctSwing = (decimal)(ctWinAfter - ctWinBefore);
                    swingDelta = attackerTeam == PlayerTeam.CounterTerrorist ? ctSwing : -ctSwing;
                }

                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    stats.Round.HadKillThisRound = true;
                    stats.Combat.Kills++;
                    if (attackerTeam == PlayerTeam.CounterTerrorist) stats.Combat.CtKills++; else stats.Combat.TKills++;
                    if (stats.Round.IsPistolRound) stats.Combat.PistolRoundKills++;
                    if (killDistance > 0f) stats.Combat.RecordKillDistance((decimal)killDistance);
                    if (swingDelta != 0m) stats.Combat.CurrentRoundSwing += swingDelta;
                    stats.Combat.Headshots += isHeadshot ? 1 : 0;
                    stats.Weapon.RecordKill(weaponName, isHeadshot);
                    stats.CurrentTeam = attackerTeam;

                    if (isClutchKill) stats.Combat.ClutchKills++;

                    if (isFirstKillOfRound)
                    {
                        stats.Combat.FirstKills++;
                        if (attackerTeam == PlayerTeam.CounterTerrorist) stats.Combat.CtFirstKills++; else stats.Combat.TFirstKills++;
                    }

                    // Entry Kill tracking
                    if (_firstEngagementAttackerIndex == attackerIndex && _firstEngagementVictimIndex == victimIndex)
                    {
                        stats.Combat.EntryKills++;
                        stats.Combat.EntryKillAttemptWins++;
                    }

                    _playerRoundKills[attackerIndex]++;
                    var killsThisRound = _playerRoundKills[attackerIndex];

                    // Tiers are exact: a round moves up a tier with each kill, so a 3k is not also a 2k (or a 1k).
                    stats.Combat.RecordRoundKill(killsThisRound);

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

                // A trade: the enemy just killed had killed one of the attacker's teammates (a bot too) within the window.
                var tradedSteamId = _lastVictimOf[victimIndex];
                if (_lastKillAt[victimIndex] != default && tradedSteamId != attackerState.SteamId &&
                    (now - _lastKillAt[victimIndex]).TotalSeconds <= tradeWindowSeconds)
                {
                    _playerSessions.MutatePlayer(attackerState.SteamId, stats => { stats.Combat.TradeKills++; stats.Round.DidTradeThisRound = true; });
                    if (tradedSteamId != 0) _playerSessions.MutatePlayer(tradedSteamId, stats => { stats.Combat.TradedDeaths++; stats.Round.WasTradedThisRound = true; });
                    _lastVictimOf[victimIndex] = 0;
                    _lastKillAt[victimIndex] = default;
                }
            }

            if (victimState.IsValid && victimTeam is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)
            {
                if (isFirstKillOfRound && !victimState.IsBot)
                {
                    _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                    {
                        stats.Combat.FirstDeaths++;
                        if (victimTeam == PlayerTeam.CounterTerrorist) stats.Combat.CtFirstDeaths++; else stats.Combat.TFirstDeaths++;
                    });
                }

                // Remember whom the enemy killer (a bot counts, as the victim) took down, so killing it can trade this death.
                if (isEnemyKill)
                {
                    _lastVictimOf[attackerIndex] = victimState.SteamId;
                    _lastKillAt[attackerIndex] = now;
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
            // Bots have no SteamID, so self-damage is told by entity index.
            var isSelfDamage = attackerState.IsValid && attacker!.Index == victim.Index;

            var weapon = (@event.GetStringValue("weapon", "unknown") ?? "unknown").ToLowerInvariant();
            var damage = ClampToRemainingHealth(victimIndex, victimState.SteamId, @event.GetIntValue("dmg_health", 0), @event.GetIntValue("health", 0));
            _lastHurt = (victimIndex, damage);
            var damageArmor = @event.GetIntValue("dmg_armor", 0);
            var hitgroup = @event.GetIntValue("hitgroup", 0);
            var isUtilityDamage = UtilityDamageWeapons.Contains(weapon);

            // Entry Kill Attempt Tracking (a bot opens the duel too; the attempt is credited to a human only)
            if (!_firstEngagementHappened && attackerState.IsValid && victimState.IsValid && attackerState.Team != victimState.Team)
            {
                _firstEngagementHappened = true;
                _firstEngagementAttackerIndex = (int)attacker!.Index;
                _firstEngagementVictimIndex = victimIndex;

                if (!attackerState.IsBot) _playerSessions.MutatePlayer(attackerState.SteamId, stats => stats.Combat.EntryKillAttempts++);
                _logger.LogDebug("First engagement of round {Round}: {Attacker} vs {Victim}", _currentRoundNumber, attackerState.PlayerName, victimState.PlayerName);
            }

            // Damage between enemies, bots included: a bot's is taken, and damage to a bot is dealt.
            var isEnemyDamage = attackerState.IsValid && victimState.IsValid && !isSelfDamage &&
                attackerState.Team != victimState.Team &&
                attackerState.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist &&
                victimState.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist;

            // Friendly fire: HP removed from a teammate, kept apart from the damage dealt.
            if (attackerState.IsValid && !attackerState.IsBot && victimState.IsValid &&
                !isSelfDamage && attackerState.Team == victimState.Team &&
                attackerState.Team is PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)
            {
                _playerSessions.MutatePlayer(attackerState.SteamId, stats => stats.Combat.TeamDamage += damage);
            }

            if (isEnemyDamage && !victimState.IsBot)
            {
                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Combat.DamageTaken += damage;
                    if (isUtilityDamage) stats.Utility.UtilityDamageTaken += damage;
                });
            }

            if (isEnemyDamage && !attackerState.IsBot)
            {
                _damageReport.RecordDamage(attackerState.SteamId, victimState.SteamId, damage);
                _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
                {
                    stats.Combat.DamageDealt += damage;
                    stats.Combat.DamageArmor += damageArmor;
                    if (attackerState.Team == PlayerTeam.CounterTerrorist) stats.Combat.CtDamage += damage; else stats.Combat.TDamage += damage;
                    stats.Weapon.RecordDamage(weapon, damage);

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
            if (!playerState.IsValid) return;

            if (!_isPlayerAlive[playerIndex])
            {
                if (playerState.Team == PlayerTeam.CounterTerrorist) _ctAliveCount++;
                else if (playerState.Team == PlayerTeam.Terrorist) _tAliveCount++;
            }

            _isPlayerAlive[playerIndex] = true;
            _playerHealth[playerIndex] = playerState.Health;
            _playerSteamIds[playerIndex] = playerState.SteamId;
            _playerTeams[playerIndex] = playerState.Team;
            // The respawn for the next round comes before round_start, while the ended round's rows are still to be
            // re-sent: it must not mark the dead as survivors. Freeze end marks who starts the new round.
            if (!playerState.IsBot)
                _playerSessions.MutatePlayer(playerState.SteamId, stats => { if (!_roundEnded) stats.Round.SurvivedThisRound = true; stats.CurrentTeam = playerState.Team; });
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
                _lastKillAt[playerIndex] = default;
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
        _roundEnded = true;
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
                    stats.Combat.RecordClutch(candidate.Opponents, team == winningTeam);
                    if (team == winningTeam)
                    {
                        var settings = _config.ClutchSettings;
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

    // A lone bot (SteamID 0) is never a candidate: there is no session to credit. Opponents are counted bots included.
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
