using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Azure;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// One place that classifies <see cref="RequestFailedException"/> from Azure Storage. Writers
/// decide "retry / split / give up" on these predicates and the ingest maps them to the HTTP
/// status the agent understands: 503 for transient (replay the batch later), 413 for payload
/// too large (halve the batch), anything else 500. The transient list mirrors
/// <see cref="ResiliencePolicies"/>' HTTP set so both stay visibly aligned.
/// </summary>
public static class StorageErrors
{
    private static readonly int[] TransientStatuses = [408, 429, 500, 502, 503, 504];

    private static readonly string[] PayloadTooLargeCodes =
    [
        "RequestBodyTooLarge",
        "EntityTooLarge",
        "PropertyValueTooLarge",
    ];

    /// <summary>
    /// Throttling, timeouts and service-side failures — a later retry can succeed. Status 0 is
    /// the SDK's "no response" (connection refused/reset before any status arrived).
    /// </summary>
    public static bool IsTransient(RequestFailedException ex)
        => ex != null && (ex.Status == 0 || Array.IndexOf(TransientStatuses, ex.Status) >= 0);

    /// <summary>
    /// The exception shapes an exhausted SDK budget surfaces as (see <c>StorageClientOptions</c>):
    /// the per-attempt network timeout is an <see cref="OperationCanceledException"/>, a socket
    /// failure an <see cref="IOException"/> / <see cref="HttpRequestException"/>, and after
    /// several attempts Azure.Core wraps them in an <see cref="AggregateException"/>. Only for
    /// call paths WITHOUT a caller cancellation token (the ingest has none): there every
    /// cancellation is the SDK's own timeout, never the caller giving up.
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        null => false,
        RequestFailedException failed => IsTransient(failed),
        OperationCanceledException => true,
        IOException => true,
        HttpRequestException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTransient),
        _ => false,
    };

    /// <summary>
    /// The transaction or entity exceeds a size limit. HTTP 413 in production; some backends
    /// report the size codes with a 400, so the error code is checked as well.
    /// </summary>
    public static bool IsPayloadTooLarge(RequestFailedException ex)
    {
        if (ex == null) return false;
        if (ex.Status == 413) return true;
        return ex.ErrorCode != null && Array.IndexOf(PayloadTooLargeCodes, ex.ErrorCode) >= 0;
    }

    /// <summary>
    /// A conditional Add hit an existing row. Production Azure Tables answers 409; the Azurite
    /// emulator and older service versions answer 400 with the same error code.
    /// </summary>
    public static bool IsAlreadyExists(RequestFailedException ex)
    {
        if (ex == null) return false;
        if (ex.Status == 409) return true;
        return ex.Status == 400 && string.Equals(ex.ErrorCode, "EntityAlreadyExists", StringComparison.Ordinal);
    }
}
