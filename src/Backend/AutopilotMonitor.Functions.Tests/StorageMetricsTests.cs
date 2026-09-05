using AutopilotMonitor.Functions.Telemetry;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

public class StorageMetricsTests
{
    [Fact]
    public void Without_a_client_every_call_is_a_no_op()
    {
        var metrics = new StorageMetrics(null);
        metrics.CasConflict("op", "Sessions", CasOutcome.Retried);
        metrics.CasConflict("op", "Sessions", CasOutcome.Exhausted);
    }

    [Fact]
    public void With_a_client_the_metric_is_registered_once_and_accepts_values()
    {
        using var config = new TelemetryConfiguration { DisableTelemetry = true };
        var client = new TelemetryClient(config);

        var metrics = new StorageMetrics(client);
        metrics.CasConflict("IncrementSessionEventCount", "Sessions", CasOutcome.Retried);
        metrics.CasConflict("IncrementSessionEventCount", "Sessions", CasOutcome.Exhausted);

        // GetMetric returns the same pre-aggregator for the same identifier; a second wrapper
        // over the same client must not fail on re-registration.
        var again = new StorageMetrics(client);
        again.CasConflict("MutateTenantStat", "PlatformStats", CasOutcome.Retried);
    }
}
