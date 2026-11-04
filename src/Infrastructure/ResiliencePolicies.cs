using System.IO;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace statsCollector.Infrastructure;

public static class ResiliencePolicies
{
    // Failures a later attempt can fix: connectivity (MySqlConnector reports a dropped connection with ErrorCode
    // None and an IO/socket inner exception), too many connections, deadlocks and lock waits. A deterministic
    // error (bad SQL, missing column or privilege, local_infile disabled) fails the same way every time.
    public static bool IsTransient(MySqlException ex) =>
        ex.IsTransient || ex.InnerException is IOException or SocketException;

    public static ResiliencePipeline CreateDatabasePipeline(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // Deterministic errors must not be retried or open the breaker that every other database call shares.
        var transient = new PredicateBuilder()
            .Handle<MySqlException>(IsTransient)
            .Handle<TimeoutRejectedException>()
            .Handle<TimeoutException>();

        // Strategies run outermost-first. The timeout is innermost so it bounds each attempt and the retry
        // and circuit breaker see TimeoutRejectedException; as the outer strategy, a hung call surfaced to
        // them as a cancellation and was never retried or counted.
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = transient,
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
                ShouldHandle = transient,
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
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(5),
                Name = "DatabaseTimeout"
            })
            .Build();
    }
}
