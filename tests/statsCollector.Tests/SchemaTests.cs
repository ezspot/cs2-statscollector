using System.Text.RegularExpressions;
using statsCollector.Domain;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

/// <summary>The per-match column lists against the SQL they generate and the tables init.sql creates.</summary>
public class SchemaTests
{
    private static readonly string Script = ReadInitSql();
    private static readonly Dictionary<string, List<string>> Tables = ParseInitSql();

    private static string ReadInitSql()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "statsCollector.csproj"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "database", "init.sql"));
    }

    private static string TableDdl(string table) =>
        Regex.Match(Script, $@"CREATE TABLE IF NOT EXISTS {table} \((.*?)\) ENGINE=InnoDB;", RegexOptions.Singleline).Groups[1].Value;

    private static Dictionary<string, List<string>> ParseInitSql()
    {
        var tables = new Dictionary<string, List<string>>();
        foreach (Match table in Regex.Matches(Script, @"CREATE TABLE IF NOT EXISTS (\w+) \((.*?)\) ENGINE=InnoDB;", RegexOptions.Singleline))
        {
            var columns = table.Groups[2].Value.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("--") && !Regex.IsMatch(line, @"^(FOREIGN|UNIQUE|INDEX|PRIMARY|KEY|CONSTRAINT|SPATIAL)\b"))
                .Select(line => line.Split(' ')[0])
                .ToList();
            tables[table.Groups[1].Value] = columns;
        }
        return tables;
    }

    public static IEnumerable<object[]> ColumnLists =>
    [
        ["match_player_stats", StatsRepository.MatchPlayerColumns.Select(c => c.Name).ToArray(), new[] { "steam_id" }],
        ["match_weapon_stats", StatsRepository.MatchWeaponColumns.Select(c => c.Name).ToArray(), new[] { "steam_id", "weapon_name" }],
        ["player_round_stats", StatsRepository.PlayerRoundColumns.Select(c => c.Name).ToArray(), new[] { "round_number", "steam_id" }]
    ];

    [Theory]
    [MemberData(nameof(ColumnLists))]
    public void EveryColumnExistsInInitSql(string table, string[] columns, string[] keys)
    {
        _ = keys;
        var ddl = Tables[table];
        Assert.Equal("match_uuid", columns[0]);
        Assert.Equal(columns.Length, columns.Distinct().Count());
        foreach (var column in columns.Skip(1)) Assert.True(ddl.Contains(column), $"{table}.{column} is not in init.sql");
        Assert.Contains("match_id", ddl);
    }

    [Theory]
    [MemberData(nameof(ColumnLists))]
    public void MergeSqlInsertsEveryColumnAndUpdatesTheData(string table, string[] columns, string[] keys)
    {
        var sql = StatsRepository.MergeSql(table, columns, keys);
        var data = columns.Skip(1).ToArray();

        Assert.Contains($"INSERT INTO {table} (match_id, {string.Join(", ", data)})", sql);
        Assert.Contains($"SELECT m.id, {string.Join(", ", data.Select(c => $"t.{c}"))}", sql);
        Assert.Contains($"FROM temp_{table}_uuid t", sql);
        var update = sql[sql.IndexOf("ON DUPLICATE KEY UPDATE", StringComparison.Ordinal)..];
        foreach (var column in data.Where(c => !keys.Contains(c)))
            Assert.Contains($" {column} = t.{column}", update);
        foreach (var column in keys.Append("match_uuid"))
            Assert.DoesNotContain($" {column} = t.{column}", update);
    }

    [Fact]
    public void LifetimeRowMapsOntoPlayerStatsColumns()
    {
        // Dapper matches snake_case columns to the row's properties by dropping the underscores and ignoring case.
        var columns = Tables["player_stats"].Select(c => c.Replace("_", "").ToLowerInvariant()).ToHashSet();
        foreach (var property in typeof(LifetimePlayerStatsRow).GetProperties())
            Assert.True(columns.Contains(property.Name.ToLowerInvariant()), $"player_stats has no column for {property.Name}");
        Assert.DoesNotContain("defuserdrops", columns);
        Assert.DoesNotContain("effectivesmokes", columns);
    }

    [Fact]
    public void AnalyticsRowHasOneIdentityAndLifetimeRatiosHaveRoom()
    {
        // A second UNIQUE key made ON DUPLICATE KEY UPDATE merge two rows of one player written in the same second.
        var analytics = TableDdl("player_advanced_analytics");
        Assert.Single(Regex.Matches(analytics, @"\bUNIQUE\b"));
        Assert.Contains("idempotency_key VARCHAR(100) UNIQUE", analytics);
        // DECIMAL(5,3) capped these at 99.999 without an error (LOAD DATA LOCAL only warns).
        foreach (var column in new[] { "kd_ratio", "impact_rating", "survival_rating", "utility_score" })
            Assert.Contains($"{column} DECIMAL(8,3) DEFAULT 0", TableDdl("player_stats"));
    }

    [Fact]
    public void RoundRowReadsTheRoundFromTheDelta()
    {
        var delta = DeltaTests.Build(0) with
        {
            CtRounds = 1, RoundsWon = 1, PistolRoundsPlayed = 1, Clutch1v3Attempts = 1, Clutch1v3Wins = 1, Mvps = 1,
            SurvivedThisRound = false, WasTradedThisRound = true, TradedDeaths = 1, OpeningDuelsLost = 1
        };
        object? Column(string name) => StatsRepository.PlayerRoundColumns.Single(c => c.Name == name).Value(delta);

        Assert.Equal(3, Column("team"));
        Assert.Equal(1, Column("won"));
        Assert.Equal(1, Column("pistol_round"));
        Assert.Equal(3, Column("clutch_opponents"));
        Assert.Equal(1, Column("clutch_won"));
        Assert.Equal(1, Column("mvp"));
        Assert.Equal(0, Column("survived"));
        Assert.Equal(1, Column("kast"));
        Assert.Equal(1, Column("traded_death"));
        Assert.Equal(1, Column("first_death"));
        Assert.Equal(0, Column("first_kill"));
        Assert.Equal(2, StatsRepository.PlayerRoundColumns.Single(c => c.Name == "team").Value(delta with { CtRounds = 0, TRounds = 1 }));
        Assert.Equal(0, StatsRepository.ClutchOpponents(delta with { Clutch1v3Attempts = 0 }));
    }
}
