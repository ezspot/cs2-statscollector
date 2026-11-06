using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using statsCollector.Config;

namespace statsCollector.Infrastructure.Database;

public sealed class ConnectionFactory : IConnectionFactory
{
    private readonly PluginConfig _config;
    private readonly ILogger<ConnectionFactory> _logger;

    public ConnectionFactory(PluginConfig config, ILogger<ConnectionFactory> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<MySqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = _config.BuildConnectionString();
        _logger.LogDebug("Opening asynchronous connection to {Host}:{Port} (SslMode: {SslMode})", 
            _config.DatabaseHost, _config.DatabasePort, _config.DatabaseSslMode);
        
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogTrace("Database connection opened successfully");
            return connection;
        }
        catch (Exception ex)
        {
            // Callers log the failure once, after their retries.
            _logger.LogDebug(ex, "Failed to open database connection to {Host}:{Port}", _config.DatabaseHost, _config.DatabasePort);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
