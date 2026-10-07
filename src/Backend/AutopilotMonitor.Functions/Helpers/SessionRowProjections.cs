using System;
using System.Linq;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Column lists for the projected point reads of the Sessions row on the agent hot path, and
    /// the reader of the status they carry. Every upload reads the row twice (deletion guard,
    /// counter merge); read whole, the row's size set the ingest latency (backend 1.5.1256:
    /// +35 ms p50 from ~400 bytes of new columns, the storage server unchanged). Each list is
    /// pinned by <c>SessionRowProjectionTests</c> against a select-strict table: a column a
    /// consumer reads but the list lacks fails there, not in production as a silent null.
    /// </summary>
    public static class SessionRowProjections
    {
        /// <summary>
        /// What the agent routes read off the deletion guard's row: the status prefetch
        /// (<see cref="TryReadStatus"/>, the registration's lifecycle entry), the registered serial
        /// for the kill switch, and the SESSION-OWNER-BINDING columns
        /// (<c>SessionOwnershipPolicy.Evaluate</c>). The guard adds its own lock columns.
        /// </summary>
        public static readonly string[] GuardRow =
            new[] { "Status", "SerialNumber" }.Concat(SessionOwner.Columns.All).ToArray();

        /// <summary>
        /// What the counter merge (<c>IncrementSessionEventCountAsync</c>) reads for its own
        /// read-modify-write and what the <c>SessionIngestSnapshot</c> it returns carries.
        /// </summary>
        public static readonly string[] IngestSnapshot =
        {
            // Counter read-modify-write, anchor alignment, index dual-write.
            "EventCount", "StartedAt", "LastEventAt", "PlatformScriptCount", "RemediationScriptCount", "RebootCount",
            "IndexRowKey",
            // Server-time tracker (StageServerTimeUpload / StageServerTimeTwin).
            "CompletedAt", TableStorageService.ServerTimeStateColumn,
            TableStorageService.StartedAtServerColumn, TableStorageService.ResumedAtServerColumn,
            // The snapshot: SignalR delta, control signals, pattern health, shutdown close, live duration
            // (ComputeEffectiveDuration reads DurationSeconds, IsPreProvisioned, ResumedAt).
            "Status", "CurrentPhase", "CurrentPhaseDetail", "FailureReason", "DurationSeconds",
            "DiagnosticsBlobName", "IsPreProvisioned", "ResumedAt", "AdminMarkedAction", "PendingActionsJson",
            "AgentVersion", "ImeAgentVersion",
        };

        /// <summary>
        /// <c>Status</c> off a Sessions row (the guard's row). Sessions writes Status as a STRING
        /// (<c>status.ToString()</c> in UpdateSessionStatusAsync — never an int), so this mirrors the
        /// canonical mapper's parse: <c>Enum.TryParse</c>, case-insensitive. Null for missing or
        /// unparseable values (incl. a defensive int fallback for any legacy numeric shape) — callers
        /// then fall back to their own read.
        /// </summary>
        public static SessionStatus? TryReadStatus(TableEntity? sessionRow)
        {
            if (sessionRow == null || !sessionRow.TryGetValue("Status", out var statusValue))
                return null;

            return statusValue switch
            {
                string s when Enum.TryParse<SessionStatus>(s, ignoreCase: true, out var parsed) => parsed,
                int i when Enum.IsDefined(typeof(SessionStatus), i) => (SessionStatus)i,
                _ => null,
            };
        }
    }
}
