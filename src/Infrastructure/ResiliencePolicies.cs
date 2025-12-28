using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace statsCollector.Infrastructure;

public static class ResiliencePolicies
{
    public static IAsyncPolicy CreateDatabasePolicy(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var jitter = new Random();
        var timeout = Policy.TimeoutAsync(TimeSpan.FromSeconds(3), TimeoutStrategy.Optimistic);

        var circuitBreaker = Policy
            .Handle<MySqlConnector.MySqlException>()
            .Or<TimeoutRejectedException>()
            .Or<TimeoutException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, ts) => logger.LogWarning(ex, "DB circuit open for {Duration}s", ts.TotalSeconds),
                onReset: () => logger.LogInformation("DB circuit closed"),
                onHalfOpen: () => logger.LogInformation("DB circuit half-open"));

        var retry = Policy
            .Handle<MySqlConnector.MySqlException>()
            .Or<TimeoutRejectedException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                {
                    var baseMs = 200 * Math.Pow(2, attempt - 1);
                    return TimeSpan.FromMilliseconds(baseMs + jitter.Next(0, 100));
                },
                onRetry: (exception, timeSpan, attempt, context) =>
                {
                    logger.LogWarning(exception, "DB operation failed, retry {Attempt} in {Delay}ms", attempt, timeSpan.TotalMilliseconds);
                });

        return Policy.WrapAsync(retry, circuitBreaker, timeout);
    }
}
