using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

/// <summary>Footsteps, jumps and pings: high-frequency counters of little analytical value. Off unless
/// <see cref="PluginConfig.EnableMovementTracking"/> is set; then the events are not even subscribed.</summary>
public sealed class MovementEventProcessor(IPlayerSessionService playerSessions, PluginConfig config) : IEventProcessor
{
    public void RegisterEvents(IEventDispatcher dispatcher)
    {
        if (!config.EnableMovementTracking) return;

        dispatcher.Subscribe<EventPlayerFootstep>((e, _) => Count(e, stats => stats.Round.Footsteps++));
        dispatcher.Subscribe<EventPlayerJump>((e, _) => Count(e, stats => stats.Round.Jumps++));
        dispatcher.Subscribe<EventPlayerPing>((e, _) => Count(e, stats => stats.Round.Pings++));
    }

    private HookResult Count(GameEvent @event, Action<PlayerStats> increment)
    {
        var player = @event.GetPlayerOrDefault("userid");
        if (player is { IsBot: false, SteamID: not 0 }) playerSessions.MutatePlayer(player.SteamID, increment);
        return HookResult.Continue;
    }
}
