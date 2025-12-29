using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using statsCollector.Config;

namespace statsCollector.Infrastructure.Database;

public sealed class ConnectionFactory : IConnectionFactory
{
    private readonly IOptionsMonitor<PluginConfig> _config;
    private readonly ILogger<ConnectionFactory> _logger;

    public ConnectionFactory(IOptionsMonitor<PluginConfig> config, ILogger<ConnectionFactory> logger)
    {
        _config = config;
        _logger = logger;
    }

    public MySqlConnection CreateConnection(QueryType queryType = QueryType.Write)
    {
        var configVal = _config.CurrentValue;
        var connectionString = configVal.BuildConnectionString();
        _logger.LogDebug("Creating synchronous connection to {Host}:{Port}", configVal.DatabaseHost, configVal.DatabasePort);
        return new MySqlConnection(connectionString);
    }

    public async Task<MySqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var configVal = _config.CurrentValue;
        var connectionString = configVal.BuildConnectionString();
        _logger.LogDebug("Opening asynchronous connection to {Host}:{Port} (SslMode: {SslMode})", 
            configVal.DatabaseHost, configVal.DatabasePort, configVal.DatabaseSslMode);
        
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogTrace("Database connection opened successfully");
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open database connection to {Host}:{Port}", configVal.DatabaseHost, configVal.DatabasePort);
            throw;
        }
    }
}
