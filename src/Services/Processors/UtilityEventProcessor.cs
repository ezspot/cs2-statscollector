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

public sealed class UtilityEventProcessor : IEventProcessor
{
    private readonly IPlayerSessionService _playerSessions;
    private readonly ILogger<UtilityEventProcessor> _logger;
    private readonly IPositionPersistenceService _positionPersistence;
    private readonly IMatchTrackingService _matchTracker;
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly List<PendingGrenade> _pendingGrenades = [];

    private DateTime _roundStartUtc;
    private int _currentRoundNumber;

    public UtilityEventProcessor(
        IPlayerSessionService playerSessions,
        ILogger<UtilityEventProcessor> logger,
        IPositionPersistenceService positionPersistence,
        IMatchTrackingService matchTracker,
        IOptionsMonitor<PluginConfig> config,
        TimeProvider timeProvider)
    {
        _playerSessions = playerSessions;
        _logger = logger;
        _positionPersistence = positionPersistence;
        _matchTracker = matchTracker;
        _config = config;
        _timeProvider = timeProvider;
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
            UpdateEffectiveness(pending);
            PersistPosition(pending);
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
            var victimState = PlayerControllerState.From(@event.GetPlayerOrDefault("userid"));
            var attackerState = PlayerControllerState.From(@event.GetPlayerOrDefault("attacker"));
            var blindDuration = @event.GetFloatValue("blind_duration", 0f);

            if (victimState.IsValid && !victimState.IsBot)
            {
                _playerSessions.MutatePlayer(victimState.SteamId, stats =>
                {
                    stats.Utility.TotalBlindTime += blindDuration;
                    stats.Utility.TimesBlinded++;
                });
            }

            if (!attackerState.IsValid || attackerState.IsBot || attackerState.SteamId == victimState.SteamId) return;

            var pending = FindPending(attackerState.SteamId, UtilityType.Flash, @event.GetIntValue("entityid"));
            pending?.RecordAffected(victimState);

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
            var attackerState = PlayerControllerState.From(@event.GetPlayerOrDefault("attacker"));
            var victimState = PlayerControllerState.From(@event.GetPlayerOrDefault("userid"));
            if (!attackerState.IsValid || attackerState.IsBot || !victimState.IsValid) return;

            var type = ParseUtilityType(@event.GetStringValue("weapon", string.Empty));
            if (type is not (UtilityType.HeGrenade or UtilityType.Molotov)) return;

            var pending = FindPending(attackerState.SteamId, type, 0);
            if (pending is null) return;
            pending.RecordAffected(victimState);
            if (victimState.Team != pending.OwnerTeam)
            {
                pending.Damage += @event.GetIntValue("dmg_health");
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

            var pending = _pendingGrenades.LastOrDefault(grenade =>
                grenade.OwnerSteamId == state.SteamId && grenade.Type == type && !grenade.Detonated);
            if (pending is null)
            {
                pending = new PendingGrenade(state.SteamId, state.Team, type, state.GrenadeThrowPosition);
                _pendingGrenades.Add(pending);
            }

            pending.EntityId = @event.GetIntValue("entityid");
            pending.LandPosition = new Vector(
                @event.GetFloatValue("x"),
                @event.GetFloatValue("y"),
                @event.GetFloatValue("z"));
            pending.DetonatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            pending.Detonated = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {UtilityType} detonation", type);
        }
    }

    private PendingGrenade? FindPending(ulong ownerSteamId, UtilityType type, int entityId)
    {
        if (entityId != 0)
        {
            var exact = _pendingGrenades.LastOrDefault(grenade => grenade.EntityId == entityId);
            if (exact is not null) return exact;
        }

        return _pendingGrenades.LastOrDefault(grenade =>
            grenade.OwnerSteamId == ownerSteamId && grenade.Type == type && grenade.Detonated);
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
        if (!_config.CurrentValue.EnablePositionTracking || string.IsNullOrWhiteSpace(matchUuid)) return;

        var @event = _positionPersistence.GetUtilityEvent();
        @event.MatchUuid = matchUuid;
        @event.SteamId = pending.OwnerSteamId;
        @event.ThrowX = pending.ThrowPosition.X;
        @event.ThrowY = pending.ThrowPosition.Y;
        @event.ThrowZ = pending.ThrowPosition.Z;
        @event.LandX = pending.LandPosition.X;
        @event.LandY = pending.LandPosition.Y;
        @event.LandZ = pending.LandPosition.Z;
        @event.UtilityType = (int)pending.Type;
        @event.OpponentsAffected = pending.OpponentsAffected;
        @event.TeammatesAffected = pending.TeammatesAffected;
        @event.Damage = pending.Damage;
        @event.MapName = Server.MapName;
        @event.RoundNumber = _currentRoundNumber;
        @event.RoundTime = (int)(pending.DetonatedAt - _roundStartUtc).TotalSeconds;
        _positionPersistence.TryEnqueue(@event);
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
        Vector throwPosition)
    {
        private readonly HashSet<ulong> _opponentsAffected = [];
        private readonly HashSet<ulong> _teammatesAffected = [];

        public ulong OwnerSteamId { get; } = ownerSteamId;
        public PlayerTeam OwnerTeam { get; } = ownerTeam;
        public UtilityType Type { get; } = type;
        public Vector ThrowPosition { get; } = throwPosition;
        public Vector LandPosition { get; set; } = new(0, 0, 0);
        public int EntityId { get; set; }
        public int Damage { get; set; }
        public DateTime DetonatedAt { get; set; }
        public bool Detonated { get; set; }
        public int OpponentsAffected => _opponentsAffected.Count;
        public int TeammatesAffected => _teammatesAffected.Count;

        public void RecordAffected(PlayerControllerState victim)
        {
            if (!victim.IsValid || victim.IsBot) return;
            if (victim.Team == OwnerTeam) _teammatesAffected.Add(victim.SteamId);
            else _opponentsAffected.Add(victim.SteamId);
        }
    }
}
