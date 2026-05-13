using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Deletion
{
    /// <summary>
    /// Mutable progress companion to <see cref="DeletionManifest"/>. Tracks which steps have
    /// already executed and whether the live verification pass has run. Plan §3 (Round-2 R9):
    /// schema is intentionally minimal — observability data goes to AuditLogs, not here.
    /// Stored as the sibling <c>{manifestId}.progress.json</c> blob and CAS'd on every write.
    /// </summary>
    public class DeletionProgress
    {
        /// <summary>SHA-256 of the immutable snapshot blob; mismatch = corruption, refuse to proceed.</summary>
        public string SnapshotSha256 { get; set; } = string.Empty;

        /// <summary>Step.Order values that have completed; the worker iterates the remaining ones.</summary>
        public HashSet<int> CompletedSteps { get; set; } = new HashSet<int>();

        /// <summary>True once the live verification pass succeeded; gates the FINAL tombstone step.</summary>
        public bool VerificationDone { get; set; }

        /// <summary>UTC timestamp once the FINAL tombstone step has completed; null while in flight.</summary>
        public DateTime? CompletedAt { get; set; }

        // ============================================================ PR4c additions ====
        // Three additive fields close the per-key idempotency + tombstone-gap correctness holes
        // discovered by Codex review of PR4 + PR4b. All three default to safe values
        // (null / false) so PR1–PR4 progress blobs without these fields deserialize cleanly.

        /// <summary>
        /// PR4c F1: Composite keys (<c>{Vendor}:{Name}:{Version}</c>) of SoftwareInventory
        /// decrements already applied by the cascade worker's AGGREGATE step. Per-key
        /// persistence so a crash mid-decrement-loop doesn't double-decrement on retry.
        /// Worker writes one entry after each successful <c>DecrementSoftwareInventoryEntryAsync</c>
        /// and persists the progress blob with ETag-CAS, then moves to the next key.
        /// Null on PR1-PR4 progress blobs (worker initializes on first AGGREGATE-step entry).
        /// </summary>
        public HashSet<string>? AggregateDecrementsApplied { get; set; }

        /// <summary>
        /// PR4c F4: Composite keys of SoftwareInventory re-increments already applied by the
        /// partial-restore service. Per-key persistence so a crash between the re-increment
        /// loop and the final <c>Poisoned → None</c> CAS doesn't double-increment counters on
        /// retry. Service writes one entry after each successful
        /// <c>RestoreSoftwareInventoryEntryByKeyAsync</c> and persists with ETag-CAS.
        /// Null on PR1-PR4 progress blobs (service initializes on first re-increment).
        /// </summary>
        public HashSet<string>? RestoreReIncrementsApplied { get; set; }

        /// <summary>
        /// PR4c F2: Set to <c>true</c> by the cascade worker <b>before</b> issuing the first
        /// FINAL-step row delete. Closes the "tombstone gap": if the worker dies between
        /// deleting the Sessions row and writing <see cref="CompletedAt"/>, restore can still
        /// dispatch into full-restore mode by reading this flag (otherwise
        /// <c>sessions=null + completedAt=null</c> looks like corruption and restore rejects).
        /// Default <c>false</c> for back-compat — PR1-PR4 progress blobs without this field
        /// deserialize cleanly to <c>false</c>, preserving the existing "corrupt-state" reject
        /// behaviour for genuine bugs (Sessions row removed outside the cascade).
        /// </summary>
        public bool TombstoneStarted { get; set; }

        // ============================================================ PR-B follow-up ====
        // Codex F4 (Medium): PR-B routed step / verification failures out of the per-tenant
        // audit (intentional — tenants don't need them), but the SessionDeletionPoisoned
        // OpsEvent was left with only queue-side data (dequeueCount + manifestId). The actual
        // root cause was only visible in App Insights structured logs, which is a problem for
        // long-tail operator forensics (logs roll off, the OpsEvent is the durable record).
        // These three nullable fields are written by the handler immediately before it throws
        // and read by the worker when it transitions the cascade to Poisoned, so the
        // SessionDeletionPoisoned OpsEvent carries the failure context that explains WHY.
        //
        // All three are nullable / default-empty so blobs written by pre-PR-B-followup workers
        // deserialize cleanly. The worker's poison-emit path defends against missing values
        // with explicit string.IsNullOrEmpty checks before attaching them to the OpsEvent.

        /// <summary>Classification of the most recent failure that triggered a re-queue. Examples:
        /// <c>"verification_residuals"</c>, <c>"step_exception"</c>. Null until the first failure.</summary>
        public string? LastFailureType { get; set; }

        /// <summary>Short, human-readable description of the most recent failure (truncated to
        /// 1024 chars by the writer). Suitable for OpsEvent embedding and operator triage.</summary>
        public string? LastFailureMessage { get; set; }

        /// <summary>
        /// Verification-failure path only: how many residual rows the verifier <b>observed</b>
        /// before short-circuiting. This is <b>NOT</b> guaranteed to be the true residual count
        /// — <c>CascadeVerificationService</c> stops both at
        /// <see cref="DeletionProgressConstants.VerificationResidualSampleSize"/> per table AND
        /// after the first failing table, so the real count may be higher. Operators treating
        /// this number as a blast-radius estimate should add "≥" mentally when it equals the cap.
        /// Null on step-exception failures and on pre-followup progress blobs.
        /// </summary>
        public int? LastObservedResidualCount { get; set; }

        /// <summary>
        /// Verification-failure path only: JSON-encoded sample of residual <c>{table, pk, rk}</c>
        /// triples (capped at <see cref="DeletionProgressConstants.VerificationResidualSampleSize"/>).
        /// Empty/null on step-exception failures. The array length always matches
        /// <see cref="LastObservedResidualCount"/> for blobs written by the current handler
        /// because the verifier and the sample share the same cap; the two fields existed
        /// historically because the design anticipated decoupling them when the verifier becomes
        /// exhaustive.
        /// </summary>
        public string? LastResidualSampleJson { get; set; }
    }

    /// <summary>Constants relating to <see cref="DeletionProgress"/> serialization caps.</summary>
    public static class DeletionProgressConstants
    {
        /// <summary>
        /// Cap on the verification residual sample written into the progress blob. Matches the
        /// in-memory log cap so the two payloads stay aligned. The progress blob has no size
        /// budget worth worrying about; this is the size operators see when they fetch the
        /// stored manifest from the admin Session Cleanup page.
        /// </summary>
        public const int VerificationResidualSampleSize = 50;

        /// <summary>
        /// Maximum entries kept in the residual preview the worker embeds in the
        /// <c>SessionDeletionPoisoned</c> OpsEvent. The OpsEvents table truncates the entire
        /// <c>Details</c> column at 4096 chars (<c>TableOpsEventRepository.cs</c>); the full
        /// 50-entry sample plus the other JSON-payload fields can blow past that and leave the
        /// OpsEvent details mid-string-corrupt, which makes the admin UI fall back to raw text
        /// rendering. The OpsEvent is a Telegram-routable summary, not the forensic record —
        /// operators get the full sample from <c>DeletionProgress.LastResidualSampleJson</c>
        /// via the Session Cleanup page's stored-manifest modal.
        /// </summary>
        public const int OpsEventResidualSamplePreviewSize = 5;

        /// <summary>
        /// Per-field length cap (table / pk / rk) inside the OpsEvent residual preview. Without
        /// this, an entry with a 200-char composite PK could on its own consume most of the
        /// 4096-char Details budget. Anything trimmed gets a trailing <c>…</c> marker so the
        /// truncation is visible to operators reading the OpsEvent.
        /// </summary>
        public const int OpsEventResidualKeyMaxChars = 96;

        /// <summary>
        /// Total character budget for the OpsEvent residual preview JSON (before it gets
        /// JSON-string-escaped into the outer <c>Details</c> column). Sized so that, after
        /// escape inflation (~1.5×) + the other Details fields (failureMessage + tenantId +
        /// manifestId + …, ~1500 chars), the resulting <c>Details</c> string stays comfortably
        /// under the 4096-char Azure Table column cap. If the per-entry trims still leave the
        /// JSON over budget, trailing entries are dropped one at a time until it fits.
        /// </summary>
        public const int OpsEventResidualPreviewBudgetChars = 1200;
    }
}
