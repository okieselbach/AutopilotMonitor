using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// Body of POST global/sessions/{id}/restore for every outcome (success AND reject —
    /// the outcome/status mapping decides the HTTP code, the shape stays one).
    /// </summary>
    public class SessionRestoreResponse : IApiResponse
    {
        public bool Success { get; set; }
        /// <summary>SessionRestoreOutcome name (e.g. "Restored", "DryRunOk", "RejectManifestNotFound").</summary>
        public string Outcome { get; set; } = string.Empty;
        /// <summary>"full" | "partial" | "dryRun"; absent on early rejects.</summary>
        public string? Mode { get; set; }
        /// <summary>Operator-readable reason; absent on clean successes.</summary>
        public string? Message { get; set; }
        /// <summary>Reject diagnostics; absent otherwise.</summary>
        public string? CurrentState { get; set; }
        /// <summary>Reject diagnostics; absent otherwise.</summary>
        public string? PendingManifestId { get; set; }
        public Dictionary<string, int> RowsRestoredByTable { get; set; } = default!;
        public Dictionary<string, int> RowsSkippedByTable { get; set; } = default!;
        public Dictionary<string, int> WouldRestoreByTable { get; set; } = default!;
        public int InventoryReIncrements { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Success body (202 Accepted) of the V2 cascade-delete enqueue; the rejected arms are
    /// <see cref="SessionDeletionRejectedResponse"/>.
    /// </summary>
    public class SessionDeletionQueuedResponse : IApiResponse
    {
        public bool Success { get; set; }
        /// <summary>Always "queued".</summary>
        public string Status { get; set; } = string.Empty;
        public string? ManifestId { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Error body of DELETE sessions/{id} when the cascade could not be enqueued (409 lock states,
    /// 503 kill-switch / CAS exhaustion, 404). Error-envelope prefix plus the lock diagnostics the
    /// portal renders; <c>manifestId</c> is absent when no manifest exists (kill-switch refusal).
    /// </summary>
    public class SessionDeletionRejectedResponse : IApiErrorResponse
    {
        public string Error { get; set; } = default!;
        /// <summary>Constants.ApiErrorCodes: CascadeAlreadyInFlight, CascadePoisonedUseRestore, KillSwitchActive, CasExhaustedRetryLater, NotFound, InternalError.</summary>
        public string Code { get; set; } = default!;
        public string CorrelationId { get; set; } = string.Empty;
        /// <summary>The in-flight cascade's state on the 409 arms; absent otherwise.</summary>
        public string? DeletionState { get; set; }
        /// <summary>The in-flight cascade's manifest on the 409 arms; absent otherwise.</summary>
        public string? ManifestId { get; set; }
    }
}
