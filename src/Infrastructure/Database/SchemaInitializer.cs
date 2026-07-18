using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
            var createDatabase = $"CREATE DATABASE IF NOT EXISTS `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";
            await connection.ExecuteAsync(new CommandDefinition(createDatabase, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ChangeDatabaseAsync(databaseName, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(script, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var migratedColumns = 0;
            migratedColumns += await EnsureColumnsAsync(connection, databaseName, "rounds",
                [("official_end_time", "TIMESTAMP NULL AFTER `end_time`")], cancellationToken).ConfigureAwait(false);
            migratedColumns += await EnsureColumnsAsync(connection, databaseName, "utility_positions",
                [
                    ("entity_id", "INT NOT NULL DEFAULT 0 AFTER `utility_type`"),
                    ("lifecycle_start_event", "VARCHAR(32) NOT NULL DEFAULT 'legacy' AFTER `entity_id`"),
                    ("lifecycle_end_event", "VARCHAR(32) NOT NULL DEFAULT 'legacy' AFTER `lifecycle_start_event`"),
                    ("lifecycle_duration_ms", "INT UNSIGNED NOT NULL DEFAULT 0 AFTER `lifecycle_end_event`")
                ], cancellationToken).ConfigureAwait(false);
            if (migratedColumns > 0)
                _logger.LogInformation("Applied {Count} event persistence schema columns.", migratedColumns);
            _logger.LogInformation("Database schema ensured (AutoCreateSchema).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoCreateSchema failed; ensure database/init.sql has been applied manually.");
            throw;
        }
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

    private static async Task<int> EnsureColumnsAsync(
        MySqlConnection connection,
        string databaseName,
        string tableName,
        IReadOnlyList<(string Name, string Definition)> columns,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @DatabaseName AND TABLE_NAME = @TableName;
            """;
        var existing = (await connection.QueryAsync<string>(new CommandDefinition(
                query,
                new { DatabaseName = databaseName, TableName = tableName },
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = columns.Where(column => !existing.Contains(column.Name)).ToArray();
        if (missing.Length == 0) return 0;

        var clauses = string.Join(", ", missing.Select(column => $"ADD COLUMN `{column.Name}` {column.Definition}"));
        await connection.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE `{tableName}` {clauses};",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return missing.Length;
    }
}
