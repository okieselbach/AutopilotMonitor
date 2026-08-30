using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Tenant-scoped endpoint: returns devices whose TPM-backed client certificate cannot
    /// perform RSA-PSS signing (Schannel silently drops the cert from TLS client-auth on
    /// Windows 11 25H2+, so the agent can never authenticate), aggregated by serial number.
    /// Data comes from distress reports with ErrorType == "TpmPssUnsupported".
    ///
    /// Informational only — the remediation is a TPM firmware update or device replacement,
    /// not a portal action. The 14-day horizon is enforced by the distress-report retention
    /// cleanup, not by this function.
    /// Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetTpmPssUnsupportedFunction
    {
        private readonly ILogger<GetTpmPssUnsupportedFunction> _logger;
        private readonly IDistressReportRepository _repository;

        public GetTpmPssUnsupportedFunction(
            ILogger<GetTpmPssUnsupportedFunction> logger,
            IDistressReportRepository repository)
        {
            _logger = logger;
            _repository = repository;
        }

        [Function("GetTpmPssUnsupported")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "audit/tpm-pss-unsupported")] HttpRequestData req)
        {
            try
            {
                string tenantId = TenantHelper.GetTenantId(req);

                var reports = await _repository.GetDistressReportsAsync(tenantId, maxResults: 200);
                var (aggregated, totalRawReports) = BuildAggregatedResult(reports);

                return await req.OkAsync(new TpmPssUnsupportedResponse
                {
                    Success = true,
                    Aggregated = aggregated,
                    TotalRawReports = totalRawReports,
                    DataQualityNotice = "This data is from pre-authentication distress reports and is UNVERIFIED. Devices reported here have a TPM that cannot perform RSA-PSS signing, so their agent cannot authenticate to the backend. Serial number, manufacturer, and model values are self-reported by devices."
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get TPM PSS unsupported devices");
            }
        }

        /// <summary>
        /// Filters distress reports to TpmPssUnsupported and aggregates by serial number.
        /// Reports with no serial number are grouped into a single "unknown serial" bucket.
        /// Extracted as public static for testability.
        /// </summary>
        public static (List<TpmPssUnsupportedItem> aggregated, int totalRawReports) BuildAggregatedResult(
            List<DistressReportEntry> reports)
        {
            var tpmReports = reports
                .Where(r => string.Equals(r.ErrorType, "TpmPssUnsupported", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var aggregated = tpmReports
                .GroupBy(r => (r.SerialNumber ?? "").Trim().ToLowerInvariant())
                .Select(g =>
                {
                    var mostRecent = g.OrderByDescending(r => r.IngestedAt).First();

                    return new TpmPssUnsupportedItem
                    {
                        SerialNumber = mostRecent.SerialNumber ?? "",
                        Manufacturer = mostRecent.Manufacturer ?? "",
                        Model = mostRecent.Model ?? "",
                        AttemptCount = g.Count(),
                        FirstSeen = g.Min(r => r.IngestedAt),
                        LastSeen = g.Max(r => r.IngestedAt)
                    };
                })
                .OrderByDescending(a => a.LastSeen)
                .ToList();

            return (aggregated, tpmReports.Count);
        }
    }
}
