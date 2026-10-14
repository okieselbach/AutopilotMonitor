using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Functions.Services.Caching;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Apps;

/// <summary>
/// The fleet context of one app (<c>GET apps/fleet-context</c>). The app's rows of the tenant's last
/// week are read once per tenant, channel, app and minute and shared by every caller within 60 s on
/// this instance (single flight); each request then counts them without its own session. The read is
/// the apps dashboard's partition query, which fails soft: a storage error reads as no rows.
/// </summary>
public sealed class AppFleetContextService
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Distinct apps cached at once; one more drops the others (see <see cref="SingleFlightCache{T}"/>).</summary>
    private const int MaxCachedApps = 256;

    private readonly IMetricsRepository _metricsRepo;
    private readonly SingleFlightCache<List<AppInstallSummary>> _appRows = new(MaxCachedApps);

    public AppFleetContextService(IMetricsRepository metricsRepo)
    {
        _metricsRepo = metricsRepo;
    }

    public async Task<GetAppFleetContextResponse> GetAsync(
        string tenantId, string appName, string source, string? appId, string? excludeSessionId)
    {
        var cutoff = DateTime.UtcNow.AddDays(-AppsAnalyticsHelper.FleetContextDays);
        // The server filter starts at the minute, never after the exact cutoff applied when counting.
        var minute = new DateTime(cutoff.Ticks - cutoff.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var key = $"{tenantId.ToLowerInvariant()}|{source}|{appName.ToLowerInvariant()}|{minute:yyyyMMddHHmm}";

        var rows = await _appRows.GetOrAddAsync(key, CacheTtl, async () =>
            (await _metricsRepo.GetAppsDashboardSummariesAsync(minute, tenantId))
                .Where(s => AppsAnalyticsHelper.IsSameApp(s, appName, source))
                .ToList());

        return AppsAnalyticsHelper.BuildFleetContextResponse(rows, appName, source, appId, excludeSessionId, cutoff);
    }
}
