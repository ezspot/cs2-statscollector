using Microsoft.Extensions.Logging.Abstractions;
using statsCollector.Domain;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

public class RoundBackupServiceTests
{
    private const ulong Early = 76561198000000001;
    private const ulong Late = 76561198000000002;

    // Someone who joins or reconnects during the freeze time has no session at round_start, but plays the round: without
    // their totals at its start the round gets no row for them, and a rollback of the round leaves them out.
    [Fact]
    public void PlayerSeededDuringFreezeTimeGetsARoundStartAtFreezeEnd()
    {
        var sessions = new PlayerSessionService(NullLogger<PlayerSessionService>.Instance, new AnalyticsService());
        var backup = new RoundBackupService(NullLogger<RoundBackupService>.Instance, null!, sessions, null!, null!, null!, new NoBaseline());
        Join(sessions, Early, kills: 3);

        backup.CreateSnapshot(4);
        Join(sessions, Late, kills: 7);
        sessions.MutatePlayer(Early, stats => stats.Combat.Kills++);
        backup.AddLateJoiners(4);

        Assert.Equal(7, backup.GetRoundStart(4, Late)!.Kills);
        // Whoever was there at round_start keeps the totals from then.
        Assert.Equal(3, backup.GetRoundStart(4, Early)!.Kills);
        // A round that was never started has nothing to add to.
        backup.AddLateJoiners(5);
        Assert.Null(backup.GetRoundStart(5, Late));
    }

    // Someone who left during a round that a restart then abandons: their row goes back to the round's start like
    // everyone's, and the session kept for the round's end goes with it instead of being written over that row later.
    [Fact]
    public void RollingBackARoundEndsTheSessionOfAPlayerWhoLeftDuringIt()
    {
        var channel = new RecordingChannel();
        var sessions = new PlayerSessionService(NullLogger<PlayerSessionService>.Instance, new AnalyticsService());
        var tracker = new MatchTrackingService(NullLogger<MatchTrackingService>.Instance, channel);
        var backup = new RoundBackupService(NullLogger<RoundBackupService>.Instance, null!, sessions, channel, null!, tracker, new NoBaseline());
        Join(sessions, Early, kills: 3);
        Join(sessions, Late, kills: 7);
        backup.CreateSnapshot(4);
        sessions.HoldDisconnected = true;
        sessions.MutatePlayer(Late, stats => stats.Combat.Kills++);
        sessions.MarkDisconnected(Late);

        Assert.True(backup.RollBackRound(4));

        var rows = channel.Written.OfType<PlayerStatsUpdate>().ToDictionary(update => update.Snapshot.SteamId, update => update.Snapshot.Kills);
        Assert.Equal(7, rows[Late]);
        Assert.Equal(3, rows[Early]);
        // Whoever is still there starts a new session; the leaver has none left.
        Assert.Equal([Early], sessions.GetActiveSteamIds());
    }

    internal static void Join(PlayerSessionService sessions, ulong steamId, int kills)
    {
        long sessionId = 0;
        void Created(ulong _, long id) => sessionId = id;
        sessions.PlayerSessionCreated += Created;
        sessions.EnsurePlayer(PlayerControllerState.From(null) with { SteamId = steamId, PlayerName = "p" });
        sessions.PlayerSessionCreated -= Created;
        sessions.HydrateLifetime(steamId, sessionId, new LifetimePlayerStatsRow { Kills = kills });
    }

    private sealed class NoBaseline : IMatchStatsService
    {
        public void CaptureBaseline() { }
        public void EnsureBaseline(ulong steamId) { }
        public void FinalizeLeavers() { }
        public IReadOnlyDictionary<ulong, PlayerSnapshot> CopyBaseline() => new Dictionary<ulong, PlayerSnapshot>();
        public void RestoreBaseline(IReadOnlyDictionary<ulong, PlayerSnapshot> baseline, IEnumerable<PlayerSnapshot> restartedFrom) { }
        public void RewriteMatchStats(PlayerSnapshot totals, PlayerSnapshot baseline) { }
        public void FinalizeMatch(IReadOnlyDictionary<ulong, EngineMatchStats>? engine = null) { }
    }
}

internal sealed class RecordingChannel : IPersistenceChannel
{
    public List<StatsUpdate> Written { get; } = [];

    public bool TryWrite(StatsUpdate update)
    {
        Written.Add(update);
        return true;
    }

    public Task FlushAsync(ulong? steamId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
