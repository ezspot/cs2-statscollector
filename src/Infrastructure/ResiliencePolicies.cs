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
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "DB circuit opened for {Duration}s", args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("DB circuit closed");
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    logger.LogInformation("DB circuit half-opened");
                    return default;
                }
            })
            .Build();
    }
}
