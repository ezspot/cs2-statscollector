using System.Reflection;
using statsCollector.Domain;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

/// <summary>
/// Builds snapshots by reflection so that every numeric property of <see cref="PlayerSnapshot"/> is covered: a counter
/// added to the record without being added to the delta fails here.
/// </summary>
public class DeltaTests
{
    // Not counters: kept from the current snapshot by MatchStatsService.Delta.
    private static readonly HashSet<string> Kept =
    [
        "Version", "RoundNumber", "AliveOnTeamAtRoundStart", "AliveEnemyAtRoundStart",
        "EquipmentValue", "RoundStartMoney", "RoundEndMoney", "EquipmentValueStart", "EquipmentValueEnd", "LongestKill",
        "KDRatio", "HeadshotPercentage", "AccuracyPercentage", "KASTPercentage", "AverageDamagePerRound", "HLTVRating",
        "ImpactRating", "UtilityScore", "PerformanceScore", "AverageKillsPerRound", "AverageDeathsPerRound",
        "AverageAssistsPerRound", "ClutchSuccessRate", "TradeKillRatio", "OpeningKillRatio", "GrenadeEffectivenessRate",
        "FlashEffectivenessRate", "UtilityUsagePerRound", "AverageMoneySpentPerRound", "SurvivalRating", "UtilityImpactScore"
    ];

    // Every numeric parameter gets its own value: parameter i is (i + 1) * scale.
    internal static PlayerSnapshot Build(int scale, string? matchUuid = "m1", int weaponValue = 0)
    {
        var ctor = typeof(PlayerSnapshot).GetConstructors().Single();
        var args = ctor.GetParameters().Select((p, i) => Value(p, i, scale, matchUuid, weaponValue)).ToArray();
        return (PlayerSnapshot)ctor.Invoke(args);
    }

    private static object? Value(ParameterInfo p, int i, int scale, string? matchUuid, int weaponValue)
    {
        var v = (i + 1) * scale;
        if (p.ParameterType == typeof(long)) return (long)v;
        if (p.ParameterType == typeof(int)) return v;
        if (p.ParameterType == typeof(decimal)) return (decimal)v;
        if (p.ParameterType == typeof(float)) return (float)v;
        if (p.ParameterType == typeof(ulong)) return (ulong)v;
        if (p.ParameterType == typeof(bool)) return scale % 2 == 1;
        if (p.ParameterType == typeof(DateTime)) return new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        if (p.ParameterType == typeof(string)) return p.Name == "MatchUuid" ? matchUuid : p.Name!;
        if (p.ParameterType == typeof(IReadOnlyDictionary<string, int>)) return new Dictionary<string, int> { ["ak47"] = weaponValue * scale, ["awp"] = scale };
        throw new InvalidOperationException($"Unexpected parameter {p.Name}: {p.ParameterType}");
    }

    [Fact]
    public void DeltaSubtractsEveryCounterAndKeepsTheRest()
    {
        var start = Build(1, weaponValue: 2);
        var cur = Build(3, weaponValue: 5);

        var delta = MatchStatsService.Delta(cur, start);

        foreach (var property in typeof(PlayerSnapshot).GetProperties())
        {
            var type = property.PropertyType;
            var curValue = property.GetValue(cur);
            var deltaValue = property.GetValue(delta);
            if (type == typeof(IReadOnlyDictionary<string, int>))
            {
                var dictionary = (IReadOnlyDictionary<string, int>)deltaValue!;
                var i = Array.IndexOf(typeof(PlayerSnapshot).GetProperties(), property);
                // ak47: 5 * 3 - 2 * 1 = 13; awp: 3 - 1 = 2.
                Assert.Equal(13, dictionary["ak47"]);
                Assert.Equal(2, dictionary["awp"]);
                continue;
            }
            if (Kept.Contains(property.Name) || type == typeof(bool) || type == typeof(string) || type == typeof(DateTime) || type == typeof(ulong))
            {
                Assert.Equal(curValue, deltaValue);
                continue;
            }

            var startValue = property.GetValue(start);
            object expected = type == typeof(int) ? (int)curValue! - (int)startValue!
                : type == typeof(decimal) ? (decimal)curValue! - (decimal)startValue!
                : type == typeof(float) ? (float)curValue! - (float)startValue!
                : throw new InvalidOperationException($"Unexpected property {property.Name}: {type}");
            Assert.True(expected.Equals(deltaValue), $"{property.Name}: expected {expected}, delta has {deltaValue} (cur {curValue}, start {startValue})");
        }
    }

    [Fact]
    public void DeltaWithoutBaselineIsTheCurrentSnapshot()
    {
        var cur = Build(2, weaponValue: 3);
        var delta = MatchStatsService.Delta(cur, null);
        Assert.Equal(cur.Kills, delta.Kills);
        Assert.Equal(cur.RoundSwing, delta.RoundSwing);
        Assert.Equal(cur.WeaponKills["ak47"], delta.WeaponKills["ak47"]);
    }

    [Fact]
    public void EngineStatsSubtractFieldByField()
    {
        var ctor = typeof(EngineMatchStats).GetConstructors().Single(c => c.GetParameters().Length > 0);
        var count = ctor.GetParameters().Length;
        var a = (EngineMatchStats)ctor.Invoke(Enumerable.Range(1, count).Select(i => (object)(i * 10)).ToArray());
        var b = (EngineMatchStats)ctor.Invoke(Enumerable.Range(1, count).Select(i => (object)i).ToArray());

        var diff = a - b;

        var properties = typeof(EngineMatchStats).GetProperties();
        Assert.Equal(count, properties.Length);
        foreach (var (property, i) in properties.Select((p, i) => (p, i)))
            Assert.True((int)property.GetValue(diff)! == (i + 1) * 9, $"{property.Name} is not its own difference");
    }
}
