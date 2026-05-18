using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Phase 2 of the tenant-offboarding cascade plan (Rev 9). Drives a queued
    /// <see cref="TenantOffboardingEnvelope"/> through:
    /// <list type="number">
    ///   <item>2.A — History/Pointer/Marker → InProgress; Rev-9-F1 Drain-Skip-Gate when DrainCompletedAt is set.</item>
    ///   <item>2.B — fail-loud session enumeration + per-session cascade enqueue + Expectations-Blob upload.</item>
    ///   <item>2.C — drain predicate against Expectations; D2 self-re-enqueue with 2-min delay up to 60 polls.</item>
    ///   <item>2.D — retain-counter + SafeWipe over all tenant-scoped tables (variants A/B/C).</item>
    ///   <item>2.E — blob-prefix cleanup of <c>deletion-manifests/{tenantId}/</c>.</item>
    ///   <item>2.D-pass-2 — re-SafeWipe AuditLogs as defence-in-depth.</item>
    ///   <item>2.F — delete the TenantConfiguration row.</item>
    ///   <item>2.G — Completed state on History/Pointer/Marker; fail-soft Expectations-Blob delete.</item>
    ///   <item>2.H — global-tenant audit row.</item>
    ///   <item>2.I — <c>TenantOffboarded</c> OpsEvent.</item>
    /// </list>
    /// All Phase 2.D-G operations are idempotent (ETag.All + 404-fallback in SafeWipe, fail-soft
    /// blob delete in 2.G, last-writer-wins on history) so the Rev-9-F1 resume from a crash
    /// between 2.E and 2.G is safe even though the progress-blob predicate cannot run any more.
    /// </summary>
    public class TenantOffboardingHandler
    {
        /// <summary>Cap on drain self-re-enqueue iterations (~2h at 2-min visibility delay).</summary>
        public const int MaxDrainPolls = 60;

        /// <summary>Delay between drain self-polls when expectations are not yet satisfied.</summary>
        internal static readonly TimeSpan DrainPollDelay = TimeSpan.FromMinutes(2);

        /// <summary>Maximum CasExhausted retries per session before fail-closing the offboarding.</summary>
        public const int MaxCasRetriesPerSession = 3;

        /// <summary>Throttle between per-session cascade enqueues so a 10k-session tenant doesn't burst.</summary>
        internal static readonly TimeSpan PerSessionEnqueueThrottle = TimeSpan.FromMilliseconds(50);

        private static readonly Dictionary<string, string> EmptyDetails = new(StringComparer.Ordinal);

        // Plan §6.2 — exact tenant-PK wipes (Variant A).
        private static readonly string[] TenantPartitionTables =
        {
            Constants.TableNames.AuditLogs,
            Constants.TableNames.UsageMetrics,
            Constants.TableNames.UserActivity,
            Constants.TableNames.AppInstallSummaries,
            Constants.TableNames.TenantAdmins,
            Constants.TableNames.BootstrapSessions, // main rows; CodeLookup discriminator below
            Constants.TableNames.BlockedDevices,
            Constants.TableNames.RuleStates,        // runtime state — NOT customs (kept; §6.7)
            Constants.TableNames.TenantNotifications,
            Constants.TableNames.HardwareRejectionNotificationTracker,
            Constants.TableNames.SlaTenantStatus,
            Constants.TableNames.DeviceSnapshot,
            Constants.TableNames.EventSessionIndex,
            Constants.TableNames.DistressReports,
            Constants.TableNames.SoftwareInventory,
            Constants.TableNames.SessionInventoryContributions,
            Constants.TableNames.SessionTombstones,
            Constants.TableNames.PreviewWhitelist,
        };

        // Plan §6.4 — composite-PK "{tenantId}_..." wipes (Variant A range).
        private static readonly string[] CompositePartitionTables =
        {
            Constants.TableNames.Events,
            Constants.TableNames.RuleResults,
            Constants.TableNames.VulnerabilityReports,
            Constants.TableNames.Signals,
            Constants.TableNames.DecisionTransitions,
            Constants.TableNames.EventTypeIndex,
            Constants.TableNames.CveIndex,
            Constants.TableNames.SessionsByTerminal,
            Constants.TableNames.SessionsByStage,
            Constants.TableNames.DeadEndsByReason,
            Constants.TableNames.ClassifierVerdictsByIdLevel,
            Constants.TableNames.SignalsByKind,
        };

        // Plan §6.3.1 — Variant B (Discriminator + TenantId property).
        // (table, discriminator)
        private static readonly (string Table, string Discriminator)[] DiscriminatorTables =
        {
            (Constants.TableNames.BootstrapSessions, "CodeLookup"),
            (Constants.TableNames.SessionReports, "reports"),
            (Constants.TableNames.PreviewConfig, "Feedback"),
        };

        // Plan §6.3.2 — Variant C (TenantId property only).
        private static readonly string[] PropertyOnlyTables =
        {
            Constants.TableNames.UserUsageLog,
        };

        // PR3.B plan §3 — Customs rules tables: archive each row to
        // TenantOffboardingCustomsArchive (per-run partition) THEN wipe the source so the
        // tenant's originals are gone. Counters in OffboardingHistory are recomputed from
        // the archive table per run + table to be crash-resume safe.
        private static readonly (string Table, string Field)[] ArchivedRuleTables =
        {
            (Constants.TableNames.GatherRules, nameof(OffboardingHistoryEntry.CustomGatherRulesArchived)),
            (Constants.TableNames.AnalyzeRules, nameof(OffboardingHistoryEntry.CustomAnalyzeRulesArchived)),
            (Constants.TableNames.ImeLogPatterns, nameof(OffboardingHistoryEntry.ImeLogPatternOverridesArchived)),
        };

        /// <summary>
        /// Maximum number of archive-then-wipe iterations per rules table before the
        /// post-wipe verify re-find triggers fail-closed with <c>customs_arrival_race</c>.
        /// First pass handles the normal case; passes 2 + 3 absorb writes that landed
        /// during the drain barrier (a warm function-host with a stale TenantConfig
        /// cache could still write between Phase 1 commit and Phase 2 pickup).
        /// </summary>
        internal const int ArchiveIterationCap = 3;

        private readonly IOffboardingAuditRepository _auditRepo;
        private readonly OffboardingSessionEnumerator _enumerator;
        private readonly ISessionDeletionEnqueuer _cascadeEnqueuer;
        private readonly IOffboardingExpectationsStore _expectations;
        private readonly IDeletionProgressDrainProbe _drainProbe;
        private readonly SafeWipeService _safeWipe;
        private readonly TableStorageService _storage;
        private readonly IMaintenanceRepository _maintenance;
        private readonly ITenantOffboardingEnqueuer _reEnqueuer;
        private readonly OpsEventService _opsEvents;
        private readonly ITenantCustomsArchiveRepository _customsArchive;
        private readonly ILogger<TenantOffboardingHandler> _logger;

        public TenantOffboardingHandler(
            IOffboardingAuditRepository auditRepo,
            OffboardingSessionEnumerator enumerator,
            ISessionDeletionEnqueuer cascadeEnqueuer,
            IOffboardingExpectationsStore expectations,
            IDeletionProgressDrainProbe drainProbe,
            SafeWipeService safeWipe,
            TableStorageService storage,
            IMaintenanceRepository maintenance,
            ITenantOffboardingEnqueuer reEnqueuer,
            OpsEventService opsEvents,
            ITenantCustomsArchiveRepository customsArchive,
            ILogger<TenantOffboardingHandler> logger)
        {
            _auditRepo = auditRepo;
            _enumerator = enumerator;
            _cascadeEnqueuer = cascadeEnqueuer;
            _expectations = expectations;
            _drainProbe = drainProbe;
            _safeWipe = safeWipe;
            _storage = storage;
            _maintenance = maintenance;
            _reEnqueuer = reEnqueuer;
            _opsEvents = opsEvents;
            _customsArchive = customsArchive;
            _logger = logger;
        }

        public async Task HandleAsync(TenantOffboardingEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            var tenantId = envelope.TenantId.ToLowerInvariant();

            // 2.A — Load history. Idempotent re-pickup: Completed/Failed → return early.
            var history = await _auditRepo.TryGetHistoryAsync(envelope.HistoryRowKey, ct)
                ?? throw new InvalidOperationException(
                    $"OffboardingHistory row missing for envelope tenant={tenantId} history={envelope.HistoryRowKey}");

            if (history.Status == "Completed")
            {
                _logger.LogInformation("Tenant offboarding already Completed — re-pickup is a no-op. tenant={Tenant}", tenantId);
                return;
            }
            if (history.Status == "Failed")
            {
                _logger.LogWarning(
                    "Tenant offboarding History is Failed (phase={Phase}) — operator action required, worker returns. tenant={Tenant}",
                    history.ErrorMessage ?? "unknown", tenantId);
                return;
            }

            // 2.A — first pickup transitions History/Pointer/Marker to InProgress.
            if (history.Status == "Initiated")
            {
                history.Status = "InProgress";
                await _auditRepo.UpsertHistoryAsync(history, ct);
                await TransitionStatusAsync(tenantId, history.RowKey, "InProgress", ct);
            }

            // 2.A.5 — Rev-9-F1 Drain-Skip-Gate. After the first successful drain, DrainCompletedAt
            // is stamped on the history row and all subsequent phases (2.D-G) are idempotent.
            // A crash between 2.E (blob wipe) and 2.G (status flip) would otherwise leave the
            // re-pickup looking for progress blobs that 2.E just deleted — fail-closed with
            // expectations_missing, which is wrong (the offboard actually succeeded in spirit).
            if (history.DrainCompletedAt != null)
            {
                _logger.LogInformation(
                    "Drain-Skip-Gate: History.DrainCompletedAt={DrainedAt} set; running post-drain phases idempotently. tenant={Tenant}",
                    history.DrainCompletedAt, tenantId);
                await RunPostDrainPhasesAsync(history, tenantId, ct);
                return;
            }

            // 2.B — Ensure Expectations blob exists. The two history markers EnumerationStartedAt
            // (ESA) and EnumerationCompletedBeforeUpload (ECBU) disambiguate three resume modes
            // when the blob is missing:
            //   • ESA null,   ECBU null  → first try → stamp ESA, enumerate, stamp ECBU, upload.
            //   • ESA set,    ECBU null  → mid-enumeration crash → fail-closed expectations_missing
            //                              (re-enumerating against a mid-mutation tenant is unsafe).
            //   • ESA set,    ECBU set   → upload failure → retry enumerate+upload (idempotent,
            //                              SessionDeletionProducer returns AlreadyInFlight).
            // Full decision table lives on EnsureExpectationsBlobAsync. Plan §7.4 step 3.
            var ensureFail = await EnsureExpectationsBlobAsync(history, tenantId, ct);
            if (ensureFail != null)
            {
                await FailAsync(history, tenantId, ensureFail,
                    $"Drain fail-closed: {ensureFail}", ct);
                throw new InvalidOperationException(
                    $"TenantOffboarding fail-closed for tenant={tenantId} phase={ensureFail}");
            }

            // 2.C — Drain probe. Three outcomes: drain OK (proceed), drain not yet (re-enqueue
            // with 2-min delay up to MaxDrainPolls), fail-closed (throw → worker poisons).
            var (drainOk, failReason) = await EvaluateDrainAsync(history, tenantId, ct);

            if (failReason != null)
            {
                await FailAsync(history, tenantId, failReason, $"Drain fail-closed: {failReason}", ct);
                throw new InvalidOperationException(
                    $"TenantOffboarding fail-closed for tenant={tenantId} phase={failReason}");
            }

            if (!drainOk)
            {
                if (envelope.DrainPollCount + 1 >= MaxDrainPolls)
                {
                    await FailAsync(history, tenantId, "drain_timeout",
                        $"Drain did not settle within {MaxDrainPolls} polls (~{MaxDrainPolls * DrainPollDelay.TotalMinutes:0}min)", ct);
                    throw new InvalidOperationException(
                        $"TenantOffboarding drain_timeout for tenant={tenantId}");
                }

                var next = new TenantOffboardingEnvelope
                {
                    EnvelopeVersion = envelope.EnvelopeVersion,
                    TenantId = envelope.TenantId,
                    HistoryPartitionKey = envelope.HistoryPartitionKey,
                    HistoryRowKey = envelope.HistoryRowKey,
                    InitiatedBy = envelope.InitiatedBy,
                    InitiatedAt = envelope.InitiatedAt,
                    EnqueuedAt = DateTime.UtcNow,
                    DrainPollCount = envelope.DrainPollCount + 1,
                };
                await _reEnqueuer.EnqueueAsync(next, DrainPollDelay, ct);
                _logger.LogInformation(
                    "Drain not yet settled — re-enqueued tenant={Tenant} poll={Poll}/{Max} delay={Delay}",
                    tenantId, next.DrainPollCount, MaxDrainPolls, DrainPollDelay);
                return;
            }

            // Drain OK — Rev-9-F1: stamp DrainCompletedAt BEFORE Phase 2.D begins.
            history.DrainCompletedAt = DateTime.UtcNow;
            await _auditRepo.UpsertHistoryAsync(history, ct);

            await RunPostDrainPhasesAsync(history, tenantId, ct);
        }

        // ── Phase 2.A status transitions ────────────────────────────────────────

        private async Task TransitionStatusAsync(string tenantId, string historyRowKey, string newStatus, CancellationToken ct)
        {
            // Marker — read existing fields so we don't clobber InitiatedAt/InitiatedBy.
            var marker = await _auditRepo.TryGetMarkerAsync(tenantId, ct);
            if (marker != null)
            {
                marker.Status = newStatus;
                await _auditRepo.UpsertMarkerAsync(marker, ct);
            }

            // Pointer — read-modify-write with ETag-CAS retry (Plan §4.4).
            await UpdatePointerWithRetryAsync(tenantId, historyRowKey, newStatus, ct);
        }

        private async Task UpdatePointerWithRetryAsync(string tenantId, string historyRowKey, string newStatus, CancellationToken ct)
        {
            const int maxRetries = 5;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var (pointer, etag) = await _auditRepo.TryGetByTenantPointerAsync(tenantId, ct);
                if (pointer == null || etag == null)
                {
                    _logger.LogWarning(
                        "Pointer missing for tenant={Tenant} while transitioning to {Status} — skip (graceful degradation, Plan §4.4)",
                        tenantId, newStatus);
                    return;
                }
                pointer.LatestStatus = newStatus;
                pointer.LatestUpdatedAt = DateTime.UtcNow;
                if (!string.Equals(pointer.LatestHistoryRowKey, historyRowKey, StringComparison.Ordinal))
                {
                    // Pointer points at a different attempt — concurrent re-offboard. Skip.
                    _logger.LogWarning(
                        "Pointer.LatestHistoryRowKey={PointerHistory} != envelope.HistoryRowKey={Envelope} — skipping pointer transition",
                        pointer.LatestHistoryRowKey, historyRowKey);
                    return;
                }
                try
                {
                    await _auditRepo.UpdateByTenantPointerWithEtagAsync(pointer, etag, ct);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    // Concurrent writer beat us — re-read + retry.
                }
            }
            _logger.LogWarning(
                "Pointer ETag-CAS exhausted for tenant={Tenant} status={Status} — giving up (Plan §4.4 graceful degradation)",
                tenantId, newStatus);
        }

        // ── Phase 2.B — initial enqueue + Expectations persistence ──────────────

        /// <summary>
        /// Returns <c>null</c> on success (blob exists / blob just created). Returns a
        /// FailedPhase string when the handler must fail-closed without enumerating.
        /// <para>
        /// Decision table (Plan §7.4 step 3 + Review-Fix Finding 2):
        /// </para>
        /// <list type="table">
        ///   <listheader><term>blob</term><term>ESA</term><term>ECBU</term><description>Action</description></listheader>
        ///   <item><term>exists</term><term>—</term><term>—</term><description>Resume path; return null.</description></item>
        ///   <item><term>missing</term><term>null</term><term>null</term><description>First try; stamp ESA, enumerate, stamp ECBU, upload.</description></item>
        ///   <item><term>missing</term><term>set</term><term>null</term><description>Mid-enumeration crash → fail-closed <c>expectations_missing</c>.</description></item>
        ///   <item><term>missing</term><term>set</term><term>set</term><description>Upload failure → retry enumerate (idempotent — producer returns AlreadyInFlight) + re-upload.</description></item>
        /// </list>
        /// </summary>
        private async Task<string?> EnsureExpectationsBlobAsync(
            OffboardingHistoryEntry history, string tenantId, CancellationToken ct)
        {
            var (existing, _) = await _expectations.TryDownloadAsync(tenantId, history.RowKey, ct);
            if (existing != null)
            {
                _logger.LogInformation(
                    "Expectations blob already exists for tenant={Tenant} (count={Count}, enumComplete={EnumComplete}) — resume path",
                    tenantId, existing.Expectations.Count, existing.EnumerationCompleted);
                return null;
            }

            // Mid-enumeration crash: prior pickup stamped ESA but never reached ECBU. Refuse
            // to re-enumerate against a possibly mid-mutation tenant.
            if (history.EnumerationStartedAt != null
                && history.EnumerationCompletedBeforeUpload == null)
            {
                _logger.LogError(
                    "Expectations blob missing, EnumerationStartedAt={StartedAt}, ECBU null — fail-closed (mid-enumeration crash). tenant={Tenant}",
                    history.EnumerationStartedAt, tenantId);
                return "expectations_missing";
            }

            // Either first try (ESA null) or upload-retry (ESA + ECBU set, blob missing).
            // First-try only: stamp ESA before touching the enumerator so a crash here lands
            // in the fail-closed branch above on the next pickup.
            if (history.EnumerationStartedAt == null)
            {
                history.EnumerationStartedAt = DateTime.UtcNow;
                await _auditRepo.UpsertHistoryAsync(history, ct);
            }
            else
            {
                _logger.LogInformation(
                    "Upload-retry path for tenant={Tenant} (ESA={Esa}, ECBU={Ecbu}) — re-running enumerate+upload",
                    tenantId, history.EnumerationStartedAt, history.EnumerationCompletedBeforeUpload);
            }

            // Fail-loud enumeration. Storage exceptions propagate; the queue worker poisons or
            // retries via visibility timeout. NEVER catch here — that would re-introduce the
            // Rev-5-F3 silent-empty bug.
            var collected = new List<OffboardingExpectation>();
            var actor = new DeletionActor { Type = "system", Actor = "System.TenantOffboarding" };

            await foreach (var sessionId in _enumerator.EnumerateAsync(tenantId, ct))
            {
                ct.ThrowIfCancellationRequested();
                var result = await _cascadeEnqueuer.EnqueueAsync(
                    tenantId, sessionId, reason: "tenant_offboard", actor: actor,
                    retentionContext: null, cancellationToken: ct);

                collected.Add(new OffboardingExpectation
                {
                    SessionId = sessionId,
                    ManifestId = result.ManifestId,
                    Outcome = result.Outcome.ToString(),
                    RetryCount = 0,
                });

                if (PerSessionEnqueueThrottle > TimeSpan.Zero)
                {
                    try { await Task.Delay(PerSessionEnqueueThrottle, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                }
            }

            // Enumeration finished cleanly. Persist ECBU BEFORE the upload so a subsequent
            // upload failure is recognized as upload-retry-eligible on the next pickup.
            // On retry pickups ECBU may already be set — UpsertHistoryAsync is idempotent.
            if (history.EnumerationCompletedBeforeUpload == null)
            {
                history.EnumerationCompletedBeforeUpload = DateTime.UtcNow;
                await _auditRepo.UpsertHistoryAsync(history, ct);
            }

            // Plan Rev-7-F2: EnumerationCompleted on the payload = the in-blob flag the drain
            // probe checks. We can only set it true here because the iterator above ran to
            // completion without throwing (the throw-path skipped this whole block).
            var payload = new OffboardingExpectations
            {
                SchemaVersion = 1,
                TenantId = tenantId,
                HistoryRowKey = history.RowKey,
                CreatedAt = DateTime.UtcNow,
                EnumerationCompleted = true,
                EnumeratedSessionCount = collected.Count,
                Expectations = collected,
            };

            var uploaded = await _expectations.TryUploadInitialAsync(payload, ct);
            if (!uploaded)
            {
                // Race: a parallel worker beat us. Their blob is the source of truth.
                _logger.LogInformation(
                    "Expectations blob race lost for tenant={Tenant} history={History} — using existing blob",
                    tenantId, history.RowKey);
            }
            else
            {
                history.CascadeSessionsEnqueued = collected.Count;
                await _auditRepo.UpsertHistoryAsync(history, ct);
            }
            return null;
        }

        // ── Phase 2.C — drain predicate ─────────────────────────────────────────

        private async Task<(bool DrainOk, string? FailReason)> EvaluateDrainAsync(
            OffboardingHistoryEntry history, string tenantId, CancellationToken ct)
        {
            var (payload, etag) = await _expectations.TryDownloadAsync(tenantId, history.RowKey, ct);
            if (payload == null) return (false, "expectations_missing");
            if (etag == null) return (false, "expectations_missing");
            if (payload.SchemaVersion != 1) return (false, "expectations_corrupt");
            if (!payload.EnumerationCompleted) return (false, "enumeration_incomplete");
            if (payload.Expectations.Count != payload.EnumeratedSessionCount)
                return (false, "expectations_size_mismatch");

            var allSatisfied = true;
            var dirty = false;
            var actor = new DeletionActor { Type = "system", Actor = "System.TenantOffboarding" };

            for (int i = 0; i < payload.Expectations.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var expectation = payload.Expectations[i];

                switch (expectation.Outcome)
                {
                    case nameof(SessionDeletionEnqueueOutcome.KillSwitchActive):
                        return (false, "killswitch");

                    case nameof(SessionDeletionEnqueueOutcome.Poisoned):
                        return (false, "poisoned");

                    case nameof(SessionDeletionEnqueueOutcome.SessionNotFound):
                        // Session was already gone when we tried to enqueue — nothing to drain.
                        continue;

                    case nameof(SessionDeletionEnqueueOutcome.CasExhausted):
                        if (expectation.RetryCount >= MaxCasRetriesPerSession)
                            return (false, "cas_exhausted");

                        // Retry the per-session enqueue. Update outcome + counter in-place.
                        var retry = await _cascadeEnqueuer.EnqueueAsync(
                            tenantId, expectation.SessionId, reason: "tenant_offboard", actor: actor,
                            retentionContext: null, cancellationToken: ct);
                        expectation.Outcome = retry.Outcome.ToString();
                        expectation.ManifestId = retry.ManifestId;
                        expectation.RetryCount++;
                        dirty = true;
                        allSatisfied = false; // re-evaluate next drain cycle
                        break;

                    case nameof(SessionDeletionEnqueueOutcome.Enqueued):
                    case nameof(SessionDeletionEnqueueOutcome.AlreadyInFlight):
                        if (string.IsNullOrEmpty(expectation.ManifestId))
                        {
                            // Rev-8-F3 — AlreadyInFlight with null ManifestId is a Preparing-without-
                            // snapshot state. There's no progress blob to drain against; fail-closed.
                            return (false, "alreadyinflight_no_manifest");
                        }
                        var done = await _drainProbe.IsCascadeCompletedAsync(
                            tenantId, expectation.SessionId, expectation.ManifestId, ct);
                        if (!done) allSatisfied = false;
                        break;

                    default:
                        _logger.LogWarning(
                            "Unknown Expectation.Outcome '{Outcome}' for tenant={Tenant} session={Session}",
                            expectation.Outcome, tenantId, expectation.SessionId);
                        allSatisfied = false;
                        break;
                }
            }

            if (dirty)
            {
                try
                {
                    await _expectations.UpdateWithEtagCasAsync(payload, etag, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    // A parallel worker re-evaluated and wrote first. Their version wins; the
                    // next drain cycle will pick up the latest state. We still report
                    // allSatisfied=false so this cycle re-enqueues.
                    _logger.LogInformation(
                        "Expectations CAS 412 for tenant={Tenant} — parallel worker won, will re-read next cycle",
                        tenantId);
                    return (false, null);
                }
            }

            return (allSatisfied, null);
        }

        // ── Phases 2.D / 2.E / 2.D-pass-2 / 2.F / 2.G / 2.H / 2.I ───────────────

        private async Task RunPostDrainPhasesAsync(
            OffboardingHistoryEntry history, string tenantId, CancellationToken ct)
        {
            // 2.D-archive (PR3.B §3) — Archive then wipe GatherRules / AnalyzeRules /
            // ImeLogPatterns. Fail-loud: a row that cannot be archived must NOT be wiped.
            // After the third iteration if the source still has rows, throw with
            // FailedPhase="customs_arrival_race" — marker stays Failed for operator action.
            try
            {
                await ArchiveAndWipeCustomsRulesAsync(history, tenantId, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("customs_arrival_race", StringComparison.Ordinal))
            {
                await FailAsync(history, tenantId, "customs_arrival_race", ex.Message, ct);
                throw; // worker dequeues + eventually poisons
            }

            // 2.D-pass-1 — SafeWipe all tenant-scoped tables, including AuditLogs.
            var deletedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            await SafeWipeAllTenantTablesAsync(deletedCounts, tenantId, ct);

            // 2.E — Blob-prefix wipe (deletion-manifests only; offboarding-state stays).
            int deletedBlobs = 0;
            try
            {
                deletedBlobs = await _safeWipe.WipeBlobsByTenantPrefixAsync(
                    Constants.BlobContainers.DeletionManifests, tenantId, ct);
            }
            catch (SafeWipeVerificationException)
            {
                throw; // unrecoverable — let worker poison
            }

            // 2.D-pass-2 — Re-wipe AuditLogs as defence in depth against late audits that may
            // have landed between Phase 2.D-pass-1 and now (the cascade-handler reorder from
            // PR1.5 closes the main window, but a struggling SignalR/Audit retry could still
            // bring a row in late).
            var auditPass2 = await _safeWipe.WipeByExactPartitionAsync(Constants.TableNames.AuditLogs, tenantId, ct);
            if (auditPass2 > 0)
            {
                _logger.LogInformation(
                    "Audit pass-2 swept {Count} late row(s) for tenant={Tenant}", auditPass2, tenantId);
                deletedCounts[Constants.TableNames.AuditLogs] =
                    deletedCounts.TryGetValue(Constants.TableNames.AuditLogs, out var existing) ? existing + auditPass2 : auditPass2;
            }

            // ── 2.G — Order: side-effects FIRST, then History terminal write LAST ────
            //
            // Note (PR3.B review): the legacy 2.F TenantConfiguration-delete used to sit
            // BEFORE 2.G. That left a tiny window where a /api/auth/me race could re-create
            // a fresh Default-Disabled=false TenantConfiguration before the Completed
            // state landed — letting an offboarded tenant slip back through. The delete
            // has moved to AFTER 2.I (see "2.F-final" below) so the entire Phase 2 sequence
            // runs against an existing Disabled=true row, and only once everything is
            // committed do we drop the row. Self-service re-onboarding still works because
            // the user's NEXT /api/auth/me after 2.F-final sees no row → auto-create-default.
            // Plan §3 Phase 2.G + Review-Fix Finding 3: a crash between History=Completed and
            // the marker/pointer/blob/audit/ops writes would otherwise make the next pickup
            // return early (terminal-status check) and leave the side-effects dangling. By
            // flipping History last we keep DrainCompletedAt as the resume anchor — re-pickup
            // skips drain and re-runs the (idempotent) side-effect chain.

            // Pre-populate the in-memory history with the final values so the LAST write below
            // commits the whole snapshot atomically.
            var completedAt = DateTime.UtcNow;
            history.CompletedAt = completedAt;
            history.DeletedRowCountsJson = JsonSerializer.Serialize(deletedCounts);
            history.TotalRowsDeleted = deletedCounts.Values.Sum();
            history.DeletedBlobCount = deletedBlobs;

            // Side-effect 1: Marker → Completed (must happen FIRST so a crash here leaves
            // History=InProgress; the next pickup re-runs the idempotent post-drain chain).
            var marker = await _auditRepo.TryGetMarkerAsync(tenantId, ct);
            if (marker != null)
            {
                marker.Status = "Completed";
                marker.CompletedAt = completedAt;
                await _auditRepo.UpsertMarkerAsync(marker, ct);
            }

            // Side-effect 2: Pointer → Completed (ETag-CAS retry).
            await UpdatePointerWithRetryAsync(tenantId, history.RowKey, "Completed", ct);

            // Side-effect 3: Expectations blob delete (fail-soft; MarkerCleanupFunction retries).
            try
            {
                await _expectations.DeleteAsync(tenantId, history.RowKey, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Expectations blob delete failed for tenant={Tenant} history={History} — MarkerCleanupFunction will retry",
                    tenantId, history.RowKey);
            }

            // Side-effect 4: Global-tenant audit row (persists across the wiped tenant audits).
            await _maintenance.LogAuditEntryAsync(
                Constants.AuditGlobalTenantId,
                action: "DELETE",
                entityType: "Tenant",
                entityId: tenantId,
                performedBy: history.InitiatedBy,
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Action"] = "Offboard",
                    ["Phase"] = "Completed",
                    ["DomainName"] = history.DomainName ?? string.Empty,
                    ["TotalRowsDeleted"] = (history.TotalRowsDeleted ?? 0).ToString(),
                    ["DeletedBlobCount"] = (history.DeletedBlobCount ?? 0).ToString(),
                    ["CascadeSessionsEnqueued"] = (history.CascadeSessionsEnqueued ?? 0).ToString(),
                    ["HistoryRowKey"] = history.RowKey,
                });

            // Side-effect 5: OpsEvent. Reuses the existing TenantOffboarded helper so the
            // dashboard category cell stays "Tenant" / event "TenantOffboarded".
            await _opsEvents.RecordTenantOffboardedAsync(
                tenantId, history.InitiatedBy, deletedCounts, history.DomainName);

            // COMMIT: History → Completed LAST. Until this write lands the next pickup will
            // see Status=InProgress + DrainCompletedAt set and re-run all of the above
            // idempotently (duplicate audit/OpsEvent emit is preferable to dangling state).
            history.Status = "Completed";
            await _auditRepo.UpsertHistoryAsync(history, ct);

            // ── 2.F-final — TenantConfiguration delete (PR3.B Codex Finding 2 reorder) ──
            //
            // This is the LAST step in Phase 2. By now Marker / Pointer / History all say
            // "Completed" and the Disabled=true auth-gate has carried the tenant through the
            // entire async wipe. Deleting the row here means the next /api/auth/me for this
            // tenant will see no row → TenantConfigurationService.GetConfigurationAsync's
            // auto-create-default path kicks in, giving the same user a fresh, clean tenant
            // (the legitimate self-service re-onboarding case).
            //
            // Fail-soft: if the delete fails, Marker stays Completed and the
            // OffboardingMarkerCleanupFunction's TenantConfig-sweep (defense-in-depth) runs
            // every 2h and finishes the cleanup. The tenant cannot re-onboard until that
            // sweep lands — acceptable, because Disabled=true still applies meanwhile.
            try
            {
                var tenantConfigDeleted = await _safeWipe.WipeByExactPartitionAsync(
                    Constants.TableNames.TenantConfiguration, tenantId, ct);
                if (tenantConfigDeleted > 0)
                {
                    _logger.LogInformation(
                        "Phase 2.F-final: deleted TenantConfiguration row(s)={Count} for tenant={Tenant}",
                        tenantConfigDeleted, tenantId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Phase 2.F-final TenantConfiguration delete failed for tenant={Tenant} — MarkerCleanupFunction's TenantConfig-sweep will retry",
                    tenantId);
            }

            _logger.LogInformation(
                "Tenant offboarding Completed: tenant={Tenant} totalRows={Rows} blobs={Blobs} sessions={Sessions} archiveGather={G} archiveAnalyze={A} archivePatterns={P}",
                tenantId,
                history.TotalRowsDeleted, history.DeletedBlobCount, history.CascadeSessionsEnqueued,
                history.CustomGatherRulesArchived, history.CustomAnalyzeRulesArchived, history.ImeLogPatternOverridesArchived);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// PR3.B §3 — Phase 2.D-archive. For each rules table, archive every row to
        /// <see cref="ITenantCustomsArchiveRepository"/> THEN delete that exact (PK, RK)
        /// from the source. Up to <see cref="ArchiveIterationCap"/> iterations to absorb
        /// late writes that may have landed during the drain barrier; if the source still
        /// has rows after the cap, throw <see cref="InvalidOperationException"/> so the
        /// caller fail-closes the offboarding with <c>FailedPhase="customs_arrival_race"</c>.
        /// <para>
        /// Crash-safety: the archive upsert is idempotent (Replace), so a worker that
        /// crashed between archive-insert and source-delete will re-enumerate the same
        /// row on re-pickup, re-write the (identical) archive entry, and delete the
        /// source row that survived the crash. Counters are recomputed from the archive
        /// table at the end so a transient crash mid-loop does not produce a stale 0.
        /// </para>
        /// </summary>
        private async Task ArchiveAndWipeCustomsRulesAsync(
            OffboardingHistoryEntry history, string tenantId, CancellationToken ct)
        {
            foreach (var (tableName, fieldName) in ArchivedRuleTables)
            {
                await ArchiveAndWipeRulesTableAsync(tableName, tenantId, history.RowKey, ct);

                // Recompute the counter from the archive table (per run + table) rather than
                // from in-loop accumulation. A crash between this assignment and the final
                // UpsertHistoryAsync below would otherwise leave a stale 0.
                var counted = await _customsArchive.CountByRunAndTableAsync(tenantId, history.RowKey, tableName, ct);
                AssignArchivedCounter(history, fieldName, counted);
            }

            await _auditRepo.UpsertHistoryAsync(history, ct);
        }

        /// <summary>
        /// Archive-then-wipe loop for a single rules table. Up to <see cref="ArchiveIterationCap"/>
        /// iterations; final-iteration verify-read triggers <c>customs_arrival_race</c> when a
        /// writer is still racing us. Marked <c>internal virtual</c> so test harnesses can
        /// override it to a no-op without faking the underlying TableServiceClient.
        /// </summary>
        internal virtual async Task ArchiveAndWipeRulesTableAsync(
            string tableName, string tenantId, string historyRowKey, CancellationToken ct)
        {
            var sourceClient = _storage.GetTableClient(tableName);

            for (var attempt = 1; attempt <= ArchiveIterationCap; attempt++)
            {
                var archivedThisAttempt = 0;
                var filter = OffboardingFilters.ExactPartition(tenantId);
                await foreach (var entity in sourceClient.QueryAsync<TableEntity>(filter, cancellationToken: ct))
                {
                    await _customsArchive.UpsertAsync(BuildArchiveEntry(entity, tenantId, tableName, historyRowKey), ct);
                    // Only AFTER the archive entry is persisted do we delete the source.
                    try
                    {
                        await sourceClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, Azure.ETag.All, ct);
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                    {
                        // Already gone (parallel writer / re-pickup) — archive already
                        // captured the state we observed, so this is idempotent-safe.
                    }
                    archivedThisAttempt++;
                }

                if (archivedThisAttempt == 0)
                {
                    // Source partition empty → converged.
                    return;
                }

                if (attempt == ArchiveIterationCap)
                {
                    // Final iteration still found rows. One last verify-read; if STILL
                    // non-empty, a writer is racing us and we cannot guarantee complete
                    // archive coverage → fail-closed.
                    var leftover = 0;
                    await foreach (var _ in sourceClient.QueryAsync<TableEntity>(
                        filter, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
                    {
                        leftover++;
                        break; // existence is enough
                    }
                    if (leftover > 0)
                    {
                        _logger.LogError(
                            "Customs archive iteration cap exhausted for {Table} tenant={Tenant} run={History}: a writer is still producing rows after {Cap} archive-then-wipe passes — fail-closing",
                            tableName, tenantId, historyRowKey, ArchiveIterationCap);
                        throw new InvalidOperationException(
                            $"customs_arrival_race: table '{tableName}' still has rows after {ArchiveIterationCap} archive-then-wipe iterations");
                    }
                }
            }
        }

        internal TenantOffboardingCustomsArchiveEntry BuildArchiveEntry(
            TableEntity source, string tenantId, string originalTable, string historyRowKey)
        {
            // Serialize every non-system property the source row exposes. TableEntity is
            // IDictionary<string, object> internally, so we can iterate.
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in source)
            {
                // Skip pseudo-properties Azure layers add (ETag/Timestamp/odata.*).
                if (kv.Key is "odata.etag" or "Timestamp") continue;
                snapshot[kv.Key] = kv.Value;
            }
            var entityJson = JsonSerializer.Serialize(snapshot);

            return new TenantOffboardingCustomsArchiveEntry
            {
                PartitionKey = TableTenantCustomsArchiveRepository.BuildPartitionKey(tenantId, historyRowKey),
                RowKey = TableTenantCustomsArchiveRepository.BuildRowKey(originalTable, source.RowKey),
                TenantId = tenantId,
                OriginalTable = originalTable,
                OriginalPartitionKey = source.PartitionKey,
                OriginalRowKey = source.RowKey,
                EntityJson = entityJson,
                HistoryRowKey = historyRowKey,
                ArchivedAt = DateTime.UtcNow,
                ArchivedBy = "TenantOffboardingHandler",
            };
        }

        private static void AssignArchivedCounter(OffboardingHistoryEntry history, string fieldName, int value)
        {
            switch (fieldName)
            {
                case nameof(OffboardingHistoryEntry.CustomGatherRulesArchived):
                    history.CustomGatherRulesArchived = value;
                    break;
                case nameof(OffboardingHistoryEntry.CustomAnalyzeRulesArchived):
                    history.CustomAnalyzeRulesArchived = value;
                    break;
                case nameof(OffboardingHistoryEntry.ImeLogPatternOverridesArchived):
                    history.ImeLogPatternOverridesArchived = value;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown archived counter field '{fieldName}'");
            }
        }

        private async Task SafeWipeAllTenantTablesAsync(
            Dictionary<string, int> counts, string tenantId, CancellationToken ct)
        {
            foreach (var table in TenantPartitionTables)
            {
                ct.ThrowIfCancellationRequested();
                counts[table] = await _safeWipe.WipeByExactPartitionAsync(table, tenantId, ct);
            }
            foreach (var table in CompositePartitionTables)
            {
                ct.ThrowIfCancellationRequested();
                counts[table] = await _safeWipe.WipeByCompositePartitionRangeAsync(table, tenantId, ct);
            }
            foreach (var (table, discriminator) in DiscriminatorTables)
            {
                ct.ThrowIfCancellationRequested();
                var key = $"{table}/{discriminator}";
                counts[key] = await _safeWipe.WipeByDiscriminatorAndTenantPropertyAsync(table, discriminator, tenantId, ct);
            }
            foreach (var table in PropertyOnlyTables)
            {
                ct.ThrowIfCancellationRequested();
                counts[table] = await _safeWipe.WipeByTenantIdPropertyAsync(table, tenantId, ct);
            }
        }

        // ── Failure path ────────────────────────────────────────────────────────
        //
        // Same Review-Fix Finding 3 invariant as the Completed path: write Marker/Pointer/Ops
        // FIRST, then History terminal LAST. A crash between the side-effects and the History
        // commit makes the next pickup re-run the side-effects idempotently — which is exactly
        // what we want for the Failed path, because the marker/pointer/ops outcome is the
        // operator-facing signal, not History.Status by itself.

        private async Task FailAsync(
            OffboardingHistoryEntry history, string tenantId,
            string failedPhase, string errorMessage, CancellationToken ct)
        {
            // Pre-populate in-memory history with the final values so the LAST write below
            // commits the snapshot.
            history.ErrorMessage = errorMessage;

            // Side-effect 1: Marker → Failed.
            var marker = await _auditRepo.TryGetMarkerAsync(tenantId, ct);
            if (marker != null)
            {
                marker.Status = "Failed";
                marker.FailedAt = DateTime.UtcNow;
                marker.FailedPhase = failedPhase;
                await _auditRepo.UpsertMarkerAsync(marker, ct);
            }

            // Side-effect 2: Pointer → Failed.
            await UpdatePointerWithRetryAsync(tenantId, history.RowKey, "Failed", ct);

            // Side-effect 3: OpsEvent (Telegram-routable).
            await _opsEvents.RecordTenantOffboardingFailedAsync(
                tenantId, history.InitiatedBy, failedPhase, errorMessage, history.RetryCount, history.DomainName);

            // COMMIT: History → Failed LAST.
            history.Status = "Failed";
            await _auditRepo.UpsertHistoryAsync(history, ct);

            _logger.LogError(
                "Tenant offboarding FAILED: tenant={Tenant} phase={Phase} message={Message}",
                tenantId, failedPhase, errorMessage);
        }

        // ── Worker-side poison transition (Review-Fix Finding 2) ────────────────

        /// <summary>
        /// Called by <see cref="TenantOffboardingWorker"/> when an envelope hits max-dequeue and
        /// moves to the poison queue. Idempotently transitions History/Pointer/Marker to Failed
        /// so the operator dashboard reflects the dead-letter rather than leaving the tenant
        /// hanging in InProgress. No-op when the row is already Completed/Failed.
        /// </summary>
        public async Task MarkEnvelopeFailedFromPoisonAsync(
            TenantOffboardingEnvelope envelope, int dequeueCount, CancellationToken ct = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            var tenantId = envelope.TenantId.ToLowerInvariant();

            var history = await _auditRepo.TryGetHistoryAsync(envelope.HistoryRowKey, ct);
            if (history == null)
            {
                _logger.LogWarning(
                    "MarkEnvelopeFailedFromPoison: history row missing for tenant={Tenant} history={History} — nothing to transition",
                    tenantId, envelope.HistoryRowKey);
                return;
            }
            if (history.Status is "Completed" or "Failed")
            {
                _logger.LogInformation(
                    "MarkEnvelopeFailedFromPoison: history already terminal ({Status}) for tenant={Tenant} — skipping",
                    history.Status, tenantId);
                return;
            }

            history.RetryCount = Math.Max(history.RetryCount, dequeueCount);
            await FailAsync(history, tenantId,
                failedPhase: "max_dequeue",
                errorMessage: $"Envelope poisoned after {dequeueCount} failed handler attempts",
                ct);
        }
    }
}
