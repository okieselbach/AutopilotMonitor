using System.Net;
using AutopilotMonitor.Functions.Security;
using Microsoft.Extensions.Logging;
using Polly;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Singleton container for Polly resilience policies used by external HTTP clients.
/// Must be registered as a DI singleton so circuit breaker state is shared across all requests.
/// </summary>
public sealed class ResiliencePolicies
{
    private static readonly HttpStatusCode[] TransientCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    /// <summary>
    /// Policy for external data APIs (GitHub, NVD, CISA, MSRC):
    /// Retry 3× with exponential backoff + jitter, wrapped in an advanced circuit breaker
    /// (≥50% failures in 30s → 60s open).
    /// </summary>
    public IAsyncPolicy<HttpResponseMessage> ExternalDataApi { get; }

    /// <summary>
    /// Policy for notification clients (webhook, Telegram):
    /// 2 retries with 2s fixed delay. No circuit breaker — a single failing endpoint
    /// should not block all notifications across tenants.
    /// </summary>
    public IAsyncPolicy<HttpResponseMessage> Notification { get; }

    public ResiliencePolicies(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(ResiliencePolicies));
        ExternalDataApi = BuildExternalDataApi(logger);
        Notification = BuildNotification(logger);
    }

    private static IAsyncPolicy<HttpResponseMessage> BuildExternalDataApi(ILogger logger)
    {
        var retry = Policy<HttpResponseMessage>
            .HandleResult(r => TransientCodes.Contains(r.StatusCode))
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))
                    + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)),
                onRetry: (outcome, delay, attempt, _) =>
                {
                    if (outcome.Exception is not null)
                        logger.LogWarning(outcome.Exception,
                            "External API request failed (attempt {Attempt}/3), retrying in {DelayMs}ms",
                            attempt, (int)delay.TotalMilliseconds);
                    else
                        logger.LogWarning(
                            "External API returned {StatusCode} (attempt {Attempt}/3), retrying in {DelayMs}ms",
                            outcome.Result?.StatusCode, attempt, (int)delay.TotalMilliseconds);
                });

        var circuitBreaker = Policy<HttpResponseMessage>
            .HandleResult(r => (int)r.StatusCode >= 500)
            .Or<HttpRequestException>()
            .AdvancedCircuitBreakerAsync(
                failureThreshold: 0.5,
                samplingDuration: TimeSpan.FromSeconds(30),
                minimumThroughput: 5,
                durationOfBreak: TimeSpan.FromSeconds(60),
                onBreak: (outcome, duration) =>
                    logger.LogWarning(
                        "Circuit breaker opened for {DurationSec}s — {Reason}",
                        (int)duration.TotalSeconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString()),
                onReset: () => logger.LogInformation("Circuit breaker reset — external API is healthy"),
                onHalfOpen: () => logger.LogInformation("Circuit breaker half-open — probing external API"));

        // Retry (outer) wraps circuit breaker (inner):
        // When the CB is open it throws BrokenCircuitException, which the retry policy
        // does not handle, so retries stop immediately without exhausting the retry budget.
        return Policy.WrapAsync(retry, circuitBreaker);
    }

    private static IAsyncPolicy<HttpResponseMessage> BuildNotification(ILogger logger)
        => Policy<HttpResponseMessage>
            .HandleResult(r => TransientCodes.Contains(r.StatusCode))
            // A destination the SSRF gate refused stays refused; a retry would only resolve it again.
            .Or<HttpRequestException>(ex => ex.InnerException is not SsrfException)
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: _ => TimeSpan.FromSeconds(2),
                onRetry: (outcome, _, attempt, _) =>
                {
                    if (outcome.Exception is not null)
                        logger.LogWarning(outcome.Exception,
                            "Notification request failed (attempt {Attempt}/2), retrying",
                            attempt);
                    else
                        logger.LogWarning(
                            "Notification returned {StatusCode} (attempt {Attempt}/2), retrying",
                            outcome.Result?.StatusCode, attempt);
                });
}
