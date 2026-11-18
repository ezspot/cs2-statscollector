using Microsoft.Extensions.Logging.Abstractions;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

public class MatchStatsServiceTests
{
    private const ulong SteamId = 76561198000000001;

    // A reconnect starts a new session, whose weapon totals start at zero: the match's top weapon is the whole match's,
    // not what the last session happened to kill with.
    [Fact]
    public void TopWeaponOfTheMatchCoversTheSessionBeforeAReconnect()
    {
        var channel = new RecordingChannel();
        var analytics = new AnalyticsService();
        var sessions = new PlayerSessionService(NullLogger<PlayerSessionService>.Instance, analytics);
        var tracker = new MatchTrackingService(NullLogger<MatchTrackingService>.Instance, channel);
        var matchStats = new MatchStatsService(NullLogger<MatchStatsService>.Instance, sessions, analytics, tracker, channel);
        tracker.StartMatch("de_test");
        RoundBackupServiceTests.Join(sessions, SteamId, kills: 0);
        matchStats.CaptureBaseline();
        sessions.MutatePlayer(SteamId, stats =>
        {
            stats.Weapon.RecordKill("ak47", headshot: false);
            stats.Round.RoundsPlayed++;
        });

        sessions.MarkDisconnected(SteamId);
        matchStats.FinalizeLeavers();
        Assert.Empty(sessions.GetActiveSteamIds());
        RoundBackupServiceTests.Join(sessions, SteamId, kills: 0);
        sessions.MutatePlayer(SteamId, stats => stats.Round.RoundsPlayed += 2);
        matchStats.FinalizeMatch();

        var row = channel.Written.OfType<MatchPlayerStatsUpdate>().Last();
        Assert.Equal(("ak47", 1), (row.Analytics.TopWeaponByKills, row.Snapshot.WeaponKills["ak47"]));
    }
}
