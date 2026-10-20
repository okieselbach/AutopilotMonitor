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
    /// Tenant-scoped endpoint: devices whose TPM-backed client certificate cannot perform RSA-PSS
    /// signing and whose agent could not connect when it reported it, aggregated by serial number.
    /// Data comes from distress reports with ErrorType == "TpmPssUnsupported"; a device that
    /// uploaded with its certificate after its last report carries <see cref="TpmPssUnsupportedItem.ConnectedAt"/>.
    ///
    /// Informational only — the remediation is a TPM firmware update or device replacement,
    /// not a portal action. The 14-day horizon is enforced by the distress-report retention
    /// cleanup, not by this function.
    /// Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetTpmPssUnsupportedFunction
    {
        /// <summary>
        /// How long before the first report the device's later session may have started: a
        /// session's start is backfilled to its earliest log line, and a pre-provisioned device
        /// resumes its session days or weeks after the technician phase.
        /// </summary>
        internal static readonly TimeSpan SessionStartLookback = TimeSpan.FromDays(30);

        private const string TpmPssErrorType = nameof(DistressErrorType.TpmPssUnsupported);

        private readonly ILogger<GetTpmPssUnsupportedFunction> _logger;
        private readonly IDistressReportRepository _repository;
        private readonly ISessionRepository _sessionRepo;

        public GetTpmPssUnsupportedFunction(
            ILogger<GetTpmPssUnsupportedFunction> logger,
            IDistressReportRepository repository,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _repository = repository;
            _sessionRepo = sessionRepo;
        }

        [Function("GetTpmPssUnsupported")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "audit/tpm-pss-unsupported")] HttpRequestData req)
        {
            try
            {
                string tenantId = TenantHelper.GetTenantId(req);

                var reports = await _repository.GetDistressReportsAsync(tenantId, maxResults: 200);
                var tpmReports = reports.Where(IsTpmPssReport).ToList();

                IReadOnlyDictionary<string, DateTime>? lastCertificateIngest = null;
                if (tpmReports.Count > 0)
                {
                    lastCertificateIngest = await _sessionRepo.GetLastCertificateIngestBySerialAsync(
                        tenantId,
                        tpmReports.Select(r => r.SerialNumber ?? "").ToList(),
                        tpmReports.Min(r => r.IngestedAt) - SessionStartLookback);
                }

                var (aggregated, totalRawReports) = BuildAggregatedResult(tpmReports, lastCertificateIngest);

                return await req.OkAsync(new TpmPssUnsupportedResponse
                {
                    Success = true,
                    Aggregated = aggregated,
                    TotalRawReports = totalRawReports,
                    DataQualityNotice = "This data is from pre-authentication distress reports and is UNVERIFIED. Devices reported here have a TPM that cannot sign with RSA-PSS, and their agent could not connect when it sent the report; connectedAt marks a device that uploaded with its certificate afterwards. Serial number, manufacturer, model, OS build, setup state and TPM version are self-reported by devices."
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get TPM PSS unsupported devices");
            }
        }

        private static bool IsTpmPssReport(DistressReportEntry r)
            => string.Equals(r.ErrorType, TpmPssErrorType, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Filters distress reports to TpmPssUnsupported and aggregates by serial number.
        /// Reports with no serial number are grouped into a single "unknown serial" bucket, which
        /// never counts as connected. <paramref name="lastCertificateIngestBySerial"/> (serial
        /// trimmed and lower-cased → last certificate-authenticated upload) sets
        /// <see cref="TpmPssUnsupportedItem.ConnectedAt"/> when that upload came after the last
        /// report; null leaves every device unresolved.
        /// Extracted as public static for testability.
        /// </summary>
        public static (List<TpmPssUnsupportedItem> aggregated, int totalRawReports) BuildAggregatedResult(
            List<DistressReportEntry> reports,
            IReadOnlyDictionary<string, DateTime>? lastCertificateIngestBySerial = null)
        {
            var tpmReports = reports.Where(IsTpmPssReport).ToList();

            var aggregated = tpmReports
                .GroupBy(r => (r.SerialNumber ?? "").Trim().ToLowerInvariant())
                .Select(g =>
                {
                    var newestFirst = g.OrderByDescending(r => r.IngestedAt).ToList();
                    var mostRecent = newestFirst[0];
                    var lastSeen = mostRecent.IngestedAt;

                    DateTime? connectedAt = null;
                    if (g.Key.Length > 0
                        && lastCertificateIngestBySerial != null
                        && lastCertificateIngestBySerial.TryGetValue(g.Key, out var lastIngest)
                        && lastIngest > lastSeen)
                    {
                        connectedAt = lastIngest;
                    }

                    return new TpmPssUnsupportedItem
                    {
                        SerialNumber = mostRecent.SerialNumber ?? "",
                        Manufacturer = mostRecent.Manufacturer ?? "",
                        Model = mostRecent.Model ?? "",
                        AttemptCount = g.Count(),
                        FirstSeen = g.Min(r => r.IngestedAt),
                        LastSeen = lastSeen,
                        // Newest report that carries the value: agents before 2026-10 send none.
                        TpmVersion = newestFirst.Select(r => r.TpmVersion).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                        OsBuild = newestFirst.Select(r => r.OsBuild).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                        OobeState = newestFirst.Select(r => r.OobeState).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                        ConnectedAt = connectedAt,
                    };
                })
                .OrderByDescending(a => a.LastSeen)
                .ToList();

            return (aggregated, tpmReports.Count);
        }
    }
}
