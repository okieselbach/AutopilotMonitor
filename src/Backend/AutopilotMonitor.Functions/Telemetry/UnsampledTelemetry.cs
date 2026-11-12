using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

namespace AutopilotMonitor.Functions.Telemetry;

internal static class UnsampledTelemetry
{
    /// <summary>
    /// Sends a custom event that the worker's adaptive sampling may not drop. A plain
    /// <c>TrackEvent</c> is sampled like any other telemetry, whatever the host's
    /// <c>excludedTypes</c> says: that setting covers the host process, not the worker. Use it for
    /// events whose count is the signal (one per run, one per failure).
    /// </summary>
    public static void TrackEventUnsampled(this TelemetryClient? client, string name, IDictionary<string, string> properties)
    {
        if (client == null) return;

        var evt = new EventTelemetry(name);
        foreach (var pair in properties)
            evt.Properties[pair.Key] = pair.Value;

        ((ISupportSampling)evt).SamplingPercentage = 100;
        client.TrackEvent(evt);
    }
}
