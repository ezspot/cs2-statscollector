using MySqlConnector;
using System.Threading;
using System.Threading.Tasks;

namespace statsCollector.Infrastructure.Database;

public interface IConnectionFactory
{
    Task<MySqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
}
