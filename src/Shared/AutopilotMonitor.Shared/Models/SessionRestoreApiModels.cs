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
    /// Success body (202 Accepted) of the V2 cascade-delete enqueue — the non-success arms
    /// stay anonymous error bodies by design (one shape: success=false + message).
    /// </summary>
    public class SessionDeletionQueuedResponse : IApiResponse
    {
        public bool Success { get; set; }
        /// <summary>Always "queued".</summary>
        public string Status { get; set; } = string.Empty;
        public string? ManifestId { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
