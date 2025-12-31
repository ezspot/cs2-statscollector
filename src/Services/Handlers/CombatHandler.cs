using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using statsCollector.Infrastructure;

namespace statsCollector.Services.Handlers;

public sealed class CombatHandler : IGameHandler
{
    private readonly IEventDispatcher _dispatcher;

    public CombatHandler(IEventDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Register(BasePlugin plugin)
    {
        // Lifecycle and Flow
        plugin.RegisterEventHandler<EventRoundFreezeEnd>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerConnectFull>((e, i) => _dispatcher.Dispatch(e, i));

        // Combat Events
        plugin.RegisterEventHandler<EventPlayerDeath>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerHurt>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventWeaponFire>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBulletImpact>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventRoundMvp>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerAvengedTeammate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerSpawned>((e, i) => _dispatcher.Dispatch(e, i));

        // Utility Events (Grouped with Combat generally, or we could make a UtilityHandler)
        plugin.RegisterEventHandler<EventPlayerBlind>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventHegrenadeDetonate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventFlashbangDetonate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventSmokegrenadeDetonate>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventMolotovDetonate>((e, i) => _dispatcher.Dispatch(e, i));

        // Bomb Events
        plugin.RegisterEventHandler<EventBombBeginplant>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombAbortplant>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombPlanted>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombDefused>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombExploded>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombDropped>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombPickup>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombBegindefuse>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventBombAbortdefuse>((e, i) => _dispatcher.Dispatch(e, i));
        
        // Item Events
        plugin.RegisterEventHandler<EventItemPurchase>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventItemPickup>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventItemEquip>((e, i) => _dispatcher.Dispatch(e, i));

        // Movement and Communication
        plugin.RegisterEventHandler<EventPlayerFootstep>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerPing>((e, i) => _dispatcher.Dispatch(e, i));
        plugin.RegisterEventHandler<EventPlayerJump>((e, i) => _dispatcher.Dispatch(e, i));
    }
}
