namespace AutopilotMonitor.Push;

/// <summary>How a push service answered one delivery attempt, reduced to what the caller must act on.</summary>
public enum PushOutcome
{
    /// <summary>2xx: the push service accepted the message.</summary>
    Delivered,

    /// <summary>404 or 410: the subscription no longer exists; the device must re-subscribe.</summary>
    Gone,

    /// <summary>413, or a body above 4096 bytes refused before sending: a bug in the caller's payload budget.</summary>
    PayloadTooLarge,

    /// <summary>429: rate limited; the caller may try again after <see cref="PushSendResult.RetryAfter"/>.</summary>
    RetryLater,

    /// <summary>400, 401 or 403: the request or the VAPID credentials are wrong; never retry.</summary>
    ConfigError,

    /// <summary>Any other status, a network failure after the single retry, or a refused connection.</summary>
    Failed,
}

/// <summary>Result of <see cref="WebPushSender.SendAsync"/>.</summary>
/// <param name="Outcome">The classification.</param>
/// <param name="StatusCode">The last HTTP status, when a response was received.</param>
/// <param name="RetryAfter">The push service's <c>Retry-After</c>, when it sent one.</param>
/// <param name="Reason">Up to 200 characters of the response body with control characters removed, or a fixed token for local failures.</param>
public sealed record PushSendResult(PushOutcome Outcome, int? StatusCode, TimeSpan? RetryAfter, string? Reason);
