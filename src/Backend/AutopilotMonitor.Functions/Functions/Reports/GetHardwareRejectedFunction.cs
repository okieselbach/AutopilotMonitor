using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Tenant-scoped endpoint: returns hardware whitelist rejections aggregated by manufacturer+model.
    /// Data comes from distress reports with ErrorType == "HardwareNotAllowed".
    /// Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetHardwareRejectedFunction
    {
        private readonly ILogger<GetHardwareRejectedFunction> _logger;
        private readonly IDistressReportRepository _repository;

        public GetHardwareRejectedFunction(
            ILogger<GetHardwareRejectedFunction> logger,
            IDistressReportRepository repository)
        {
            _logger = logger;
            _repository = repository;
        }

        [Function("GetHardwareRejected")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "audit/hardware-rejected")] HttpRequestData req)
        {
            try
            {
                string tenantId = TenantHelper.GetTenantId(req);

                var reports = await _repository.GetDistressReportsAsync(tenantId, maxResults: 200);
                var (aggregated, totalRawReports) = BuildAggregatedResult(reports);

                return await req.OkAsync(new HardwareRejectedResponse
                {
                    Success = true,
                    Aggregated = aggregated,
                    TotalRawReports = totalRawReports,
                    DataQualityNotice = "This data is from pre-authentication distress reports and is UNVERIFIED. Manufacturer, model, and serial number values are self-reported by devices."
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get hardware rejections");
            }
        }

        /// <summary>
        /// Filters distress reports to HardwareNotAllowed and aggregates by manufacturer+model.
        /// Extracted as public static for testability.
        /// </summary>
        public static (List<HardwareRejectedItem> aggregated, int totalRawReports) BuildAggregatedResult(
            List<DistressReportEntry> reports)
        {
            var hardwareReports = reports
                .Where(r => string.Equals(r.ErrorType, "HardwareNotAllowed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var aggregated = hardwareReports
                .GroupBy(r => new
                {
                    Manufacturer = (r.Manufacturer ?? "").ToLowerInvariant(),
                    Model = (r.Model ?? "").ToLowerInvariant()
                })
                .Select(g =>
                {
                    var mostRecent = g.OrderByDescending(r => r.IngestedAt).First();
                    var serials = g
                        .Where(r => !string.IsNullOrEmpty(r.SerialNumber))
                        .Select(r => r.SerialNumber!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    return new HardwareRejectedItem
                    {
                        Manufacturer = mostRecent.Manufacturer ?? "",
                        Model = mostRecent.Model ?? "",
                        AttemptCount = g.Count(),
                        UniqueSerials = serials.Count,
                        FirstSeen = g.Min(r => r.IngestedAt),
                        LastSeen = g.Max(r => r.IngestedAt),
                        SampleSerialNumbers = serials.Take(5).ToList()
                    };
                })
                .OrderByDescending(a => a.LastSeen)
                .ToList();

            return (aggregated, hardwareReports.Count);
        }
    }
}
