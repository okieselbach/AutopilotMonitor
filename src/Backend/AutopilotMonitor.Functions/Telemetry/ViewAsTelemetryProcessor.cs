using AutopilotMonitor.Functions.Helpers;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AutopilotMonitor.Functions.Telemetry;

/// <summary>
/// Stamps every telemetry item a view-as request emits (customEvents, traces, dependencies) with the viewed
/// tenant, so KQL can leave a Global Admin's read-only "view as" out of that tenant's activity. Handlers put
/// the viewed tenant on their own events; this marks them as not the tenant's doing. The request row is
/// stamped by RequestTelemetryMiddleware, which runs outside the scope <see cref="Enter"/> opens.
/// </summary>
public sealed class ViewAsTelemetryProcessor : ITelemetryProcessor
{
    private static readonly AsyncLocal<string?> ViewedTenant = new();

    private readonly ITelemetryProcessor _next;

    public ViewAsTelemetryProcessor(ITelemetryProcessor next) => _next = next;

    /// <summary>Marks the rest of the current async flow — one admitted view-as request — as viewing this tenant.</summary>
    public static void Enter(string viewedTenantId) => ViewedTenant.Value = viewedTenantId;

    public void Process(ITelemetry item)
    {
        Stamp(item, ViewedTenant.Value);
        _next.Process(item);
    }

    internal static void Stamp(ITelemetry item, string? viewedTenantId)
    {
        if (viewedTenantId != null && item is ISupportProperties withProperties)
            withProperties.Properties.TryAdd(RequestRowDimensions.ViewAsTenantId, viewedTenantId);
    }
}
