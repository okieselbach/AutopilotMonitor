using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Background poll-loop for the <c>session-deletion</c> queue (plan §5 PR4). Built on the
    /// shared <see cref="QueuePollingWorker{TEnvelope}"/> with four explicit deviations:
    /// <list type="bullet">
    ///   <item><see cref="BatchSize"/> = <b>1</b> (not 32) — a single cascade can be tens of MB
    ///       and minutes of wall-time; one-at-a-time bounds memory and poison blast radius.</item>
    ///   <item><b>Kill-switch on entry</b> via <see cref="ShouldPauseAsync"/>: every receive checks
    ///       <c>AdminConfiguration.SessionDeletionKillSwitch</c>; when active the worker idles
    ///       without dequeuing.</item>
    ///   <item><b>Heartbeat</b> (<see cref="UseHeartbeat"/>): the base extends message visibility
    ///       while <see cref="SessionDeletionHandler"/> runs so re-delivery can't spawn a parallel
    ///       worker on the same manifest.</item>
    ///   <item><b>Poison hooks</b>: <see cref="BeforePoisonMoveAsync"/> transitions
    ///       <c>Sessions.DeletionState → Poisoned</c> before the move; <see cref="AfterPoisonMoveAsync"/>
    ///       records a <c>SessionDeletionPoisoned</c> OpsEvent enriched with the handler's last-failure
    ///       breadcrumb. (PR-B audit consolidation: the signal lives in OpsEvents, not a per-tenant
    ///       audit; the Sessions row's <c>DeletionState</c> is NOT auto-cleared — operator action via
    ///       restore-from-poisoned is required.)</item>
    /// </list>
    /// </summary>
    public sealed class SessionDeletionWorker : QueuePollingWorker<SessionDeletionEnvelope>
    {
        private readonly SessionDeletionHandler _handler;
        private readonly TableStorageService _storage;
        private readonly AdminConfigurationService _adminConfig;
        private readonly BlobStorageService _blob;
        private readonly OpsEventService _opsEvents;

        public SessionDeletionWorker(
            QueueClientFactory queueFactory,
            SessionDeletionHandler handler,
            TableStorageService storage,
            AdminConfigurationService adminConfig,
            BlobStorageService blob,
            OpsEventService opsEvents,
            ILogger<SessionDeletionWorker> logger)
            : base(queueFactory, Constants.QueueNames.SessionDeletion, logger)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _adminConfig = adminConfig ?? throw new ArgumentNullException(nameof(adminConfig));
            _blob = blob ?? throw new ArgumentNullException(nameof(blob));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
        }

        /// <summary>
        /// Test seam: construct directly with mock <see cref="QueueClient"/> instances so the poll
        /// loop can be exercised against an in-memory queue without Azurite. Mirrors
        /// <see cref="SessionDeletionProducer"/>'s internal test ctor.
        /// </summary>
        internal SessionDeletionWorker(
            QueueClient mainQueue,
            QueueClient poisonQueue,
            SessionDeletionHandler handler,
            TableStorageService storage,
            AdminConfigurationService adminConfig,
            BlobStorageService blob,
            OpsEventService opsEvents,
            ILogger<SessionDeletionWorker> logger,
            TimeSpan? heartbeatInterval = null,
            TimeSpan? pollInterval = null)
            : base(mainQueue, poisonQueue, logger, pollInterval, heartbeatInterval)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _adminConfig = adminConfig ?? throw new ArgumentNullException(nameof(adminConfig));
            _blob = blob ?? throw new ArgumentNullException(nameof(blob));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
        }

        /// <summary>
        /// Explicit deviation from sibling workers — one cascade per receive bounds the worker's
        /// memory footprint (a 35MB snapshot blob × 32 messages = 1.1GB) and limits poison-queue
        /// blast radius on a corruption signal. Plan §5 PR4.
        /// </summary>
        protected override int BatchSize => 1;

        protected override bool UseHeartbeat => true;

        protected override async ValueTask<bool> ShouldPauseAsync(CancellationToken ct)
        {
            // Kill-switch entry guard (plan §1 P8 / §9). When active, the worker does not dequeue —
            // the message stays visible on the main queue until the operator flips the switch back.
            // PR5 finding 1: uncached read so a flip-ON is honored within seconds across all worker
            // instances, not minutes (the cache TTL). One extra storage read per poll interval is
            // fine — the config row is tiny.
            if (await _adminConfig.IsSessionDeletionKillSwitchActiveAsync().ConfigureAwait(false))
            {
                Logger.LogDebug("{Worker}: kill-switch active; idling for {Interval}", WorkerName, ResolvedPollInterval);
                return true;
            }
            return false;
        }

        protected override bool TryValidate(SessionDeletionEnvelope envelope)
            => !string.IsNullOrEmpty(envelope.TenantId)
            && !string.IsNullOrEmpty(envelope.SessionId)
            && !string.IsNullOrEmpty(envelope.ManifestId);

        protected override string DescribeForLog(SessionDeletionEnvelope envelope)
            => $"tenant={envelope.TenantId} session={envelope.SessionId} manifestId={envelope.ManifestId}";

        protected override Task HandleAsync(SessionDeletionEnvelope envelope, CancellationToken ct)
            => _handler.HandleAsync(envelope, ct);

        // ── Poison hooks ─────────────────────────────────────────────────────────

        protected override async Task<bool> BeforePoisonMoveAsync(QueueMessage msg, CancellationToken ct)
        {
            // PR4b E1: transition Sessions.DeletionState → Poisoned BEFORE the poison-queue send.
            // Without this, the row stays at Running (or Queued if poisoned before pickup) and the
            // restore endpoint cannot dispatch into partial-restore mode. Best-effort: if CAS fails
            // we still proceed with the poison-queue move so the original poison flow completes and
            // an audit trail exists (so this hook always returns true).
            var envelope = TryDeserialize(msg);
            if (envelope != null && !string.IsNullOrEmpty(envelope.TenantId) && !string.IsNullOrEmpty(envelope.SessionId))
            {
                await TryTransitionToPoisonedStateAsync(envelope.TenantId, envelope.SessionId, envelope.ManifestId, ct).ConfigureAwait(false);
            }
            return true;
        }

        protected override async Task AfterPoisonMoveAsync(QueueMessage msg, CancellationToken ct)
        {
            var envelope = TryDeserialize(msg);
            if (envelope == null || string.IsNullOrEmpty(envelope.TenantId) || string.IsNullOrEmpty(envelope.SessionId))
            {
                return;
            }

            // PR-B audit consolidation: poisoned cascades surface as a global-scope OpsEvent
            // (operator-bound, Telegram-routable) rather than a per-tenant audit. Tenant admins
            // observe deletion via the lifecycle audits (started / completed / restored).
            //
            // Codex F4 follow-up: pull the handler's last-failure breadcrumb out of the
            // DeletionProgress blob so the OpsEvent carries the root cause (failureType +
            // residual sample), not just queue-side metadata. Blob read is best-effort.
            string? failureType = null;
            string? failureMessage = null;
            int? observedResidualCount = null;
            string? residualSamplePreviewJson = null;
            if (!string.IsNullOrEmpty(envelope.ManifestId))
            {
                try
                {
                    var (progress, _) = await _blob.DownloadDeletionProgressAsync(
                        envelope.TenantId, envelope.SessionId, envelope.ManifestId!, ct)
                        .ConfigureAwait(false);
                    failureType = string.IsNullOrEmpty(progress.LastFailureType) ? null : progress.LastFailureType;
                    failureMessage = string.IsNullOrEmpty(progress.LastFailureMessage) ? null : progress.LastFailureMessage;
                    // Codex F2 round-3: this is the verifier's OBSERVED count (capped per table AND
                    // short-circuited after the first failing table — the real residual count may be
                    // larger). For pre-followup progress blobs lacking the field, fall back to the
                    // sample-array length so the OpsEvent still carries a number.
                    observedResidualCount = progress.LastObservedResidualCount
                        ?? ExtractResidualSampleArrayLength(progress.LastResidualSampleJson);
                    // Codex F2 round-2: shrink the residual sample further before embedding it in the
                    // OpsEvent. The OpsEvents table truncates Details at 4096 chars; the full sample
                    // can blow past that and corrupt the JSON. The Session Cleanup admin page shows
                    // the full sample via the progress blob, so the OpsEvent only needs a preview.
                    residualSamplePreviewJson = ShrinkResidualSampleForOpsEvent(progress.LastResidualSampleJson);
                }
                catch (Exception blobEx)
                {
                    Logger.LogWarning(blobEx,
                        "{Worker}: failed to read DeletionProgress for poison enrichment (tenant={Tenant} session={Session} manifestId={Manifest}) — OpsEvent will lack root-cause data",
                        WorkerName, envelope.TenantId, envelope.SessionId, envelope.ManifestId);
                }
            }

            try
            {
                await _opsEvents.RecordSessionDeletionPoisonedAsync(
                    envelope.TenantId,
                    envelope.SessionId,
                    envelope.ManifestId ?? string.Empty,
                    envelope.Reason ?? string.Empty,
                    msg.MessageId,
                    (int)(msg.DequeueCount - 1),
                    failureType,
                    failureMessage,
                    observedResidualCount,
                    residualSamplePreviewJson).ConfigureAwait(false);
            }
            catch (Exception opsEx)
            {
                Logger.LogError(opsEx,
                    "{Worker}: SessionDeletionPoisoned OpsEvent write failed for tenant={Tenant} session={Session}",
                    WorkerName, envelope.TenantId, envelope.SessionId);
            }
        }

        private static SessionDeletionEnvelope? TryDeserialize(QueueMessage msg)
        {
            try { return JsonConvert.DeserializeObject<SessionDeletionEnvelope>(msg.Body.ToString()); }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// Back-compat fallback used only when <see cref="DeletionProgress.LastObservedResidualCount"/>
        /// is missing (progress blobs written before the field was introduced). Parses the JSON
        /// sample array length and uses it as a best-effort substitute. Returns null on parse errors.
        /// </summary>
        internal static int? ExtractResidualSampleArrayLength(string? residualSampleJson)
        {
            if (string.IsNullOrEmpty(residualSampleJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(residualSampleJson!);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    return doc.RootElement.GetArrayLength();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Tolerate malformed payloads — the breadcrumb itself still goes through.
            }
            return null;
        }

        /// <summary>
        /// Trims the progress-blob's residual sample for safe embedding in the
        /// <c>SessionDeletionPoisoned</c> OpsEvent's <c>Details</c> column. Three-layer defense
        /// against the 4096-char Azure Table truncation: cap entry count, trim each key field, then
        /// drop trailing entries until the serialized JSON fits the budget. Returns null when there's
        /// nothing to embed or the input cannot be parsed.
        /// </summary>
        internal static string? ShrinkResidualSampleForOpsEvent(string? fullSampleJson)
        {
            if (string.IsNullOrEmpty(fullSampleJson)) return null;

            List<ResidualPreviewEntry> entries;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(fullSampleJson!);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return null;
                }
                entries = new List<ResidualPreviewEntry>();
                var max = DeletionProgressConstants.OpsEventResidualSamplePreviewSize;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (entries.Count >= max) break;
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    entries.Add(new ResidualPreviewEntry
                    {
                        Table = TrimResidualKeyField(ReadStringProperty(element, "table")),
                        Pk    = TrimResidualKeyField(ReadStringProperty(element, "pk")),
                        Rk    = TrimResidualKeyField(ReadStringProperty(element, "rk")),
                    });
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }

            // Total-budget pass: serialize, then drop trailing entries until under the cap. Worst
            // case (one over-budget entry) ends at "[]" — still valid JSON; the full sample remains
            // in the progress blob.
            var budget = DeletionProgressConstants.OpsEventResidualPreviewBudgetChars;
            while (true)
            {
                var serialized = SerializeResidualPreview(entries);
                if (serialized.Length <= budget || entries.Count == 0) return serialized;
                entries.RemoveAt(entries.Count - 1);
            }
        }

        private static string SerializeResidualPreview(List<ResidualPreviewEntry> entries)
        {
            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var e in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("table", e.Table);
                    writer.WriteString("pk", e.Pk);
                    writer.WriteString("rk", e.Rk);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string TrimResidualKeyField(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var max = DeletionProgressConstants.OpsEventResidualKeyMaxChars;
            if (raw!.Length <= max) return raw;
            // -1 makes room for the trailing ellipsis so the marked-truncated string fits in the
            // same budget as a max-length untrimmed one.
            return raw.Substring(0, max - 1) + "…";
        }

        private static string? ReadStringProperty(System.Text.Json.JsonElement obj, string propertyName)
        {
            if (!obj.TryGetProperty(propertyName, out var value)) return null;
            return value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        }

        private struct ResidualPreviewEntry
        {
            public string Table;
            public string Pk;
            public string Rk;
        }

        /// <summary>
        /// PR4b E1 + PR4c F5: CAS Sessions.DeletionState to Poisoned with manifestId pre-check.
        /// Tries <c>Running → Poisoned</c> first (the common case), falls back to
        /// <c>Queued → Poisoned</c> (poisoned before pickup ever succeeded). Logs warning on
        /// persistent CAS failure but does NOT throw — the poison-queue move + audit must still
        /// complete. PR4c F5: before any CAS, read the row's <c>PendingDeletionManifestId</c> and
        /// compare to the envelope's <c>manifestId</c>; a stale envelope must not flip a fresh
        /// active cascade.
        /// </summary>
        private async Task TryTransitionToPoisonedStateAsync(string tenantId, string sessionId, string? manifestId, CancellationToken ct)
        {
            var row = await _storage.GetSessionRowAsync(tenantId, sessionId, ct).ConfigureAwait(false);
            if (row == null)
            {
                Logger.LogInformation(
                    "{Worker}: Sessions row gone for tenant={Tenant} session={Session} (cascade already tombstoned by another worker?) — skipping state transition",
                    WorkerName, tenantId, sessionId);
                return;
            }

            var currentPending = row.GetString("PendingDeletionManifestId");
            if (!string.Equals(currentPending, manifestId, StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "{Worker}: stale poison envelope for tenant={Tenant} session={Session} — envelope.ManifestId={Envelope} but row.PendingDeletionManifestId={Pending}; skipping state transition (active cascade is a different manifest)",
                    WorkerName, tenantId, sessionId, manifestId, currentPending);
                return;
            }

            // First attempt: Running → Poisoned (the expected case after a successful pickup).
            var cas = await _storage.CasSetSessionDeletionStateAsync(
                tenantId, sessionId,
                fromState: SessionDeletionState.Running,
                toState: SessionDeletionState.Poisoned,
                newManifestId: null,
                ct).ConfigureAwait(false);
            if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.Updated)
            {
                Logger.LogInformation(
                    "{Worker}: CAS Running→Poisoned succeeded for tenant={Tenant} session={Session} manifestId={ManifestId}",
                    WorkerName, tenantId, sessionId, manifestId);
                return;
            }

            // Fallback: Queued → Poisoned (poisoned before pickup ever succeeded).
            if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.WrongState
                && cas.CurrentState == SessionDeletionState.Queued)
            {
                cas = await _storage.CasSetSessionDeletionStateAsync(
                    tenantId, sessionId,
                    fromState: SessionDeletionState.Queued,
                    toState: SessionDeletionState.Poisoned,
                    newManifestId: null,
                    ct).ConfigureAwait(false);
                if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.Updated)
                {
                    Logger.LogInformation(
                        "{Worker}: CAS Queued→Poisoned succeeded for tenant={Tenant} session={Session} manifestId={ManifestId}",
                        WorkerName, tenantId, sessionId, manifestId);
                    return;
                }
            }

            // Persistent CAS failure — log and continue so the poison-queue move + audit still fire.
            Logger.LogWarning(
                "{Worker}: could not transition Sessions.DeletionState to Poisoned for tenant={Tenant} session={Session} manifestId={ManifestId} outcome={Outcome} currentState={State}; restore endpoint may reject with 409 \"active_cascade\" until operator manually intervenes.",
                WorkerName, tenantId, sessionId, manifestId, cas.Outcome, cas.CurrentState);
        }
    }
}
