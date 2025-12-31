using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure.Database;

namespace statsCollector.Infrastructure.Health;

public interface IHealthCheck
{
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(IConnectionFactory connectionFactory, ILogger<DatabaseHealthCheck> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
            
            var startTime = DateTime.UtcNow;
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            if (responseTime > 1000)
            {
                _logger.LogWarning("Database health check slow: {ResponseTime}ms", responseTime);
                return HealthCheckResult.Degraded($"Slow response: {responseTime:F0}ms");
            }

            return HealthCheckResult.Healthy($"Response time: {responseTime:F0}ms");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database connection failed", ex);
        }
    }
}

public sealed class HealthCheckResult
{
    public HealthStatus Status { get; }
    public string Description { get; }
    public Exception? Exception { get; }
    public DateTime CheckedAt { get; }

    private HealthCheckResult(HealthStatus status, string description, Exception? exception = null)
    {
        Status = status;
        Description = description;
        Exception = exception;
        CheckedAt = DateTime.UtcNow;
    }

    public static HealthCheckResult Healthy(string description = "Healthy") 
        => new(HealthStatus.Healthy, description);

    public static HealthCheckResult Degraded(string description) 
        => new(HealthStatus.Degraded, description);

    public static HealthCheckResult Unhealthy(string description, Exception? exception = null) 
        => new(HealthStatus.Unhealthy, description, exception);
}

public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}
