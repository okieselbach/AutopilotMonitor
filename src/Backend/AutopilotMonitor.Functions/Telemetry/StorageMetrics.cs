using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Metrics;

namespace AutopilotMonitor.Functions.Telemetry;

/// <summary>Whether a compare-and-swap loop retried after a conflict or gave up.</summary>
public enum CasOutcome
{
    Retried,
    Exhausted,
}

/// <summary>
/// Low-cardinality storage metrics that survive the worker's adaptive sampling and the
/// dependency filter. <see cref="StorageDependencyFilterProcessor"/> drops the expected 412/409
/// outcomes to curb cost, which made ETag conflicts invisible; the CAS loops report them here
/// instead, pre-aggregated per minute by the SDK (<c>GetMetric</c>), so a rising conflict rate
/// on one table shows up as a trend without one row per conflict (audit 2026-09-05 F10).
/// <para>
/// Dimensions: <c>operation</c> (the CAS helper name, ~10 values), <c>table</c> (the table
/// constant) and <c>outcome</c> (<c>retried</c> / <c>exhausted</c>). Never a tenant, session
/// or user id. Null-tolerant: without a <see cref="TelemetryClient"/> every call is a no-op.
/// </para>
/// </summary>
public sealed class StorageMetrics
{
    public const string CasConflictMetricName = "StorageCasConflict";

    private readonly Metric? _casConflicts;

    public StorageMetrics(TelemetryClient? telemetryClient = null)
    {
        _casConflicts = telemetryClient?.GetMetric(CasConflictMetricName, "operation", "table", "outcome");
    }

    /// <summary>Counts one ETag / insert conflict inside a CAS loop.</summary>
    public void CasConflict(string operation, string table, CasOutcome outcome)
        => _casConflicts?.TrackValue(1, operation, table, outcome == CasOutcome.Retried ? "retried" : "exhausted");
}
