using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AutopilotMonitor.Functions.Telemetry;

/// <summary>
/// Drops successful Azure Storage dependency telemetry (Table / Queue / Blob) before it
/// reaches Application Insights, to curb AppDependencies ingestion cost.
///
/// Rationale: this backend is storage-I/O heavy (telemetry ingest, index dual-write, queues,
/// diagnostics blobs), so the overwhelming majority of AppDependencies rows are high-frequency,
/// successful storage calls with little diagnostic value. AppDependencies does NOT support the
/// cheaper Basic table plan, so the only lever is reducing what is emitted.
///
/// Deliberately scoped:
/// - Only <see cref="DependencyTelemetry"/> is considered; requests, traces, exceptions,
///   metrics and all NON-storage dependencies (HTTP, Microsoft Graph, SQL, SignalR, ...) pass
///   through untouched.
/// - FAILED storage calls are KEPT (Success == false) EXCEPT the expected-outcome status codes
///   404 (point-read miss), 412 (ETag precondition) and 409 (insert conflict): those are normal
///   control flow for this backend (index lookups, optimistic concurrency, idempotent inserts).
///   Live-verified 2026-08-23: 100% of the remaining "failed" storage rows were 404/412/409
///   (~540 MB/week, two rows per call: InProc span + HTTP span). Throttling (429), 5xx, auth
///   failures and timeouts stay — they are the real signal.
/// - Successful Azure SignalR REST management calls (group add/remove per connection, ~470 MB/week)
///   are dropped for the same reason; failed SignalR calls are kept.
///
/// Registered in Program.cs via AddApplicationInsightsTelemetryProcessor so it runs inside the
/// isolated worker's telemetry pipeline, where the app's own Azure SDK dependencies are tracked.
/// </summary>
public sealed class StorageDependencyFilterProcessor : ITelemetryProcessor
{
    // Azure Storage data-plane endpoints. Catches the classic HTTP-level dependency shape
    // ("Azure table"/"Azure queue"/"Azure blob" types), whose Target/Data carry the endpoint host.
    private static readonly string[] StorageEndpointSuffixes =
    {
        ".table.core.windows.net",
        ".queue.core.windows.net",
        ".blob.core.windows.net",
    };

    // Azure SDK ActivitySource dependency shape, as mapped by the App Insights SDK from the
    // activity's az.namespace tag ("InProc | {namespace}"). These rows carry only the SDK
    // operation name (e.g. "TableClient.GetEntity") in Target/Data — no endpoint host — so the
    // suffix match above can never see them. Live-verified 2026-06-09: this shape was ~80% of
    // all billed dependency rows. Prefix match keeps other InProc namespaces (AAD, Insights)
    // and the bare worker "InProc" invocation span untouched.
    private static readonly string[] StorageInProcTypePrefixes =
    {
        "InProc | Microsoft.Tables",   // Azure.Data.Tables
        "InProc | Microsoft.Storage",  // Azure.Storage.Queues + Azure.Storage.Blobs
    };

    // Azure SignalR Service REST endpoint (group membership management per connection).
    private const string SignalRServiceSuffix = ".service.signalr.net";

    // Expected storage outcomes that are normal control flow, not failures worth billing.
    private static readonly string[] ExpectedStorageStatusCodes = { "404", "412", "409" };

    private readonly ITelemetryProcessor _next;

    public StorageDependencyFilterProcessor(ITelemetryProcessor next) => _next = next;

    public void Process(ITelemetry item)
    {
        if (ShouldDrop(item))
        {
            // Swallow: not forwarded to the next processor → never sent → never billed.
            return;
        }

        _next.Process(item);
    }

    private static bool ShouldDrop(ITelemetry item)
    {
        if (item is not DependencyTelemetry dependency)
        {
            return false;
        }

        var isStorage = IsStorageEndpoint(dependency.Target)
            || IsStorageEndpoint(dependency.Data)
            || IsStorageInProcType(dependency.Type);

        if (isStorage)
        {
            // Successful storage chatter is noise; so are expected 404/412/409 outcomes.
            // Every other failure (429, 5xx, auth, timeout) is kept.
            return dependency.Success != false || IsExpectedStorageOutcome(dependency);
        }

        // SignalR REST management calls: keep only failures.
        return dependency.Success != false && IsSignalRService(dependency.Target);
    }

    private static bool IsExpectedStorageOutcome(DependencyTelemetry dependency)
    {
        // HTTP-shaped rows ("Azure table") carry the status in ResultCode; the Azure SDK InProc
        // span has no ResultCode, only the exception text in Properties["Error"]
        // ("...Status: 404 (Not Found)...").
        var code = dependency.ResultCode;
        if (!string.IsNullOrEmpty(code))
        {
            return Array.IndexOf(ExpectedStorageStatusCodes, code) >= 0;
        }

        if (dependency.Properties.TryGetValue("Error", out var error) && !string.IsNullOrEmpty(error))
        {
            foreach (var expected in ExpectedStorageStatusCodes)
            {
                if (error.Contains("Status: " + expected + " (", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSignalRService(string? target)
        => !string.IsNullOrEmpty(target)
           && target.EndsWith(SignalRServiceSuffix, System.StringComparison.OrdinalIgnoreCase);

    private static bool IsStorageInProcType(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return false;
        }

        foreach (var prefix in StorageInProcTypePrefixes)
        {
            if (type.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStorageEndpoint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var suffix in StorageEndpointSuffixes)
        {
            if (value.Contains(suffix, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
