using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Tenant-scoped endpoint: returns devices that failed enrollment because they were not
    /// registered in the tenant's Autopilot / Corporate Identifier registry (backend 403),
    /// aggregated by serial number. Data comes from distress reports with
    /// ErrorType == "DeviceNotRegistered".
    ///
    /// Informational only — surfaces a 14-day window of unregistered devices so admins can
    /// sanity-check their enrollment scoping/assignment. The 14-day horizon is enforced by the
    /// distress-report retention cleanup, not by this function.
    /// Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetDeviceNotRegisteredFunction
    {
        private readonly ILogger<GetDeviceNotRegisteredFunction> _logger;
        private readonly IDistressReportRepository _repository;

        public GetDeviceNotRegisteredFunction(
            ILogger<GetDeviceNotRegisteredFunction> logger,
            IDistressReportRepository repository)
        {
            _logger = logger;
            _repository = repository;
        }

        [Function("GetDeviceNotRegistered")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "audit/device-not-registered")] HttpRequestData req)
        {
            try
            {
                string tenantId = TenantHelper.GetTenantId(req);

                var reports = await _repository.GetDistressReportsAsync(tenantId, maxResults: 200);
                var (aggregated, totalRawReports) = BuildAggregatedResult(reports);

                return await req.OkAsync(new
                {
                    success = true,
                    aggregated,
                    totalRawReports,
                    dataQualityNotice = "This data is from pre-authentication distress reports and is UNVERIFIED. Devices reported here were rejected with HTTP 403 because they were not found in the tenant's Autopilot or Corporate Identifier registry. Serial number, manufacturer, model, and the Cloud PC marker are self-reported by devices."
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get devices not registered");
            }
        }

        /// <summary>
        /// Filters distress reports to DeviceNotRegistered and aggregates by serial number.
        /// Reports with no serial number are grouped into a single "unknown serial" bucket.
        /// Extracted as public static for testability.
        /// </summary>
        public static (List<object> aggregated, int totalRawReports) BuildAggregatedResult(
            List<DistressReportEntry> reports)
        {
            var notRegistered = reports
                .Where(r => string.Equals(r.ErrorType, "DeviceNotRegistered", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var aggregated = notRegistered
                .GroupBy(r => (r.SerialNumber ?? "").Trim().ToLowerInvariant())
                .Select(g =>
                {
                    var mostRecent = g.OrderByDescending(r => r.IngestedAt).First();

                    return new
                    {
                        serialNumber = mostRecent.SerialNumber ?? "",
                        manufacturer = mostRecent.Manufacturer ?? "",
                        model = mostRecent.Model ?? "",
                        // Sticky-true across the group (same OR semantics as the Sessions-table
                        // IsCloudPc merge): older agents in the mix report false for the same
                        // device, and a once-seen W365 marker must not flap back to false.
                        isCloudPc = g.Any(r => r.IsCloudPc),
                        attemptCount = g.Count(),
                        firstSeen = g.Min(r => r.IngestedAt),
                        lastSeen = g.Max(r => r.IngestedAt)
                    };
                })
                .OrderByDescending(a => a.lastSeen)
                .ToList<object>();

            return (aggregated, notRegistered.Count);
        }
    }
}
