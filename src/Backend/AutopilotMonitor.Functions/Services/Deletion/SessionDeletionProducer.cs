using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Queues;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Outcome of <see cref="SessionDeletionProducer.EnqueueAsync"/>. Producers translate
    /// these into HTTP statuses (PR5: admin-delete) or audit + skip (PR6: maintenance fanout).
    /// </summary>
    public enum SessionDeletionEnqueueOutcome
    {
        /// <summary>Cascade was enqueued successfully; <see cref="SessionDeletionEnqueueResult.ManifestId"/> is set.</summary>
        Enqueued,
        /// <summary>Global kill-switch is active — caller returns 503 / skips fanout.</summary>
        KillSwitchActive,
        /// <summary>Sessions row not found — caller returns 404.</summary>
        SessionNotFound,
        /// <summary>Another cascade is already in flight for this session (state ∈ Preparing/Queued/Running).
        ///     Treated as success-resume: the existing cascade will run; the caller may surface its
        ///     ManifestId as 200 OK (idempotent) or 409 Conflict (admin-delete UX). PR5/PR6 decide.</summary>
        AlreadyInFlight,
        /// <summary>Sessions row is Poisoned — operator must restore via <c>POST /restore</c> first.
        ///     Caller returns 409 with a "use restore endpoint" hint.</summary>
        Poisoned,
        /// <summary>Persistent ETag-CAS contention — bounded retries exhausted. Caller returns 503.</summary>
        CasExhausted,
    }

    public class SessionDeletionEnqueueResult
    {
        public SessionDeletionEnqueueOutcome Outcome { get; set; }
        public string? ManifestId { get; set; }
        public string? ExistingState { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Cascade-delete producer (Plan §5 PR3). Sequence per <c>EnqueueAsync</c>:
    /// <list type="number">
    ///   <item>Check <see cref="AdminConfiguration.SessionDeletionKillSwitch"/> → 503 if true.</item>
    ///   <item>CAS Sessions.DeletionState: <c>None → Preparing</c>, stamp new ManifestId.</item>
    ///   <item>Build manifest (<see cref="DeletionManifestBuilder"/>) — writers are now blocked.</item>
    ///   <item>Upload immutable snapshot blob (PR1 helper).</item>
    ///   <item>Upload mutable progress blob (PR3 helper).</item>
    ///   <item>Audit <c>deletion_started</c>.</item>
    ///   <item>CAS Sessions.DeletionState: <c>Preparing → Queued</c>.</item>
    ///   <item>Send <see cref="SessionDeletionEnvelope"/> to the <c>session-deletion</c> queue.</item>
    /// </list>
    /// Crash recovery contract (Plan §5 PR3 step 4): if the producer crashes between steps 2 and 8,
    /// the row is left in <c>Preparing</c> with no in-flight queue message. The cascade-maintenance
    /// function (PR6) clears Preparing-with-no-progress-blob rows older than 1h back to <c>None</c>.
    /// States <c>Queued</c>, <c>Running</c>, <c>Poisoned</c> are NEVER auto-cleared — operator action only.
    /// </summary>
    public sealed class SessionDeletionProducer
    {
        private readonly TableStorageService _storage;
        private readonly DeletionManifestBuilder _builder;
        private readonly BlobStorageService _blob;
        private readonly AdminConfigurationService _adminConfig;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly QueueClient _queueClient;
        private readonly ILogger<SessionDeletionProducer> _logger;

        private int _queueEnsured; // 0 = not yet ensured, 1 = CreateIfNotExistsAsync has run

        public SessionDeletionProducer(
            TableStorageService storage,
            DeletionManifestBuilder builder,
            BlobStorageService blob,
            AdminConfigurationService adminConfig,
            IMaintenanceRepository maintenanceRepo,
            IConfiguration configuration,
            ILogger<SessionDeletionProducer> logger)
        {
            _storage = storage;
            _builder = builder;
            _blob = blob;
            _adminConfig = adminConfig;
            _maintenanceRepo = maintenanceRepo;
            _logger = logger;

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString   = configuration["AzureTableStorageConnectionString"];
            var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var queueUri = new Uri($"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.SessionDeletion}");
                _queueClient = new QueueClient(queueUri, new DefaultAzureCredential(), options);
                _logger.LogInformation("SessionDeletionProducer initialized with Managed Identity (account: {Account})", storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _queueClient = new QueueClient(connectionString, Constants.QueueNames.SessionDeletion, options);
                _logger.LogInformation("SessionDeletionProducer initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        /// <summary>
        /// Test seam: construct directly with a (possibly Moq'd) <see cref="QueueClient"/>.
        /// Used by xUnit so the producer's full state-machine flow can be exercised against
        /// an in-memory queue without spinning Azurite.
        /// </summary>
        internal SessionDeletionProducer(
            TableStorageService storage,
            DeletionManifestBuilder builder,
            BlobStorageService blob,
            AdminConfigurationService adminConfig,
            IMaintenanceRepository maintenanceRepo,
            QueueClient queueClient,
            ILogger<SessionDeletionProducer> logger)
        {
            _storage = storage;
            _builder = builder;
            _blob = blob;
            _adminConfig = adminConfig;
            _maintenanceRepo = maintenanceRepo;
            _queueClient = queueClient;
            _logger = logger;
        }

        /// <summary>
        /// Acquires the cascade lock, builds + uploads the manifest, audits, and enqueues.
        /// Idempotent across crashes: a second producer call against an already-locked session
        /// returns <see cref="SessionDeletionEnqueueOutcome.AlreadyInFlight"/> with the existing
        /// ManifestId; PR5/PR6 callers translate per their UX (admin → 409 Conflict, maintenance
        /// → skip + audit).
        /// </summary>
        public async Task<SessionDeletionEnqueueResult> EnqueueAsync(
            string tenantId, string sessionId, string reason, DeletionActor actor,
            DeletionRetentionContext? retentionContext = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (actor == null) throw new ArgumentNullException(nameof(actor));

            // Step 0: kill-switch (Plan §1 P8 / §9). Independent of per-tenant flag — that's the caller's job (PR5/PR6).
            var globalConfig = await _adminConfig.GetConfigurationAsync().ConfigureAwait(false);
            if (globalConfig.SessionDeletionKillSwitch)
            {
                _logger.LogWarning(
                    "SessionDeletionProducer rejected (kill-switch active). tenant={TenantId} session={SessionId}",
                    tenantId, sessionId);
                return new SessionDeletionEnqueueResult { Outcome = SessionDeletionEnqueueOutcome.KillSwitchActive };
            }

            // Step 1: CAS None → Preparing with new ManifestId.
            var manifestId = NewManifestId();
            var cas1 = await _storage.CasSetSessionDeletionStateAsync(
                tenantId, sessionId,
                fromState: SessionDeletionState.None,
                toState: SessionDeletionState.Preparing,
                newManifestId: manifestId,
                cancellationToken).ConfigureAwait(false);

            switch (cas1.Outcome)
            {
                case TableStorageService.SessionDeletionStateCasOutcome.SessionMissing:
                    return new SessionDeletionEnqueueResult { Outcome = SessionDeletionEnqueueOutcome.SessionNotFound };

                case TableStorageService.SessionDeletionStateCasOutcome.WrongState:
                    if (cas1.CurrentState == SessionDeletionState.Poisoned)
                    {
                        return new SessionDeletionEnqueueResult
                        {
                            Outcome = SessionDeletionEnqueueOutcome.Poisoned,
                            ManifestId = cas1.CurrentManifestId,
                            ExistingState = cas1.CurrentState,
                        };
                    }
                    if (cas1.CurrentState == SessionDeletionState.Queued && !string.IsNullOrEmpty(cas1.CurrentManifestId))
                    {
                        // Resume: snapshot + progress blobs were uploaded by a prior producer
                        // call but the SendMessageAsync step failed (or the queue message was
                        // lost). Re-send the envelope with the EXISTING ManifestId so PR4's
                        // worker picks it up. Idempotent — if the queue already has a message,
                        // duplicates are handled by the worker via the manifest's CAS-bound
                        // progress blob (Plan §5 PR3 step 4 + §16-R12).
                        return await ResumeBySendingQueueMessageAsync(
                            tenantId, sessionId, cas1.CurrentManifestId!, cas1.CurrentState!, reason, cancellationToken).ConfigureAwait(false);
                    }
                    if (cas1.CurrentState == SessionDeletionState.Preparing && !string.IsNullOrEmpty(cas1.CurrentManifestId))
                    {
                        // Codex round-2 follow-up F4: a prior producer crashed AFTER uploading
                        // the snapshot + progress blobs but BEFORE the CAS Preparing→Queued
                        // step. Plan §10 GC only clears Preparing rows without a progress blob,
                        // so this case would otherwise sit forever. Probe the snapshot blob;
                        // if present, attempt the missing CAS Preparing→Queued + queue send
                        // to finish the resume. If absent (the prior crash was earlier in the
                        // build/upload phase), bail out with AlreadyInFlight and let the
                        // maintenance GC clean up.
                        var snapshotExists = false;
                        try
                        {
                            snapshotExists = await _blob.DeletionSnapshotExistsAsync(
                                tenantId, sessionId, cas1.CurrentManifestId!, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "DeletionSnapshotExistsAsync probe failed while attempting Preparing-resume; falling back to AlreadyInFlight. tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                                tenantId, sessionId, cas1.CurrentManifestId);
                        }
                        if (snapshotExists)
                        {
                            var resumeCas = await _storage.CasSetSessionDeletionStateAsync(
                                tenantId, sessionId,
                                fromState: SessionDeletionState.Preparing,
                                toState: SessionDeletionState.Queued,
                                newManifestId: null,
                                cancellationToken).ConfigureAwait(false);
                            if (resumeCas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.Updated)
                            {
                                _logger.LogInformation(
                                    "Preparing-resume: snapshot blob present, CAS Preparing→Queued succeeded. tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                                    tenantId, sessionId, cas1.CurrentManifestId);
                                return await ResumeBySendingQueueMessageAsync(
                                    tenantId, sessionId, cas1.CurrentManifestId!, SessionDeletionState.Preparing, reason, cancellationToken).ConfigureAwait(false);
                            }
                            _logger.LogInformation(
                                "Preparing-resume: CAS Preparing→Queued lost (outcome={Outcome}, currentState={CurrentState}); reporting AlreadyInFlight",
                                resumeCas.Outcome, resumeCas.CurrentState);
                            // Fall through to AlreadyInFlight below.
                        }
                    }
                    if (SessionDeletionState.IsLocked(cas1.CurrentState))
                    {
                        // Preparing without a snapshot blob (or Running) — caller decides UX
                        // (admin → 409 Conflict, maintenance → skip + audit). Preparing without
                        // progress blob is GC'd after 1h by PR6's maintenance function; Running
                        // is the worker actively processing.
                        return new SessionDeletionEnqueueResult
                        {
                            Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight,
                            ManifestId = cas1.CurrentManifestId,
                            ExistingState = cas1.CurrentState,
                        };
                    }
                    // Should not happen — None is the only other valid value.
                    return new SessionDeletionEnqueueResult
                    {
                        Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight,
                        ExistingState = cas1.CurrentState,
                    };

                case TableStorageService.SessionDeletionStateCasOutcome.EtagConflict:
                    // Concurrent writer raced our Update. PR3 keeps the producer simple — return
                    // CasExhausted; caller can retry (admin click) or skip (maintenance).
                    return new SessionDeletionEnqueueResult { Outcome = SessionDeletionEnqueueOutcome.CasExhausted };

                case TableStorageService.SessionDeletionStateCasOutcome.Updated:
                    break; // happy path falls through
                default:
                    throw new InvalidOperationException("Unhandled CAS outcome: " + cas1.Outcome);
            }

            // Step 2: Build manifest. Pass our pre-allocated ManifestId so the builder stamps
            // it on the manifest AND computes SchemaHash against it — overwriting after build
            // would invalidate the hash (Codex finding #3). The guard now blocks any new writers
            // across the wired sites (PR3 wiring), so enumeration is consistent.
            var ctx = retentionContext ?? new DeletionRetentionContext();
            DeletionManifest manifest;
            try
            {
                manifest = await _builder.BuildAsync(
                    tenantId, sessionId, reason, actor, ctx, cancellationToken,
                    preAllocatedManifestId: manifestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeletionManifestBuilder.BuildAsync threw for tenant={TenantId} session={SessionId} manifestId={ManifestId}; row left in Preparing for GC recovery",
                    tenantId, sessionId, manifestId);
                throw;
            }

            // Step 3: Upload the immutable snapshot blob.
            var pointer = await _blob.UploadDeletionManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

            // Step 4: Upload the mutable progress blob (initial state — no completed steps).
            await _blob.UploadInitialDeletionProgressAsync(
                tenantId, sessionId, manifestId, pointer.SnapshotSha256, cancellationToken).ConfigureAwait(false);

            // Step 5: Audit deletion_started. Captures the manifest pointer + reason for the audit trail.
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                action: "deletion_started",
                entityType: "Session",
                entityId: sessionId,
                performedBy: actor.Actor,
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["manifestId"] = manifestId,
                    ["reason"] = reason,
                    ["actorType"] = actor.Type,
                    ["snapshotBlob"] = pointer.BlobName,
                    ["snapshotSha256"] = pointer.SnapshotSha256,
                    ["snapshotSizeBytes"] = pointer.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }).ConfigureAwait(false);

            // Step 6: CAS Preparing → Queued. Manifest + progress blob are uploaded; the cascade
            // is now ready for the worker.
            var cas2 = await _storage.CasSetSessionDeletionStateAsync(
                tenantId, sessionId,
                fromState: SessionDeletionState.Preparing,
                toState: SessionDeletionState.Queued,
                newManifestId: null,
                cancellationToken).ConfigureAwait(false);
            if (cas2.Outcome != TableStorageService.SessionDeletionStateCasOutcome.Updated)
            {
                _logger.LogError(
                    "Failed CAS Preparing→Queued: tenant={TenantId} session={SessionId} manifestId={ManifestId} outcome={Outcome} currentState={CurrentState}",
                    tenantId, sessionId, manifestId, cas2.Outcome, cas2.CurrentState);
                // Snapshot + progress blobs are already uploaded; row is in Preparing. Maintenance
                // GC will clear after 1h if no progress blob exists. Operator may need to act.
                return new SessionDeletionEnqueueResult
                {
                    Outcome = SessionDeletionEnqueueOutcome.CasExhausted,
                    ManifestId = manifestId,
                    ExistingState = cas2.CurrentState,
                };
            }

            // Step 7: Enqueue the envelope.
            await EnsureQueueExistsAsync(cancellationToken).ConfigureAwait(false);
            var envelope = new SessionDeletionEnvelope
            {
                TenantId = tenantId,
                SessionId = sessionId,
                ManifestId = manifestId,
                Reason = reason,
                EnqueuedAt = DateTime.UtcNow,
            };
            var body = JsonConvert.SerializeObject(envelope);
            await _queueClient.SendMessageAsync(body, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "SessionDeletionProducer enqueued: tenant={TenantId} session={SessionId} manifestId={ManifestId} reason={Reason} actor={Actor}",
                tenantId, sessionId, manifestId, reason, actor.Actor);

            return new SessionDeletionEnqueueResult
            {
                Outcome = SessionDeletionEnqueueOutcome.Enqueued,
                ManifestId = manifestId,
            };
        }

        /// <summary>
        /// Sends a SessionDeletionEnvelope with the SUPPLIED ManifestId (the stranded one we're
        /// recovering) and returns <see cref="SessionDeletionEnqueueOutcome.Enqueued"/> with
        /// <c>Reason="resume"</c>. Used by both the Queued-resume and Preparing-resume paths so
        /// the recovery is consistent and the audit log shows a unified ":resume" marker.
        /// </summary>
        private async Task<SessionDeletionEnqueueResult> ResumeBySendingQueueMessageAsync(
            string tenantId, string sessionId, string existingManifestId, string fromState,
            string originalReason, CancellationToken cancellationToken)
        {
            await EnsureQueueExistsAsync(cancellationToken).ConfigureAwait(false);
            var resumeEnvelope = new SessionDeletionEnvelope
            {
                TenantId = tenantId,
                SessionId = sessionId,
                ManifestId = existingManifestId,
                Reason = originalReason + ":resume",
                EnqueuedAt = DateTime.UtcNow,
            };
            await _queueClient.SendMessageAsync(JsonConvert.SerializeObject(resumeEnvelope), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "SessionDeletionProducer re-enqueued (resume) from {FromState}: tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                fromState, tenantId, sessionId, existingManifestId);
            return new SessionDeletionEnqueueResult
            {
                Outcome = SessionDeletionEnqueueOutcome.Enqueued,
                ManifestId = existingManifestId,
                ExistingState = fromState,
                Reason = "resume",
            };
        }

        private async Task EnsureQueueExistsAsync(CancellationToken cancellationToken)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _queueEnsured, 1, 0) == 0)
            {
                try
                {
                    await _queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "session-deletion queue create-if-not-exists failed; continuing — SendMessageAsync will surface a real error if the queue is genuinely missing");
                    System.Threading.Interlocked.Exchange(ref _queueEnsured, 0);
                }
            }
        }

        private static string NewManifestId()
        {
            // ULID-shaped string (matching DeletionManifestBuilder): lex-sortable timestamp prefix
            // + random tail. PR3 doesn't need a strict ULID library; the producer just needs an
            // identifier that's unique per cascade attempt.
            var ticks = DateTime.UtcNow.Ticks;
            var prefix = ticks.ToString("X16");
            var tail = Guid.NewGuid().ToString("N").Substring(0, 16).ToUpperInvariant();
            return prefix + "_" + tail;
        }
    }
}
