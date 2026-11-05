using System.Text.RegularExpressions;
using Xunit;

namespace statsCollector.Tests;

/// <summary>The columns SchemaInitializer adds to an older database must be the columns init.sql creates, with the
/// same definition: a column added to one and not the other leaves upgraded and fresh databases apart.</summary>
public class SchemaInitializerTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "statsCollector.csproj"))) directory = directory.Parent;
        return directory!.FullName;
    }

    private static Dictionary<string, Dictionary<string, string>> InitSqlColumns()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "database", "init.sql"));
        var tables = new Dictionary<string, Dictionary<string, string>>();
        foreach (Match table in Regex.Matches(script, @"CREATE TABLE IF NOT EXISTS (\w+) \((.*?)\) ENGINE=InnoDB;", RegexOptions.Singleline))
        {
            var columns = new Dictionary<string, string>();
            foreach (var raw in table.Groups[2].Value.Split('\n'))
            {
                var line = Regex.Replace(raw, @"\s*--.*$", "").Trim().TrimEnd(',');
                if (line.Length == 0 || Regex.IsMatch(line, @"^(FOREIGN|UNIQUE|INDEX|PRIMARY|KEY|CONSTRAINT|SPATIAL)\b")) continue;
                var split = line.IndexOf(' ');
                columns[line[..split]] = Normalize(line[(split + 1)..]);
            }
            tables[table.Groups[1].Value] = columns;
        }
        return tables;
    }

    // "INT NOT NULL DEFAULT 0 AFTER `x`" and "INT NOT NULL DEFAULT 0" are the same column.
    private static string Normalize(string definition)
    {
        definition = Regex.Replace(definition, @"\s+AFTER\s+`?\w+`?", "");
        definition = Regex.Replace(definition, @",\s+", ",");
        return Regex.Replace(definition, @"\s+", " ").Trim().ToUpperInvariant().Replace("BOOLEAN", "TINYINT(1)");
    }

    public static IEnumerable<object[]> AddedColumns()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Infrastructure", "Database", "SchemaInitializer.cs"));
        // [before, table1, call1, table2, call2, ...]
        var calls = Regex.Split(source, @"EnsureColumnsAsync\(connection, databaseName, ""(\w+)""");
        for (var i = 1; i + 1 < calls.Length; i += 2)
        {
            var body = calls[i + 1];
            var end = body.IndexOf("cancellationToken)", StringComparison.Ordinal);
            if (end >= 0) body = body[..end];
            foreach (Match tuple in Regex.Matches(body, @"\(""(\w+)"",\s*""([^""]+)""\)"))
                yield return [calls[i], tuple.Groups[1].Value, tuple.Groups[2].Value];
        }
    }

    [Theory]
    [MemberData(nameof(AddedColumns))]
    public void EveryUpgradeColumnMatchesInitSql(string table, string column, string definition)
    {
        var tables = InitSqlColumns();
        Assert.True(tables.TryGetValue(table, out var columns), $"init.sql has no table {table}");
        Assert.True(columns!.TryGetValue(column, out var expected), $"init.sql {table} has no column {column}");
        Assert.Equal(expected, Normalize(definition));
    }

    [Fact]
    public void TheUpgradeListIsNotEmpty() => Assert.True(AddedColumns().Count() > 100, "the upgrade column list was not found");
}
