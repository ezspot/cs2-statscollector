using statsCollector.Domain;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

public class CounterTests
{
    [Fact]
    public void ClutchBucketsClampToOneThroughFive()
    {
        var s = new PlayerStats();
        s.Combat.RecordClutch(0, true);
        s.Combat.RecordClutch(3, false);
        s.Combat.RecordClutch(7, true);

        Assert.Equal((1, 1), (s.Combat.Clutch1v1Attempts, s.Combat.Clutch1v1Wins));
        Assert.Equal((1, 0), (s.Combat.Clutch1v3Attempts, s.Combat.Clutch1v3Wins));
        Assert.Equal((1, 1), (s.Combat.Clutch1v5Attempts, s.Combat.Clutch1v5Wins));
        Assert.Equal(0, s.Combat.Clutch1v2Attempts + s.Combat.Clutch1v4Attempts + s.Combat.Clutch1v2Wins + s.Combat.Clutch1v4Wins);
    }

    [Fact]
    public void RoundKillTiersAreExclusive()
    {
        var s = new PlayerStats();
        s.Combat.RecordRoundKill(1);
        Assert.Equal((1, 0, 0), (s.Combat.MultiKill1, s.Combat.MultiKill2, s.Combat.MultiKills));
        s.Combat.RecordRoundKill(2);
        Assert.Equal((0, 1, 1), (s.Combat.MultiKill1, s.Combat.MultiKill2, s.Combat.MultiKills));
        s.Combat.RecordRoundKill(3);
        s.Combat.RecordRoundKill(4);
        s.Combat.RecordRoundKill(5);
        Assert.Equal((0, 0, 0, 0, 1, 1), (s.Combat.MultiKill1, s.Combat.MultiKill2, s.Combat.MultiKill3, s.Combat.MultiKill4, s.Combat.MultiKill5, s.Combat.MultiKills));
        // A sixth kill stays a 5k round.
        s.Combat.RecordRoundKill(6);
        Assert.Equal(1, s.Combat.MultiKill5);
    }

    [Fact]
    public void KillDistanceKeepsTotalAndLongest()
    {
        var s = new PlayerStats();
        s.Combat.RecordKillDistance(12.5m);
        s.Combat.RecordKillDistance(40m);
        s.Combat.RecordKillDistance(3m);
        Assert.Equal((55.5m, 40m), (s.Combat.KillDistanceTotal, s.Combat.LongestKill));
    }

    [Fact]
    public void WeaponDetailIsKeyedByWeapon()
    {
        var s = new PlayerStats();
        s.Weapon.RecordKill("ak47", headshot: true);
        s.Weapon.RecordKill("ak47", headshot: false);
        s.Weapon.RecordDamage("ak47", 27);
        s.Weapon.RecordDamage("ak47", 100);
        s.Weapon.RecordDeath("awp");

        Assert.Equal(2, s.Weapon.KillsByWeapon["ak47"]);
        Assert.Equal(1, s.Weapon.HeadshotsByWeapon["ak47"]);
        Assert.Equal(127, s.Weapon.DamageByWeapon["ak47"]);
        Assert.Equal(1, s.Weapon.DeathsByWeapon["awp"]);
        Assert.False(s.Weapon.HeadshotsByWeapon.ContainsKey("awp"));
    }

    [Fact]
    public void LifetimeRowSeedsSnapshotAndResets()
    {
        var seeded = new PlayerStats { SteamId = 76561198000000001, Name = "seed" };
        seeded.Combat.LongestKill = 50m;
        seeded.SeedLifetime(new LifetimePlayerStatsRow
        {
            TeamKills = 2, Suicides = 1, Clutch1v2Attempts = 4, Clutch1v2Wins = 3, Clutch1v5Wins = 1,
            KillReward = 900, CashEarned = 12_000, MoneySaved = 700, LiveTime = 1_234,
            TeamDamage = 55, MultiKill1 = 9, CtKills = 30, TKills = 31, CtDeaths = 20, TDeaths = 21, CtDamage = 3000, TDamage = 3100,
            CtFirstKills = 5, TFirstKills = 6, CtFirstDeaths = 7, TFirstDeaths = 8, RoundsSurvived = 40, RoundsTraded = 10,
            CtRoundsWon = 15, TRoundsWon = 14, PistolRoundsPlayed = 4, PistolRoundsWon = 2, PistolRoundKills = 3,
            KillDistanceTotal = 1234.5m, LongestKill = 42m,
        });

        var snap = new AnalyticsService().CreateSnapshot(seeded, "m1");
        Assert.Equal((2, 1), (snap.TeamKills, snap.Suicides));
        Assert.Equal((4, 3, 1, 0), (snap.Clutch1v2Attempts, snap.Clutch1v2Wins, snap.Clutch1v5Wins, snap.Clutch1v5Attempts));
        Assert.Equal((900, 12_000, 700, 1_234), (snap.KillReward, snap.CashEarned, snap.MoneySaved, snap.LiveTime));
        Assert.Equal((55, 9, 30, 31, 20, 21), (snap.TeamDamage, snap.MultiKill1, snap.CtKills, snap.TKills, snap.CtDeaths, snap.TDeaths));
        Assert.Equal((3000, 3100, 5, 6, 7, 8), (snap.CtDamage, snap.TDamage, snap.CtFirstKills, snap.TFirstKills, snap.CtFirstDeaths, snap.TFirstDeaths));
        Assert.Equal((40, 10, 15, 14), (snap.RoundsSurvived, snap.RoundsTraded, snap.CtRoundsWon, snap.TRoundsWon));
        Assert.Equal((4, 2, 3), (snap.PistolRoundsPlayed, snap.PistolRoundsWon, snap.PistolRoundKills));
        // The longest kill is a maximum, not a sum.
        Assert.Equal((1234.5m, 50m), (snap.KillDistanceTotal, snap.LongestKill));
        // The fields before the appended blocks are still aligned.
        Assert.Equal(("None", 76561198000000001UL, "m1"), (snap.TopWeaponByKills, snap.SteamId, snap.MatchUuid));
        Assert.Empty(snap.WeaponHeadshots);

        seeded.Combat.Reset();
        seeded.Economy.Reset();
        seeded.Round.Reset();
        Assert.Equal(0, seeded.Combat.Clutch1v2Attempts + seeded.Combat.TeamKills + seeded.Economy.KillReward + seeded.Round.LiveTime +
            seeded.Combat.TeamDamage + seeded.Combat.CtKills + seeded.Round.RoundsSurvived + seeded.Round.PistolRoundsWon + seeded.Combat.MultiKill1);
        Assert.Equal(0m, seeded.Combat.KillDistanceTotal + seeded.Combat.LongestKill);
    }

    [Fact]
    public void RoundFlagsResetEachRound()
    {
        var s = new PlayerStats();
        s.Round.IsPistolRound = true;
        s.Round.WasTradedThisRound = true;
        s.ResetRoundStats();
        Assert.False(s.Round.IsPistolRound);
        Assert.False(s.Round.WasTradedThisRound);
    }
}
