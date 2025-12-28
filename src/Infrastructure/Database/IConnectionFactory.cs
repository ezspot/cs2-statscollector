using MySqlConnector;
using System.Threading;
using System.Threading.Tasks;

namespace statsCollector.Infrastructure.Database;

public interface IConnectionFactory
{
    MySqlConnection CreateConnection(QueryType queryType = QueryType.Write);
    Task<MySqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
}
