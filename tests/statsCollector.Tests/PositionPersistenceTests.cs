using Microsoft.Extensions.Logging.Abstractions;
using statsCollector.Config;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

/// <summary>The position writer: a restore's void is applied after the rows queued before it and before the rows
/// queued after it; a rejected row is dropped alone; an outage buffers and retries.</summary>
public class PositionPersistenceTests
{
    [Fact]
    public async Task VoidIsAppliedInQueueOrderAndRejectedRowsAreDroppedAlone()
    {
        var log = new List<string>();
        var repo = new FakeRepo(log);
        var svc = new PositionPersistenceService(repo, NullLogger<PositionPersistenceService>.Instance,
            new PluginConfig());
        await svc.StartAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++) svc.TryEnqueue(Kill(5, i));
        svc.VoidRounds("m1", 5);
        for (var i = 10; i < 12; i++) svc.TryEnqueue(Kill(5, i));
        await Task.Delay(500);
        svc.TryEnqueue(Kill(6, 20));
        svc.TryEnqueue(Kill(6, 99)); // poison: rejected as bad data
        svc.TryEnqueue(Kill(6, 21));
        await Task.Delay(500);
        repo.Outage = true;
        svc.TryEnqueue(Kill(7, 30));
        await Task.Delay(500);
        repo.Outage = false;
        await svc.StopAsync(TimeSpan.FromSeconds(15), CancellationToken.None);

        var joined = string.Join(" | ", log);
        var voidAt = log.IndexOf("void m1 >=5");
        Check(voidAt >= 0, "void applied");
        Check(log.Take(voidAt).Any(e => e.Contains("kills[0,1,2]") || (log.Take(voidAt).Count(e => e.StartsWith("kills")) >= 1)), "rows before the marker written first");
        Check(log.Take(voidAt).All(e => !e.Contains("10") && !e.Contains("11")), "replay rows not written before the void");
        Check(log.Skip(voidAt).Any(e => e.Contains("10,11")), "replay rows written after the void");
        Check(log.Any(e => e == "kills[20,21]" || (log.Any(x => x == "kills[20]") && log.Any(x => x == "kills[21]"))), "batch-mates of the rejected row written");
        Check(!log.Any(e => e.Contains("99")), "rejected row dropped");
        Check(log.Any(e => e == "kills[30]"), "outage row written after recovery");
    }

    private static void Check(bool ok, string what) => Assert.True(ok, what);

    static KillPositionEvent Kill(int round, int id) =>
        new() { MatchUuid = "m1", RoundNumber = round, Distance = id, MapName = "de_test", Weapon = "ak47" };

    sealed class FakeRepo(List<string> log) : IPositionTrackingService
    {
        public volatile bool Outage;
        public Task BulkTrackKillPositionsAsync(IEnumerable<KillPositionEvent> events, CancellationToken ct = default)
        {
            var ids = events.Select(e => (int)e.Distance).ToList();
            if (Outage) throw new IOException("db down");
            if (ids.Contains(99)) throw new InvalidOperationException("rejected row");
            lock (log) log.Add($"kills[{string.Join(",", ids)}]");
            return Task.CompletedTask;
        }
        public Task BulkTrackDeathPositionsAsync(IEnumerable<DeathPositionEvent> events, CancellationToken ct = default) => Task.CompletedTask;
        public Task BulkTrackUtilityPositionsAsync(IEnumerable<UtilityPositionEvent> events, CancellationToken ct = default) => Task.CompletedTask;
        public Task VoidRoundPositionsAsync(string matchUuid, int fromRound, CancellationToken ct = default)
        {
            if (Outage) throw new IOException("db down");
            lock (log) log.Add($"void {matchUuid} >={fromRound}");
            return Task.CompletedTask;
        }
    }
}
