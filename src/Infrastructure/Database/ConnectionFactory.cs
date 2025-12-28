using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using statsCollector.Config;

namespace statsCollector.Infrastructure.Database;

public sealed class ConnectionFactory(IOptionsMonitor<PluginConfig> config) : IConnectionFactory
{
    public MySqlConnection CreateConnection(QueryType queryType = QueryType.Write)
    {
        var configVal = config.CurrentValue;
        return new MySqlConnection(configVal.BuildConnectionString());
    }

    public async Task<MySqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var configVal = config.CurrentValue;
        var connection = new MySqlConnection(configVal.BuildConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
