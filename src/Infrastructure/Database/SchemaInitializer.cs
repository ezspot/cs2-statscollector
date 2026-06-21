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

/// <summary>
/// Applies the embedded database/init.sql on startup when AutoCreateSchema is enabled.
/// The script creates the database, tables, and views (IF NOT EXISTS / OR REPLACE), so it is
/// safe to run repeatedly. It connects without a default database since the script creates it.
/// </summary>
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
            _logger.LogWarning("AutoCreateSchema is enabled but the embedded init.sql could not be found.");
            return;
        }

        try
        {
            var connectionString = _config.CurrentValue.BuildConnectionString(includeDatabase: false);
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(script, cancellationToken: cancellationToken)).ConfigureAwait(false);
            _logger.LogInformation("Database schema ensured (AutoCreateSchema).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoCreateSchema failed; ensure database/init.sql has been applied manually.");
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
}
