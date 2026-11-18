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
        // MySqlConnector's connect ignores the token while it waits for the server's greeting and ends only at its
        // ConnectionTimeout. The caller stops waiting when the token is cancelled (an unload must spill what it holds
        // before the server exits); the attempt ends by itself.
        var open = connection.OpenAsync(cancellationToken);
        try
        {
            await open.WaitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogTrace("Database connection opened successfully");
            return connection;
        }
        catch (Exception ex)
        {
            // Callers log the failure once, after their retries.
            _logger.LogDebug(ex, "Failed to open database connection to {Host}:{Port}", _config.DatabaseHost, _config.DatabasePort);
            _ = DisposeWhenEndedAsync(open, connection);
            throw;
        }
    }

    // A connection is not disposed while its connect is still running.
    private static async Task DisposeWhenEndedAsync(Task open, MySqlConnection connection)
    {
        await open.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
