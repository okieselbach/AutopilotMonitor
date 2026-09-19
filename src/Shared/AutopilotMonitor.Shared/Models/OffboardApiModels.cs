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

    /// <summary>
    /// Body of GET global/tenants/{tenantId}/offboarding — the tenant's current offboarding
    /// record for the admin tenant editor. <see cref="Status"/> is the marker's status (the
    /// anchor every HTTP decision keys on); the history fields describe the run behind it.
    /// A Failed record carries <see cref="FailedPhase"/> so the operator sees what to expect
    /// from a retry before starting one.
    /// </summary>
    public class TenantOffboardingStatusResponse : IApiResponse
    {
        public string TenantId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string HistoryRowKey { get; set; } = string.Empty;
        public DateTime InitiatedAt { get; set; }
        public string InitiatedBy { get; set; } = string.Empty;
        /// <summary>Queue dequeue attempts plus operator retries of this run.</summary>
        public int RetryCount { get; set; }
        public DateTime? EarliestProcessingAt { get; set; }
        public DateTime? DrainCompletedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? FailedAt { get; set; }
        public string? FailedPhase { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>Body of POST tenants/{tenantId}/offboard/feedback.</summary>
    public class SubmitOffboardingFeedbackRequest : IApiRequest
    {
        public string? Comment { get; set; }
    }
}
