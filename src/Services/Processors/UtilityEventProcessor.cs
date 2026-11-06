using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public sealed class UtilityEventProcessor : IEventProcessor
{
    private const string RoundEndEventName = "round_end";
    private const float InfernoAssociationRadiusSquared = 256f * 256f;
    private static readonly TimeSpan InfernoAssociationWindow = TimeSpan.FromSeconds(3);

    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<UtilityEventProcessor> _logger;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;
    private readonly PluginConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly ICombatEventProcessor _combat;
    private readonly List<PendingGrenade> _pendingGrenades = [];

    private DateTime _roundStartUtc;
    private int _currentRoundNumber;

    public UtilityEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<UtilityEventProcessor> logger,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        PluginConfig config,
        TimeProvider timeProvider,
        ICombatEventProcessor combat)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _config = config;
        _timeProvider = timeProvider;
        _combat = combat;
    }

    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        dispatcher.Subscribe<EventGrenadeThrown>((e, i) => { HandleGrenadeThrown(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerBlind>((e, i) => { HandlePlayerBlind(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventPlayerHurt>((e, i) => { HandlePlayerHurt(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventHegrenadeDetonate>((e, i) => { TrackDetonation(e, UtilityType.HeGrenade); return HookResult.Continue; });
        dispatcher.Subscribe<EventFlashbangDetonate>((e, i) => { TrackDetonation(e, UtilityType.Flash); return HookResult.Continue; });
        dispatcher.Subscribe<EventSmokegrenadeDetonate>((e, i) => { TrackDetonation(e, UtilityType.Smoke); return HookResult.Continue; });
        dispatcher.Subscribe<EventMolotovDetonate>((e, i) => { TrackDetonation(e, UtilityType.Molotov); return HookResult.Continue; });
        dispatcher.Subscribe<EventDecoyDetonate>((e, i) => { TrackDetonation(e, UtilityType.Decoy); return HookResult.Continue; });
        dispatcher.Subscribe<EventSmokegrenadeExpired>((e, i) => { HandleSmokeExpired(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventInfernoStartburn>((e, i) => { HandleInfernoStarted(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventInfernoExpire>((e, i) => { HandleInfernoEnded(e); return HookResult.Continue; });
        dispatcher.Subscribe<EventInfernoExtinguish>((e, i) => { HandleInfernoEnded(e); return HookResult.Continue; });
    }

    public void OnRoundStart(RoundContext context)
    {
        _currentRoundNumber = context.RoundNumber;
        _roundStartUtc = context.RoundStartUtc;
        _pendingGrenades.Clear();
    }

    public void OnRoundEnd(int winnerTeam, int winReason)
    {
        foreach (var pending in _pendingGrenades.Where(grenade => grenade.Detonated))
        {
            FinalizePending(pending, RoundEndEventName);
        }
        _pendingGrenades.Clear();
    }

    private void HandleGrenadeThrown(EventGrenadeThrown @event)
    {
        try
        {
            var player = @event.GetPlayerOrDefault("userid");
            var state = PlayerControllerState.From(player);
            if (!state.IsValid || state.IsBot) return;

            var type = ParseUtilityType(@event.GetStringValue("weapon", string.Empty));
            if (type == UtilityType.Unknown) return;

            _pendingGrenades.Add(new PendingGrenade(state.SteamId, state.Team, type, state.GrenadeThrowPosition));
            _playerSessions.MutatePlayer(state.SteamId, stats =>
            {
                switch (type)
                {
                    case UtilityType.Flash: stats.Utility.FlashbangsThrown++; break;
                    case UtilityType.Smoke: stats.Utility.SmokesThrown++; break;
                    case UtilityType.Molotov: stats.Utility.MolotovsThrown++; break;
                    case UtilityType.HeGrenade: stats.Utility.HeGrenadesThrown++; break;
                    case UtilityType.Decoy: stats.Utility.DecoysThrown++; break;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling grenade thrown event");
        }
    }

    private void HandlePlayerBlind(EventPlayerBlind @event)
    {
        try
        {
            var victim = @event.GetPlayerOrDefault("userid");
            var victimState = PlayerControllerState.From(victim);
            var attackerState = PlayerControllerState.From(@event.GetPlayerOrDefault("attacker"));
            var blindDuration = @event.GetFloatValue("blind_duration", 0f);
            // The engine blinds dead players and spectators in range too; that flashes nobody.
            if (victimState.Health <= 0 || victimState.Team is not (PlayerTeam.Terrorist or PlayerTeam.CounterTerrorist)) return;

            if (!victimState.IsBot)
            {
                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Utility.TotalBlindTime += blindDuration;
                    stats.Utility.TimesBlinded++;
                });
            }

            if (!attackerState.IsValid || attackerState.IsBot || attackerState.SteamId == victimState.SteamId) return;

            var pending = FindPending(attackerState.SteamId, UtilityType.Flash, @event.GetIntValue("entityid"));
            pending?.RecordAffected(victimState, (int)victim!.Index);

            _playerSessions.MutatePlayer(attackerState.SteamId, stats =>
            {
                if (!victimState.IsValid) return;
                if (attackerState.Team != victimState.Team)
                {
                    stats.Utility.EnemiesBlinded++;
                    stats.Utility.TotalBlindTimeInflicted += blindDuration;
                }
                else
                {
                    stats.Utility.TeammatesBlinded++;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling player blind event");
        }
    }

    private void HandlePlayerHurt(EventPlayerHurt @event)
    {
        try
        {
            var victim = @event.GetPlayerOrDefault("userid");
            var attackerState = PlayerControllerState.From(@event.GetPlayerOrDefault("attacker"));
            var victimState = PlayerControllerState.From(victim);
            if (!attackerState.IsValid || attackerState.IsBot || !victimState.IsValid) return;

            var type = ParseUtilityType(@event.GetStringValue("weapon", string.Empty));
            if (type is not (UtilityType.HeGrenade or UtilityType.Molotov)) return;

            var pending = FindPending(attackerState.SteamId, type, 0);
            if (pending is null) return;
            pending.RecordAffected(victimState, (int)victim!.Index);
            if (victimState.Team != pending.OwnerTeam)
            {
                // Overkill is not damage: a lethal 98 on a 20 HP player removed 20, as ADR counts it.
                pending.Damage += _combat.GetHealthRemoved((int)victim!.Index, @event.GetIntValue("dmg_health"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling utility damage event");
        }
    }

    private void TrackDetonation(GameEvent @event, UtilityType type)
    {
        try
        {
            var state = PlayerControllerState.From(@event.GetPlayerOrDefault("userid"));
            if (!state.IsValid || state.IsBot) return;

            // Oldest first: two same-type grenades in flight detonate in throw order.
            var pending = _pendingGrenades.FirstOrDefault(grenade =>
                grenade.OwnerSteamId == state.SteamId && grenade.Type == type && !grenade.Detonated);
            if (pending is null)
            {
                pending = new PendingGrenade(state.SteamId, state.Team, type, state.GrenadeThrowPosition);
                _pendingGrenades.Add(pending);
            }

            pending.EntityId = @event.GetIntValue("entityid");
            pending.LandPosition = new Vector3(
                @event.GetFloatValue("x"),
                @event.GetFloatValue("y"),
                @event.GetFloatValue("z"));
            pending.DetonatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            pending.LifecycleStartedAt = pending.DetonatedAt;
            pending.LifecycleStartEvent = @event.EventName;
            if (type is UtilityType.HeGrenade or UtilityType.Flash or UtilityType.Decoy)
            {
                pending.LifecycleEndedAt = pending.DetonatedAt;
                pending.LifecycleEndEvent = @event.EventName;
            }
            pending.Detonated = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {UtilityType} detonation", type);
        }
    }

    private void HandleSmokeExpired(EventSmokegrenadeExpired @event)
    {
        try
        {
            var owner = PlayerControllerState.From(@event.Userid);
            var pending = FindPending(owner.SteamId, UtilityType.Smoke, @event.Entityid);
            if (pending is null) return;

            pending.LandPosition = new Vector3(@event.X, @event.Y, @event.Z);
            FinalizePending(pending, @event.EventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling smoke grenade expiration");
        }
    }

    private void HandleInfernoStarted(EventInfernoStartburn @event)
    {
        try
        {
            if (@event.Entityid == 0) return;

            var position = new Vector3(@event.X, @event.Y, @event.Z);
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime - InfernoAssociationWindow;
            var pending = _pendingGrenades
                .Where(grenade => grenade.Type == UtilityType.Molotov && grenade.Detonated &&
                                  !grenade.Finalized && grenade.EntityId == 0 && grenade.DetonatedAt >= cutoff)
                .OrderBy(grenade => Vector3.DistanceSquared(grenade.LandPosition, position))
                .FirstOrDefault();
            if (pending is null || Vector3.DistanceSquared(pending.LandPosition, position) > InfernoAssociationRadiusSquared) return;

            pending.EntityId = @event.Entityid;
            pending.LandPosition = position;
            pending.LifecycleStartedAt = _timeProvider.GetUtcNow().UtcDateTime;
            pending.LifecycleStartEvent = @event.EventName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling inferno start");
        }
    }

    private void HandleInfernoEnded(GameEvent @event)
    {
        try
        {
            var entityId = @event.GetIntValue("entityid");
            if (entityId == 0) return;

            var pending = _pendingGrenades.LastOrDefault(grenade =>
                grenade.Type == UtilityType.Molotov && grenade.EntityId == entityId && !grenade.Finalized);
            if (pending is null) return;

            pending.LandPosition = new Vector3(
                @event.GetFloatValue("x"),
                @event.GetFloatValue("y"),
                @event.GetFloatValue("z"));
            FinalizePending(pending, @event.EventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling inferno end");
        }
    }

    private PendingGrenade? FindPending(ulong ownerSteamId, UtilityType type, int entityId)
    {
        if (entityId != 0)
        {
            var exact = _pendingGrenades.LastOrDefault(grenade =>
                grenade.EntityId == entityId && grenade.Type == type && !grenade.Finalized);
            if (exact is not null) return exact;
        }

        return _pendingGrenades.LastOrDefault(grenade =>
            grenade.OwnerSteamId == ownerSteamId && grenade.Type == type && grenade.Detonated && !grenade.Finalized);
    }

    private void FinalizePending(PendingGrenade pending, string lifecycleEndEvent)
    {
        if (pending.Finalized) return;

        var endedAt = pending.LifecycleEndedAt == default
            ? _timeProvider.GetUtcNow().UtcDateTime
            : pending.LifecycleEndedAt;
        var startedAt = pending.LifecycleStartedAt == default ? pending.DetonatedAt : pending.LifecycleStartedAt;
        var durationMs = (long)Math.Max(0, (endedAt - startedAt).TotalMilliseconds);
        if (string.IsNullOrEmpty(pending.LifecycleEndEvent)) pending.LifecycleEndEvent = lifecycleEndEvent;
        pending.LifecycleDurationMs = (int)Math.Min(durationMs, int.MaxValue);
        pending.Finalized = true;
        UpdateEffectiveness(pending);
        PersistPosition(pending);
    }

    private void UpdateEffectiveness(PendingGrenade pending)
    {
        _playerSessions.MutatePlayer(pending.OwnerSteamId, stats =>
        {
            switch (pending.Type)
            {
                case UtilityType.Flash when pending.OpponentsAffected > 0:
                    stats.Utility.EffectiveFlashes++;
                    break;
                case UtilityType.Flash:
                    stats.Utility.WastedFlashes++;
                    break;
                case UtilityType.HeGrenade when pending.Damage > 0 && pending.OpponentsAffected > 0:
                    stats.Utility.EffectiveHEGrenades++;
                    break;
                case UtilityType.Molotov when pending.Damage > 0 && pending.OpponentsAffected > 0:
                    stats.Utility.EffectiveMolotovs++;
                    break;
            }
        });
    }

    private void PersistPosition(PendingGrenade pending)
    {
        var matchUuid = _matchTracker.CurrentMatch?.MatchUuid;
        // Round 0: no freeze end seen yet (loaded mid-round), so there is no round to attribute the throw to.
        if (!_config.EnablePositionTracking || string.IsNullOrWhiteSpace(matchUuid) || _currentRoundNumber == 0) return;

        _positionPersistence.TryEnqueue(new UtilityPositionEvent
        {
            MatchUuid = matchUuid,
            SteamId = pending.OwnerSteamId,
            ThrowX = pending.ThrowPosition.X,
            ThrowY = pending.ThrowPosition.Y,
            ThrowZ = pending.ThrowPosition.Z,
            LandX = pending.LandPosition.X,
            LandY = pending.LandPosition.Y,
            LandZ = pending.LandPosition.Z,
            UtilityType = (int)pending.Type,
            EntityId = pending.EntityId,
            LifecycleStartEvent = pending.LifecycleStartEvent,
            LifecycleEndEvent = pending.LifecycleEndEvent,
            LifecycleDurationMs = pending.LifecycleDurationMs,
            OpponentsAffected = pending.OpponentsAffected,
            TeammatesAffected = pending.TeammatesAffected,
            Damage = pending.Damage,
            MapName = Server.MapName,
            RoundNumber = _currentRoundNumber,
            RoundTime = (int)(pending.DetonatedAt - _roundStartUtc).TotalSeconds
        });
    }

    private static UtilityType ParseUtilityType(string? weapon)
    {
        var normalized = weapon?.Replace("weapon_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
        return normalized switch
        {
            "flashbang" => UtilityType.Flash,
            "smokegrenade" => UtilityType.Smoke,
            "molotov" or "incgrenade" or "inferno" => UtilityType.Molotov,
            "hegrenade" => UtilityType.HeGrenade,
            "decoy" => UtilityType.Decoy,
            _ => UtilityType.Unknown
        };
    }

    private sealed class PendingGrenade(
        ulong ownerSteamId,
        PlayerTeam ownerTeam,
        UtilityType type,
        Vector3 throwPosition)
    {
        // By entity index: a bot (no SteamID) blinded or burned is an opponent affected too, once.
        private readonly HashSet<int> _opponentsAffected = [];
        private readonly HashSet<int> _teammatesAffected = [];

        public ulong OwnerSteamId { get; } = ownerSteamId;
        public PlayerTeam OwnerTeam { get; } = ownerTeam;
        public UtilityType Type { get; } = type;
        public Vector3 ThrowPosition { get; } = throwPosition;
        public Vector3 LandPosition { get; set; }
        public int EntityId { get; set; }
        public int Damage { get; set; }
        public DateTime DetonatedAt { get; set; }
        public DateTime LifecycleStartedAt { get; set; }
        public DateTime LifecycleEndedAt { get; set; }
        public string LifecycleStartEvent { get; set; } = string.Empty;
        public string LifecycleEndEvent { get; set; } = string.Empty;
        public int LifecycleDurationMs { get; set; }
        public bool Detonated { get; set; }
        public bool Finalized { get; set; }
        public int OpponentsAffected => _opponentsAffected.Count;
        public int TeammatesAffected => _teammatesAffected.Count;

        public void RecordAffected(PlayerControllerState victim, int victimIndex)
        {
            // The thrower hurting themselves is not a teammate affected.
            if (!victim.IsValid || victim.SteamId == OwnerSteamId) return;
            if (victim.Team == OwnerTeam) _teammatesAffected.Add(victimIndex);
            else _opponentsAffected.Add(victimIndex);
        }
    }
}
