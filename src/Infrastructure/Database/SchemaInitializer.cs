using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using statsCollector.Config;

namespace statsCollector.Infrastructure.Database;

public interface ISchemaInitializer
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);
}

public sealed class SchemaInitializer(
    IOptionsMonitor<PluginConfig> config,
    ILogger<SchemaInitializer> logger) : ISchemaInitializer
{
    private readonly IOptionsMonitor<PluginConfig> _config = config;
    private readonly ILogger<SchemaInitializer> _logger = logger;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (!_config.CurrentValue.AutoCreateSchema) return;

        var script = ReadInitScript();
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new InvalidOperationException("AutoCreateSchema is enabled but the embedded init.sql could not be found.");
        }

        try
        {
            var databaseName = _config.CurrentValue.DatabaseName;
            if (databaseName.Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '$')))
            {
                throw new InvalidOperationException("DatabaseName may contain only letters, digits, underscores, and dollar signs.");
            }

            var connectionString = _config.CurrentValue.BuildConnectionString(includeDatabase: false);
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Servers sharing the database often restart together; without a lock they race on ALTER TABLE
            // ADD COLUMN and the loser fails with "Duplicate column name".
            var locked = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT GET_LOCK('statscollector_schema', 60);", cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (locked != 1)
            {
                throw new TimeoutException("Timed out waiting for another server to finish schema initialization.");
            }

            try
            {
                await ApplySchemaAsync(connection, databaseName, script, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Never let the release replace the original error; the server frees a dead session's locks.
                if (connection.State == ConnectionState.Open)
                {
                    try
                    {
                        await connection.ExecuteAsync(new CommandDefinition(
                            "SELECT RELEASE_LOCK('statscollector_schema');", cancellationToken: CancellationToken.None)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not release the schema lock.");
                    }
                }
            }
        }
        // A cancellation is not a failure, and the caller waits out the lock (TimeoutException) and tries again.
        catch (Exception ex) when (ex is not (OperationCanceledException or TimeoutException))
        {
            _logger.LogError(ex, "AutoCreateSchema failed; ensure database/init.sql has been applied manually.");
            throw;
        }
    }

    private async Task ApplySchemaAsync(MySqlConnection connection, string databaseName, string script, CancellationToken cancellationToken)
    {
        var createDatabase = $"CREATE DATABASE IF NOT EXISTS `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";
        await connection.ExecuteAsync(new CommandDefinition(createDatabase, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ChangeDatabaseAsync(databaseName, cancellationToken).ConfigureAwait(false);

        // Existing tables are upgraded before the script runs: its views use the newer columns, and the first view
        // that fails stops the script. New databases have no tables yet, so the script creates them complete. One
        // batch per table; a column goes after the one before it in init.sql, added in the same statement if need be.
        var migratedColumns = 0;
        migratedColumns += await EnsureColumnsAsync(connection, databaseName, "rounds",
            [
                ("official_end_time", "TIMESTAMP NULL AFTER `end_time`"),
                // Migration 006: the round's buy and bomb.
                .. Chain("win_type",
                    ("ct_start_money", "INT NULL"),
                    ("t_start_money", "INT NULL"),
                    ("ct_equipment_value", "INT NULL"),
                    ("t_equipment_value", "INT NULL"),
                    ("bomb_site", "CHAR(1) NULL"),
                    ("bomb_plant_time", "DECIMAL(6,1) NULL"),
                    ("defuse_time_left", "DECIMAL(5,2) NULL"))
            ], cancellationToken).ConfigureAwait(false);
        migratedColumns += await EnsureColumnsAsync(connection, databaseName, "utility_positions",
            [
                ("entity_id", "INT NOT NULL DEFAULT 0 AFTER `utility_type`"),
                ("lifecycle_start_event", "VARCHAR(32) NOT NULL DEFAULT 'legacy' AFTER `entity_id`"),
                ("lifecycle_end_event", "VARCHAR(32) NOT NULL DEFAULT 'legacy' AFTER `lifecycle_start_event`"),
                ("lifecycle_duration_ms", "INT UNSIGNED NOT NULL DEFAULT 0 AFTER `lifecycle_end_event`")
            ], cancellationToken).ConfigureAwait(false);
        migratedColumns += await EnsureColumnsAsync(connection, databaseName, "player_stats",
            [
                ("round_swing", "DECIMAL(10,3) DEFAULT 0 AFTER `utility_score`"),
                ("kast_rounds", "INT NOT NULL DEFAULT 0 AFTER `rounds_won`"),
                ("bomb_explosions", "INT NOT NULL DEFAULT 0 AFTER `bomb_defuses`"),
                ("multi_kill_2", "INT NOT NULL DEFAULT 0 AFTER `multi_kills`"),
                ("multi_kill_3", "INT NOT NULL DEFAULT 0 AFTER `multi_kill_2`"),
                ("multi_kill_4", "INT NOT NULL DEFAULT 0 AFTER `multi_kill_3`"),
                ("multi_kill_5", "INT NOT NULL DEFAULT 0 AFTER `multi_kill_4`"),
                ("clutch_kills", "INT NOT NULL DEFAULT 0 AFTER `clutches_lost`"),
                // Migration 005: team kills, suicides, the 1vX clutch split, and the engine's economy/time-alive counters.
                ("team_kills", "INT NOT NULL DEFAULT 0 AFTER `round_swing`"),
                ("suicides", "INT NOT NULL DEFAULT 0 AFTER `team_kills`"),
                ("clutch_1v1_attempts", "INT NOT NULL DEFAULT 0 AFTER `suicides`"),
                ("clutch_1v1_wins", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v1_attempts`"),
                ("clutch_1v2_attempts", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v1_wins`"),
                ("clutch_1v2_wins", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v2_attempts`"),
                ("clutch_1v3_attempts", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v2_wins`"),
                ("clutch_1v3_wins", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v3_attempts`"),
                ("clutch_1v4_attempts", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v3_wins`"),
                ("clutch_1v4_wins", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v4_attempts`"),
                ("clutch_1v5_attempts", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v4_wins`"),
                ("clutch_1v5_wins", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v5_attempts`"),
                ("kill_reward", "INT NOT NULL DEFAULT 0 AFTER `clutch_1v5_wins`"),
                ("cash_earned", "INT NOT NULL DEFAULT 0 AFTER `kill_reward`"),
                ("money_saved", "INT NOT NULL DEFAULT 0 AFTER `cash_earned`"),
                ("live_time", "INT NOT NULL DEFAULT 0 AFTER `money_saved`"),
                // Migration 006: the side split, KAST parts, pistol rounds, team damage and kill distances. The two columns
                // it drops (defuser_drops, effective_smokes) stay: a server still on the previous build sharing the
                // database maps them by name in its bulk copy. The migration, run by hand, drops them.
                .. Chain("live_time",
                    ("team_damage", "INT NOT NULL DEFAULT 0"),
                    ("multi_kill_1", "INT NOT NULL DEFAULT 0"),
                    ("ct_kills", "INT NOT NULL DEFAULT 0"),
                    ("t_kills", "INT NOT NULL DEFAULT 0"),
                    ("ct_deaths", "INT NOT NULL DEFAULT 0"),
                    ("t_deaths", "INT NOT NULL DEFAULT 0"),
                    ("ct_damage", "INT NOT NULL DEFAULT 0"),
                    ("t_damage", "INT NOT NULL DEFAULT 0"),
                    ("ct_first_kills", "INT NOT NULL DEFAULT 0"),
                    ("t_first_kills", "INT NOT NULL DEFAULT 0"),
                    ("ct_first_deaths", "INT NOT NULL DEFAULT 0"),
                    ("t_first_deaths", "INT NOT NULL DEFAULT 0"),
                    ("rounds_survived", "INT NOT NULL DEFAULT 0"),
                    ("rounds_traded", "INT NOT NULL DEFAULT 0"),
                    ("ct_rounds_won", "INT NOT NULL DEFAULT 0"),
                    ("t_rounds_won", "INT NOT NULL DEFAULT 0"),
                    ("pistol_rounds_played", "INT NOT NULL DEFAULT 0"),
                    ("pistol_rounds_won", "INT NOT NULL DEFAULT 0"),
                    ("pistol_round_kills", "INT NOT NULL DEFAULT 0"),
                    ("kill_distance_total", "DECIMAL(12,2) NOT NULL DEFAULT 0"),
                    ("longest_kill", "DECIMAL(8,2) NOT NULL DEFAULT 0"))
            ], cancellationToken).ConfigureAwait(false);
        // Migration 006: the whole match; then the three columns the 2026-01-01 schema lacked, which every match-end
        // write needs.
        migratedColumns += await EnsureColumnsAsync(connection, databaseName, "match_player_stats", Chain("entry_kill_attempt_wins",
            ("rounds_played", "INT DEFAULT 0"),
            ("rounds_won", "INT DEFAULT 0"),
            ("ct_rounds", "INT DEFAULT 0"),
            ("t_rounds", "INT DEFAULT 0"),
            ("ct_rounds_won", "INT DEFAULT 0"),
            ("t_rounds_won", "INT DEFAULT 0"),
            ("kast_rounds", "INT DEFAULT 0"),
            ("rounds_survived", "INT DEFAULT 0"),
            ("rounds_traded", "INT DEFAULT 0"),
            ("damage_taken", "INT DEFAULT 0"),
            ("team_damage", "INT DEFAULT 0"),
            ("team_kills", "INT DEFAULT 0"),
            ("suicides", "INT DEFAULT 0"),
            ("utility_damage", "INT DEFAULT 0"),
            ("enemies_flashed", "INT DEFAULT 0"),
            ("flash_assists", "INT DEFAULT 0"),
            ("flashes_thrown", "INT DEFAULT 0"),
            ("smokes_thrown", "INT DEFAULT 0"),
            ("he_grenades_thrown", "INT DEFAULT 0"),
            ("molotovs_thrown", "INT DEFAULT 0"),
            ("trade_kills", "INT DEFAULT 0"),
            ("traded_deaths", "INT DEFAULT 0"),
            ("first_kills", "INT DEFAULT 0"),
            ("first_deaths", "INT DEFAULT 0"),
            ("multi_kill_1", "INT DEFAULT 0"),
            ("multi_kill_2", "INT DEFAULT 0"),
            ("multi_kill_3", "INT DEFAULT 0"),
            ("multi_kill_4", "INT DEFAULT 0"),
            ("multi_kill_5", "INT DEFAULT 0"),
            ("clutches_won", "INT DEFAULT 0"),
            ("clutches_lost", "INT DEFAULT 0"),
            ("clutch_1v1_attempts", "INT DEFAULT 0"),
            ("clutch_1v1_wins", "INT DEFAULT 0"),
            ("clutch_1v2_attempts", "INT DEFAULT 0"),
            ("clutch_1v2_wins", "INT DEFAULT 0"),
            ("clutch_1v3_attempts", "INT DEFAULT 0"),
            ("clutch_1v3_wins", "INT DEFAULT 0"),
            ("clutch_1v4_attempts", "INT DEFAULT 0"),
            ("clutch_1v4_wins", "INT DEFAULT 0"),
            ("clutch_1v5_attempts", "INT DEFAULT 0"),
            ("clutch_1v5_wins", "INT DEFAULT 0"),
            ("bomb_plants", "INT DEFAULT 0"),
            ("bomb_defuses", "INT DEFAULT 0"),
            ("shots_fired", "INT DEFAULT 0"),
            ("shots_hit", "INT DEFAULT 0"),
            ("wallbang_kills", "INT DEFAULT 0"),
            ("noscope_kills", "INT DEFAULT 0"),
            ("thru_smoke_kills", "INT DEFAULT 0"),
            ("attacker_blind_kills", "INT DEFAULT 0"),
            ("money_spent", "INT DEFAULT 0"),
            ("kill_reward", "INT DEFAULT 0"),
            ("cash_earned", "INT DEFAULT 0"),
            ("ct_kills", "INT DEFAULT 0"),
            ("t_kills", "INT DEFAULT 0"),
            ("ct_deaths", "INT DEFAULT 0"),
            ("t_deaths", "INT DEFAULT 0"),
            ("ct_damage", "INT DEFAULT 0"),
            ("t_damage", "INT DEFAULT 0"),
            ("pistol_rounds_played", "INT DEFAULT 0"),
            ("pistol_rounds_won", "INT DEFAULT 0"),
            ("pistol_round_kills", "INT DEFAULT 0"),
            ("kill_distance_total", "DECIMAL(12,2) DEFAULT 0"),
            ("round_swing", "DECIMAL(10,3) DEFAULT 0"),
            ("idempotency_key", "VARCHAR(255) NULL"),
            ("created_at", "DATETIME NULL"),
            ("retry_count", "INT DEFAULT 0")), cancellationToken).ConfigureAwait(false);
        // Migration 006: headshots, damage and deaths per weapon; then the 2026-01-01 schema's columns.
        migratedColumns += await EnsureColumnsAsync(connection, databaseName, "match_weapon_stats",
            [
                ("headshots", "INT DEFAULT 0 AFTER `kills`"),
                .. Chain("hits",
                    ("damage", "INT DEFAULT 0"),
                    ("deaths", "INT DEFAULT 0"),
                    ("idempotency_key", "VARCHAR(255) NULL"),
                    ("created_at", "DATETIME NULL"),
                    ("retry_count", "INT DEFAULT 0"))
            ], cancellationToken).ConfigureAwait(false);
        // Migrations 003 and 007: lifetime totals and ratios a narrower column would silently cap (MySQL clamps
        // out-of-range values), and the blind times that stayed INT (whole seconds) on databases from early 2026.
        migratedColumns += await WidenDecimalsAsync(connection, databaseName, "player_stats",
            [
                ("clutch_points", "DECIMAL(10,3) DEFAULT 0"), ("round_swing", "DECIMAL(10,3) DEFAULT 0"),
                ("total_blind_time", "DECIMAL(10,2) DEFAULT 0"), ("total_blind_time_inflicted", "DECIMAL(10,2) DEFAULT 0"),
                ("kd_ratio", "DECIMAL(8,3) DEFAULT 0"), ("impact_rating", "DECIMAL(8,3) DEFAULT 0"),
                ("survival_rating", "DECIMAL(8,3) DEFAULT 0"), ("utility_score", "DECIMAL(8,3) DEFAULT 0")
            ], cancellationToken).ConfigureAwait(false);
        migratedColumns += await WidenDecimalsAsync(connection, databaseName, "player_advanced_analytics",
            [("clutch_points", "DECIMAL(10,3) NOT NULL")], cancellationToken).ConfigureAwait(false);
        // Migration 004: before 2026-07-18 the position tables had no match_id foreign key, and so no index for removing
        // a restored round's rows; without it that delete scans and locks the whole table.
        foreach (var table in new[] { "kill_positions", "death_positions", "utility_positions" })
        {
            migratedColumns += await EnsureMatchIndexAsync(connection, databaseName, table, cancellationToken).ConfigureAwait(false);
        }
        migratedColumns += await ReplaceAnalyticsUniqueKeyAsync(connection, databaseName, cancellationToken).ConfigureAwait(false);
        if (migratedColumns > 0)
            _logger.LogInformation("Applied {Count} schema upgrades.", migratedColumns);

        await connection.ExecuteAsync(new CommandDefinition(script, cancellationToken: cancellationToken)).ConfigureAwait(false);
        _logger.LogInformation("Database schema ensured (AutoCreateSchema).");
    }

    private static string? ReadInitScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("init.sql", StringComparison.OrdinalIgnoreCase));
        if (resourceName == null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Each column placed after the previous one, the first after `after`.
    private static (string Name, string Definition)[] Chain(string after, params (string Name, string Type)[] columns)
    {
        var result = new (string, string)[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            result[i] = (columns[i].Name, $"{columns[i].Type} AFTER `{(i == 0 ? after : columns[i - 1].Name)}`");
        }
        return result;
    }

    private static async Task<HashSet<string>> ExistingColumnsAsync(MySqlConnection connection, string databaseName, string tableName, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @DatabaseName AND TABLE_NAME = @TableName;
            """;
        return (await connection.QueryAsync<string>(new CommandDefinition(
                query,
                new { DatabaseName = databaseName, TableName = tableName },
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<int> EnsureColumnsAsync(
        MySqlConnection connection,
        string databaseName,
        string tableName,
        IReadOnlyList<(string Name, string Definition)> columns,
        CancellationToken cancellationToken)
    {
        var existing = await ExistingColumnsAsync(connection, databaseName, tableName, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0) return 0; // no such table yet; the script creates it

        var missing = columns.Where(column => !existing.Contains(column.Name)).ToArray();
        if (missing.Length == 0) return 0;

        var clauses = string.Join(", ", missing.Select(column => $"ADD COLUMN `{column.Name}` {column.Definition}"));
        await connection.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE `{tableName}` {clauses};",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return missing.Length;
    }

    private static async Task<int> EnsureMatchIndexAsync(
        MySqlConnection connection,
        string databaseName,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT
                (SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @DatabaseName AND TABLE_NAME = @TableName),
                (SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                 WHERE TABLE_SCHEMA = @DatabaseName AND TABLE_NAME = @TableName AND COLUMN_NAME = 'match_id' AND SEQ_IN_INDEX = 1);
            """;
        var (tables, indexes) = await connection.QuerySingleAsync<(long Tables, long Indexes)>(new CommandDefinition(
            query,
            new { DatabaseName = databaseName, TableName = tableName },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (tables == 0 || indexes > 0) return 0;

        // Named as the index a fresh database gets from its foreign key.
        await connection.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE `{tableName}` ADD INDEX `match_id` (`match_id`);",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return 1;
    }

    // Migration 007: a second UNIQUE key (steam_id, calculated_at) on player_advanced_analytics made ON DUPLICATE KEY
    // UPDATE merge two rows of one player written in the same second. idempotency_key (match and player) is the row's
    // one identity; the per-player lookup keeps a plain index, which the steam_id foreign key needs anyway.
    private static async Task<int> ReplaceAnalyticsUniqueKeyAsync(MySqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT DISTINCT INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = @DatabaseName AND TABLE_NAME = 'player_advanced_analytics';
            """;
        var indexes = (await connection.QueryAsync<string>(new CommandDefinition(
                query, new { DatabaseName = databaseName }, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!indexes.Contains("uq_player_analytics_steam_calculated")) return 0;

        var replacement = indexes.Contains("idx_player_analytics_steam_calculated")
            ? ""
            : "ADD INDEX `idx_player_analytics_steam_calculated` (`steam_id`, `calculated_at`), ";
        await connection.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE `player_advanced_analytics` {replacement}DROP INDEX `uq_player_analytics_steam_calculated`;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return 1;
    }

    // Widens a column narrower, in digits or in decimals, than its DECIMAL(p,s) definition: an INT (precision 10,
    // scale 0) loses the fraction a DECIMAL(10,2) keeps.
    private static async Task<int> WidenDecimalsAsync(
        MySqlConnection connection,
        string databaseName,
        string tableName,
        IReadOnlyList<(string Name, string Definition)> columns,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT COLUMN_NAME, CAST(NUMERIC_PRECISION AS SIGNED), CAST(NUMERIC_SCALE AS SIGNED)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @DatabaseName AND TABLE_NAME = @TableName;
            """;
        var widths = (await connection.QueryAsync<(string Name, long? Precision, long? Scale)>(new CommandDefinition(
                query,
                new { DatabaseName = databaseName, TableName = tableName },
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(column => column.Name, column => (column.Precision, column.Scale), StringComparer.OrdinalIgnoreCase);

        var narrow = columns.Where(column =>
        {
            var wanted = Regex.Match(column.Definition, @"DECIMAL\((\d+),(\d+)\)");
            var (precision, scale) = widths.GetValueOrDefault(column.Name);
            return precision < long.Parse(wanted.Groups[1].Value) || scale < long.Parse(wanted.Groups[2].Value);
        }).ToArray();
        if (narrow.Length == 0) return 0;

        var clauses = string.Join(", ", narrow.Select(column => $"MODIFY COLUMN `{column.Name}` {column.Definition}"));
        await connection.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE `{tableName}` {clauses};",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return narrow.Length;
    }
}
