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
/// - FAILED storage calls are KEPT (Success == false): they are rare and are real signal for
///   troubleshooting throttling / transient faults, so the noise reduction does not blind us.
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

        // Keep failures — rare, high-value diagnostic signal. Only successful storage chatter is noise.
        if (dependency.Success == false)
        {
            return false;
        }

        return IsStorageEndpoint(dependency.Target)
            || IsStorageEndpoint(dependency.Data)
            || IsStorageInProcType(dependency.Type);
    }

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
