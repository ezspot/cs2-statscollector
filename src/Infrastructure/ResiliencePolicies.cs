using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System;
using System.Threading.Tasks;

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
                ShouldHandle = new PredicateBuilder().Handle<MySqlConnector.MySqlException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "DB operation failed, retry {Attempt} after {Delay}ms", 
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<MySqlConnector.MySqlException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TimeoutException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(60),
                OnOpened = args =>
                {
                    logger.LogCritical(args.Outcome.Exception, 
                        "🔴 DATABASE CIRCUIT BREAKER OPENED - Stats persistence disabled for {Duration}s. " +
                        "Server will continue operating but stats will be lost until circuit recovers.",
                        args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("🟢 DATABASE CIRCUIT BREAKER CLOSED - Stats persistence restored");
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    logger.LogInformation("🟡 DATABASE CIRCUIT BREAKER HALF-OPEN - Testing connection recovery");
                    return default;
                }
            })
            .Build();
    }
}
