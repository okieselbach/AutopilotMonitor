using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for pre-auth distress reports (DistressReports table).
    /// All data is unverified — the distress channel has no authentication.
    /// </summary>
    public interface IDistressReportRepository
    {
        Task SaveDistressReportAsync(string tenantId, DistressReportEntry entry);
        Task<List<DistressReportEntry>> GetDistressReportsAsync(string tenantId, int maxResults = 100);
        Task<List<DistressReportEntry>> GetAllDistressReportsAsync(int maxResults = 500);
        Task<int> DeleteDistressReportsOlderThanAsync(string tenantId, DateTime cutoff);
    }

    public class DistressReportEntry
    {
        public string TenantId { get; set; } = string.Empty;
        public string ErrorType { get; set; } = string.Empty;
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? SerialNumber { get; set; }
        public string? AgentVersion { get; set; }
        public int? HttpStatusCode { get; set; }
        public string? Message { get; set; }
        public DateTime AgentTimestamp { get; set; }
        public DateTime IngestedAt { get; set; }
        public string? SourceIp { get; set; }

        /// <summary>
        /// Agent-reported W365 marker verdict (CloudPcDetector). UNVERIFIED like every distress
        /// field; false for rows written before the field existed.
        /// </summary>
        public bool IsCloudPc { get; set; }

        // Cert-context fields (V2 agents only). All optional; legacy entries leave these null.
        // Format-validated and length-capped at ingest; treat as UNVERIFIED claims.
        public string? CertSourceState { get; set; }
        public string? CertThumbprint { get; set; }
        public string? CertSubject { get; set; }
        public string? CertIssuer { get; set; }
        public DateTime? CertNotBefore { get; set; }
        public DateTime? CertNotAfter { get; set; }
    }
}
