using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace statsCollector.Infrastructure.Health;

public sealed class ChannelHealthCheck : IHealthCheck
{
    private readonly ILogger<ChannelHealthCheck> _logger;
    private long _totalEnqueued;
    private long _totalDropped;
    private readonly object _lock = new();

    public ChannelHealthCheck(ILogger<ChannelHealthCheck> logger)
    {
        _logger = logger;
    }

    public void RecordEnqueued() 
    {
        lock (_lock) _totalEnqueued++;
    }

    public void RecordDropped() 
    {
        lock (_lock) _totalDropped++;
    }

    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        long enqueued, dropped;
        lock (_lock)
        {
            enqueued = _totalEnqueued;
            dropped = _totalDropped;
        }

        if (enqueued == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No events processed yet"));
        }

        var dropRate = (double)dropped / enqueued;
        
        if (dropRate > 0.1)
        {
            _logger.LogWarning("High channel drop rate: {DropRate:P2} ({Dropped}/{Enqueued})", dropRate, dropped, enqueued);
            return Task.FromResult(HealthCheckResult.Unhealthy($"High drop rate: {dropRate:P2}"));
        }
        
        if (dropRate > 0.05)
        {
            _logger.LogWarning("Elevated channel drop rate: {DropRate:P2} ({Dropped}/{Enqueued})", dropRate, dropped, enqueued);
            return Task.FromResult(HealthCheckResult.Degraded($"Elevated drop rate: {dropRate:P2}"));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"Drop rate: {dropRate:P2} ({dropped}/{enqueued})"));
    }
}
