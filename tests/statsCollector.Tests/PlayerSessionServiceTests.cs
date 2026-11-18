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
    public async Task ReconnectDoesNotCountTheTimeAwayAsPlaytime()
    {
        var (service, sessionId) = Connect();
        service.MarkDisconnected(SteamId);

        await Task.Delay(1500);
        service.EnsurePlayer(PlayerControllerState.From(null) with { SteamId = SteamId, PlayerName = "p" });
        service.HydrateLifetime(SteamId, sessionId, null);

        // Connected for a few milliseconds in all; the 1.5 s away is not playtime.
        Assert.True(service.TryGetSnapshot(SteamId, out var snapshot));
        Assert.Equal(0, snapshot.PlaytimeSeconds);
    }

    // A round being played ends for a leaver when it ends for everyone: the session is kept, so the round end counts the
    // round for them too.
    [Fact]
    public async Task LeaverIsKeptWhileARoundIsPlayedAndRemovedOnceItIsOver()
    {
        var (service, sessionId) = Connect();
        service.HydrateLifetime(SteamId, sessionId, null);
        service.HoldDisconnected = true;

        service.MarkDisconnected(SteamId);

        Assert.False(service.TryRemoveDisconnected(SteamId, out _));
        Assert.True(service.IsHeld(SteamId));
        // Whatever writes the kept session (the round end, a rollback, an unload), its playtime ended at the disconnect.
        await Task.Delay(1500);
        Assert.Equal(0, service.CaptureSnapshots().Single().PlaytimeSeconds);

        service.MutatePlayer(SteamId, stats => stats.Round.RoundsPlayed++);
        service.HoldDisconnected = false;

        Assert.True(service.TryRemoveDisconnected(SteamId, out var snapshot));
        Assert.Equal(1, snapshot.RoundsPlayed);
        Assert.Empty(service.GetActiveSteamIds());
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
