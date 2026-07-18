using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace statsCollector.Infrastructure;

public static class ResiliencePolicies
{
    public static ResiliencePipeline CreateDatabasePipeline(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(5),
                Name = "DatabaseTimeout"
            })
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<MySqlException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "Database operation failed, retry {Attempt} after {Delay}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<MySqlException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TimeoutException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(60),
                OnOpened = args =>
                {
                    logger.LogCritical(args.Outcome.Exception,
                        "Database circuit breaker opened for {Duration}s. Pending statistics will remain in the bounded retry buffer.",
                        args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Database circuit breaker closed. Persistence restored.");
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    logger.LogInformation("Database circuit breaker half-open. Testing recovery.");
                    return default;
                }
            })
            .Build();
    }
}
