using Microsoft.Extensions.Logging.Abstractions;
using statsCollector.Domain;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

/// <summary>A session whose lifetime seed has not completed when the player leaves is held until it is seeded, then
/// handed out once and dropped; a reconnect in between keeps it.</summary>
public class PlayerSessionServiceTests
{
    private const ulong SteamId = 76561198000000001;

    private static (PlayerSessionService Service, long SessionId) Connect()
    {
        var service = new PlayerSessionService(NullLogger<PlayerSessionService>.Instance, new AnalyticsService());
        long sessionId = 0;
        service.PlayerSessionCreated += (_, id) => sessionId = id;
        service.EnsurePlayer(PlayerControllerState.From(null) with { SteamId = SteamId, PlayerName = "p" });
        return (service, sessionId);
    }

    [Fact]
    public void UnseededSessionIsHeldAtDisconnectAndRemovedOnceSeeded()
    {
        var (service, sessionId) = Connect();

        service.MarkDisconnected(SteamId);

        Assert.False(service.TryRemoveDisconnected(SteamId, out _));
        Assert.Contains(SteamId, service.GetActiveSteamIds());

        service.HydrateLifetime(SteamId, sessionId, new LifetimePlayerStatsRow { Kills = 7 });

        Assert.True(service.TryRemoveDisconnected(SteamId, out var snapshot, "m1"));
        Assert.Equal((7, "m1"), (snapshot.Kills, snapshot.MatchUuid));
        Assert.Empty(service.GetActiveSteamIds());
        // Handed out once: the disconnect handler and the seed loop both ask.
        Assert.False(service.TryRemoveDisconnected(SteamId, out _));
    }

    [Fact]
    public void ReconnectClearsTheDisconnectMark()
    {
        var (service, sessionId) = Connect();
        service.MarkDisconnected(SteamId);

        service.EnsurePlayer(PlayerControllerState.From(null) with { SteamId = SteamId, PlayerName = "p" });
        service.HydrateLifetime(SteamId, sessionId, null);

        Assert.False(service.TryRemoveDisconnected(SteamId, out _));
        Assert.Contains(SteamId, service.GetActiveSteamIds());
        Assert.True(service.TryGetSnapshot(SteamId, out _));
    }

    [Fact]
    public void SeededSessionIsRemovedAtDisconnect()
    {
        var (service, sessionId) = Connect();
        service.HydrateLifetime(SteamId, sessionId, null);

        service.MarkDisconnected(SteamId);

        Assert.True(service.TryRemoveDisconnected(SteamId, out _));
        Assert.Empty(service.GetActiveSteamIds());
    }
}
