using System;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// 202/200 response body for the offboarding endpoint. Fields point the caller at the
    /// History row so subsequent reporting / status polling can resolve back to the audit
    /// trail. <see cref="EarliestProcessingAt"/> drives the "data deletion starts in mm ss"
    /// countdown in the Web UI's drain-barrier state.
    /// </summary>
    public class OffboardResponse : IApiResponse
    {
        public string TenantId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string HistoryPartitionKey { get; set; } = string.Empty;
        public string HistoryRowKey { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        /// <summary>UTC timestamp before which the worker MUST NOT start Phase 2. Drives the
        /// cache-drain-barrier countdown UI. Absent on the idempotent-Completed/Failed branches.</summary>
        public DateTime? EarliestProcessingAt { get; set; }
    }
}
