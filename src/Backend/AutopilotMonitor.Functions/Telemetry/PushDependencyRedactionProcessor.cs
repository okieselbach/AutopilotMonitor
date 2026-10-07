using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AutopilotMonitor.Functions.Telemetry;

/// <summary>
/// A Web Push endpoint is a per-device send capability, so the HttpClient dependency rows the
/// worker emits for the push services must not carry it (K20): <c>Data</c> and <c>Name</c> are
/// cut down to scheme + host for those hosts. Target, ResultCode and Duration stay, which is all
/// <c>/platform-health</c> reads. Everything else passes through untouched.
/// </summary>
public sealed class PushDependencyRedactionProcessor : ITelemetryProcessor
{
    private static readonly string[] PushHostSuffixes =
    {
        ".push.apple.com",
        "fcm.googleapis.com",
        ".push.services.mozilla.com",
        ".notify.windows.com",
    };

    private readonly ITelemetryProcessor _next;

    public PushDependencyRedactionProcessor(ITelemetryProcessor next) => _next = next;

    public void Process(ITelemetry item)
    {
        if (item is DependencyTelemetry dependency)
            Redact(dependency);
        _next.Process(item);
    }

    internal static void Redact(DependencyTelemetry dependency)
    {
        if (!Uri.TryCreate(dependency.Data, UriKind.Absolute, out var uri) || !IsPushHost(uri.Host))
            return;

        var origin = $"{uri.Scheme}://{uri.Host}";
        dependency.Data = origin;
        dependency.Name = $"POST {origin}";
    }

    private static bool IsPushHost(string host)
    {
        foreach (var suffix in PushHostSuffixes)
        {
            if (suffix.StartsWith('.') ? host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                                       : host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
