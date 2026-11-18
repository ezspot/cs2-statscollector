using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using statsCollector.Config;
using statsCollector.Domain;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

/// <summary>The stats channel: a restore's void drops the voided rounds' rows queued before it in the same batch; what an
/// outage or a stop leaves unwritten survives a restart on disk and is not replayed once anything newer is written;
/// every update kind round-trips through JSON.</summary>
public class PersistenceChannelTests
{
    [Fact]
    public async Task VoidDropsTheVoidedRoundsRowsQueuedBeforeIt()
    {
        var log = new List<string>();
        var channel = NewChannel(new FakeRepo(log), NewSpillPath());
        // All queued before the worker starts, so they form one batch.
        channel.TryWrite(new PlayerRoundStatsUpdate(Round(4, scale: 1)));
        channel.TryWrite(new PlayerRoundStatsUpdate(Round(5, scale: 2)));
        channel.TryWrite(new RoundsVoidedUpdate("m1", 5));
        channel.TryWrite(new PlayerRoundStatsUpdate(Round(5, scale: 3)));
        channel.TryWrite(new PlayerRoundStatsUpdate(Round(5, scale: 4, matchUuid: "m2")));
        await channel.StartAsync(CancellationToken.None);
        await channel.FlushAsync();
        await channel.StopAsync(CancellationToken.None);

        Assert.Equal(["void m1 >=5", "rounds[m1 r4 k9, m1 r5 k27, m2 r5 k36]"], log);
    }

    [Fact]
    public async Task UnwrittenBatchesSurviveARestartOnDisk()
    {
        var log = new List<string>();
        var repo = new FakeRepo(log) { Outage = true };
        var path = NewSpillPath();

        var first = NewChannel(repo, path);
        first.TryWrite(new RoundStartUpdate("m1", 1));
        first.TryWrite(new PlayerRoundStatsUpdate(Round(1, scale: 1)));
        await first.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.FlushAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StopAsync(CancellationToken.None));
        Assert.True(File.Exists(path), "spilled on stop");
        Assert.Empty(log);

        // A start while the database is still down replays nothing; it then crashes (never stopped, so it never spills
        // again). The file must outlive it.
        var crashed = NewChannel(new FakeRepo([]) { Outage = true }, path);
        await crashed.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashed.FlushAsync());
        Assert.True(File.Exists(path), "kept until its batches are written");

        repo.Outage = false;
        var second = NewChannel(repo, path);
        await second.StartAsync(CancellationToken.None);
        await second.FlushAsync();
        await second.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(path), "spill file removed once replayed");
        Assert.Equal(["round m1 #1", "rounds[m1 r1 k9]"], log);
    }

    [Fact]
    public async Task SpillIsRemovedBeforeNewerBatchesAreWrittenNotWhenTheBufferEmpties()
    {
        var log = new List<string>();
        var repo = new FakeRepo(log) { Outage = true };
        var path = NewSpillPath();

        var first = NewChannel(repo, path);
        first.TryWrite(new RoundStartUpdate("m1", 1));
        await first.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StopAsync(CancellationToken.None));

        // Restored during the outage; two newer batches are buffered behind it.
        var second = NewChannel(repo, path);
        await second.StartAsync(CancellationToken.None);
        second.TryWrite(new RoundStartUpdate("m1", 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.FlushAsync());
        second.TryWrite(new RoundStartUpdate("m1", 3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.FlushAsync());

        // Back, but the last batch still fails: the restored one and the next are written.
        repo.Refused = "round m1 #3";
        repo.Outage = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.FlushAsync());
        Assert.Equal(["round m1 #1", "round m1 #2"], log);

        // A crash now (never stopped, so no new spill) must not replay the restored batch over the newer one.
        Assert.False(File.Exists(path), "spill file removed once its batches are written");
        var replayed = new List<string>();
        var third = NewChannel(new FakeRepo(replayed), path);
        await third.StartAsync(CancellationToken.None);
        await third.FlushAsync();
        await third.StopAsync(CancellationToken.None);
        Assert.Empty(replayed);
    }

    [Fact]
    public async Task StopSpillsTheBatchItCutsOffInsteadOfWritingItAgain()
    {
        var repo = new HungRepo();
        var path = NewSpillPath();
        var channel = NewChannel(repo, path);
        channel.TryWrite(new RoundStartUpdate("m1", 1));
        await channel.StartAsync(CancellationToken.None);
        await repo.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The unload budget runs out while the worker waits on the database.
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.StopAsync(budget.Token));
        // Well before the 10 s a second write would hang for.
        await channel.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, repo.Attempts);
        var spilled = JsonSerializer.Deserialize<List<List<StatsUpdate>>>(File.ReadAllText(path))!;
        Assert.Equal(new RoundStartUpdate("m1", 1), Assert.Single(Assert.Single(spilled)));
    }

    [Fact]
    public async Task UnreadableSpillIsMovedAsideInsteadOfOverwritten()
    {
        var path = NewSpillPath();
        File.WriteAllText(path, "{ not json");

        var channel = NewChannel(new FakeRepo([]) { Outage = true }, path);
        channel.TryWrite(new RoundStartUpdate("m1", 1));
        await channel.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.StopAsync(CancellationToken.None));

        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "pending_stats.json.unreadable-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.True(File.Exists(path), "this run's spill is written next to it");
    }

    [Fact]
    public void EveryUpdateKindRoundTripsThroughJson()
    {
        var snapshot = DeltaTests.Build(3, weaponValue: 2);
        var analyticsCtor = typeof(PlayerAnalyticsSnapshot).GetConstructors().Single();
        var analytics = (PlayerAnalyticsSnapshot)analyticsCtor.Invoke(analyticsCtor.GetParameters().Select(Arg).ToArray());
        List<StatsUpdate> updates =
        [
            new PlayerStatsUpdate(snapshot),
            new MatchPlayerStatsUpdate(snapshot, analytics),
            new RoundStartUpdate("m1", 1),
            new RoundEndUpdate("m1", 1, 3, 7, new RoundBombInfo("A", 35.5m, 2.25m)),
            new RoundEconomyUpdate("m1", 1, new RoundEconomy(1, 2, 3, 4)),
            new PlayerRoundStatsUpdate(snapshot),
            new RoundOfficialEndUpdate("m1", 1),
            new RoundsVoidedUpdate("m1", 5),
            new MatchStartUpdate("de_test", "m1"),
            new MatchEndUpdate("m1")
        ];
        var kinds = typeof(StatsUpdate).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(StatsUpdate))).Select(t => t.Name).Order();
        Assert.Equal(kinds, updates.Select(u => u.GetType().Name).Order());
        Assert.Equal(kinds, typeof(StatsUpdate).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.DerivedType.Name).Order());

        var json = JsonSerializer.Serialize(updates);
        var back = JsonSerializer.Deserialize<List<StatsUpdate>>(json)!;

        Assert.Equal(updates.Select(u => u.GetType()), back.Select(u => u.GetType()));
        Assert.Equal(json, JsonSerializer.Serialize(back));
        var player = Assert.IsType<PlayerStatsUpdate>(back[0]);
        Assert.Equal(snapshot.Kills, player.Snapshot.Kills);
        Assert.Equal(snapshot.WeaponKills["ak47"], player.Snapshot.WeaponKills["ak47"]);
        Assert.Equal(snapshot.TotalBlindTime, player.Snapshot.TotalBlindTime);
        Assert.Equal(snapshot.SteamId, back[1].SteamId);
        Assert.Equal(analytics, Assert.IsType<MatchPlayerStatsUpdate>(back[1]).Analytics);
    }

    private static object Arg(ParameterInfo p) => p.ParameterType switch
    {
        var t when t == typeof(string) => p.Name!,
        var t when t == typeof(decimal) => 1.25m,
        var t when t == typeof(ulong) => 7UL,
        _ => 2
    };

    private static PersistenceChannel NewChannel(IStatsRepository repo, string spillPath) =>
        new(NullLogger<PersistenceChannel>.Instance, repo, new PluginConfig(), spillPath);

    private static string NewSpillPath() =>
        Path.Combine(Directory.CreateTempSubdirectory("statscollector-tests").FullName, "pending_stats.json");

    private static PlayerSnapshot Round(int round, int scale, string matchUuid = "m1") =>
        DeltaTests.Build(scale, matchUuid) with { RoundNumber = round };

    private sealed class FakeRepo(List<string> log) : IStatsRepository
    {
        public volatile bool Outage;
        // An entry the database keeps refusing while the others go through.
        public volatile string? Refused;

        private Task Record(string entry)
        {
            if (Outage || entry == Refused) throw new IOException("db down");
            lock (log) log.Add(entry);
            return Task.CompletedTask;
        }

        public Task UpsertPlayersAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken ct) => Record("players");
        public Task<LifetimePlayerStatsRow?> GetLifetimePlayerStatsAsync(ulong steamId, CancellationToken ct) => Task.FromResult<LifetimePlayerStatsRow?>(null);
        public Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken ct) => Record("summaries");
        public Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken ct) => Record("weapons");
        public Task UpsertPlayerAnalyticsAsync(IEnumerable<PlayerAnalyticsSnapshot> snapshots, CancellationToken ct) => Record("analytics");
        public Task UpsertPlayerRoundStatsAsync(IEnumerable<PlayerSnapshot> roundDeltas, CancellationToken ct) =>
            Record($"rounds[{string.Join(", ", roundDeltas.Select(d => $"{d.MatchUuid} r{d.RoundNumber} k{d.Kills}"))}]");
        public Task StartMatchAsync(string mapName, string matchUuid, CancellationToken ct) => Record($"match {matchUuid} start");
        public Task EndMatchAsync(string matchUuid, CancellationToken ct) => Record($"match {matchUuid} end");
        public Task StartRoundAsync(string matchUuid, int roundNumber, CancellationToken ct) => Record($"round {matchUuid} #{roundNumber}");
        public Task EndRoundAsync(string matchUuid, int roundNumber, int winnerTeam, int winReason, RoundBombInfo bomb, CancellationToken ct) => Record($"round {matchUuid} #{roundNumber} end");
        public Task UpdateRoundEconomyAsync(string matchUuid, int roundNumber, RoundEconomy economy, CancellationToken ct) => Record($"round {matchUuid} #{roundNumber} economy");
        public Task OfficiallyEndRoundAsync(string matchUuid, int roundNumber, CancellationToken ct) => Record($"round {matchUuid} #{roundNumber} official end");
        public Task VoidRoundsAsync(string matchUuid, int fromRound, CancellationToken ct) => Record($"void {matchUuid} >={fromRound}");
        public Task CheckLocalInfileAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // Every write waits until it is cancelled, like one against a server that accepts and never answers.
    private sealed class HungRepo : IStatsRepository
    {
        public readonly TaskCompletionSource Writing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts;

        private async Task Hang(CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            Writing.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }

        public Task UpsertPlayersAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken ct) => Hang(ct);
        public Task<LifetimePlayerStatsRow?> GetLifetimePlayerStatsAsync(ulong steamId, CancellationToken ct) => Task.FromResult<LifetimePlayerStatsRow?>(null);
        public Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken ct) => Hang(ct);
        public Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken ct) => Hang(ct);
        public Task UpsertPlayerAnalyticsAsync(IEnumerable<PlayerAnalyticsSnapshot> snapshots, CancellationToken ct) => Hang(ct);
        public Task UpsertPlayerRoundStatsAsync(IEnumerable<PlayerSnapshot> roundDeltas, CancellationToken ct) => Hang(ct);
        public Task StartMatchAsync(string mapName, string matchUuid, CancellationToken ct) => Hang(ct);
        public Task EndMatchAsync(string matchUuid, CancellationToken ct) => Hang(ct);
        public Task StartRoundAsync(string matchUuid, int roundNumber, CancellationToken ct) => Hang(ct);
        public Task EndRoundAsync(string matchUuid, int roundNumber, int winnerTeam, int winReason, RoundBombInfo bomb, CancellationToken ct) => Hang(ct);
        public Task UpdateRoundEconomyAsync(string matchUuid, int roundNumber, RoundEconomy economy, CancellationToken ct) => Hang(ct);
        public Task OfficiallyEndRoundAsync(string matchUuid, int roundNumber, CancellationToken ct) => Hang(ct);
        public Task VoidRoundsAsync(string matchUuid, int fromRound, CancellationToken ct) => Hang(ct);
        public Task CheckLocalInfileAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
