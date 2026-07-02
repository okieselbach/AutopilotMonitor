using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Per-tenant retention fanout for cascade-delete. Replaces the session-retention loop
    /// previously embedded in <c>MaintenanceService.CleanupOldDataAsync</c>: each tenant's
    /// <c>DataRetentionDays</c> is read; sessions older than the cutoff are dispatched to
    /// <see cref="ISessionDeletionEnqueuer.EnqueueAsync"/> with <c>reason="retention_cutoff"</c>.
    /// The producer's own CAS-then-build-then-enqueue handles the <c>DeletionState != None</c>
    /// case (returns <c>AlreadyInFlight</c>).
    /// <para>
    /// <b>Rate limit:</b> at most <see cref="MaxEnqueuesPerTenantPerRun"/> dispatches per tenant
    /// per invocation, with <see cref="EnqueueThrottleDelay"/> between successive dispatches.
    /// Bounds the cost of a maintenance fan-out when a tenant has months of backlog behind a
    /// freshly-shortened retention setting.
    /// </para>
    /// </summary>
    public class SessionRetentionFanoutService
    {
        public const int MaxEnqueuesPerTenantPerRun = 100;
        public static readonly TimeSpan EnqueueThrottleDelay = TimeSpan.FromMilliseconds(50);

        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly TenantConfigurationService _tenantConfig;
        private readonly ISessionDeletionEnqueuer _enqueuer;
        private readonly AdminConfigurationService _adminConfig;
        private readonly ILogger<SessionRetentionFanoutService> _logger;
        private readonly Func<TimeSpan, CancellationToken, Task> _throttle;

        public SessionRetentionFanoutService(
            IMaintenanceRepository maintenanceRepo,
            TenantConfigurationService tenantConfig,
            ISessionDeletionEnqueuer enqueuer,
            AdminConfigurationService adminConfig,
            ILogger<SessionRetentionFanoutService> logger)
            : this(maintenanceRepo, tenantConfig, enqueuer, adminConfig, logger, throttle: Task.Delay)
        {
        }

        /// <summary>
        /// Test seam — tests inject a no-op throttle to make the rate-limit loop exercise its
        /// counter without waiting 50ms × N in real time.
        /// </summary>
        internal SessionRetentionFanoutService(
            IMaintenanceRepository maintenanceRepo,
            TenantConfigurationService tenantConfig,
            ISessionDeletionEnqueuer enqueuer,
            AdminConfigurationService adminConfig,
            ILogger<SessionRetentionFanoutService> logger,
            Func<TimeSpan, CancellationToken, Task> throttle)
        {
            _maintenanceRepo = maintenanceRepo;
            _tenantConfig = tenantConfig;
            _enqueuer = enqueuer;
            _adminConfig = adminConfig;
            _logger = logger;
            _throttle = throttle;
        }

        /// <summary>
        /// Aggregate result of a single fanout invocation; used for the per-tenant audit + the
        /// completion summary OpsEvent.
        /// </summary>
        public sealed class FanoutResult
        {
            public int TenantsProcessed { get; set; }
            public int SessionsEnqueued { get; set; }     // enqueued for cascade
            public int SessionsSkipped { get; set; }       // already locked / poisoned / kill-switch / etc.
            public int RateLimitedTenants { get; set; }    // tenants that hit MaxEnqueuesPerTenantPerRun
            public bool AbortedByKillSwitch { get; set; }  // kill-switch flipped mid-run
        }

        /// <summary>
        /// Runs the fanout for every tenant returned by <see cref="IMaintenanceRepository.GetAllTenantIdsAsync"/>.
        /// Each tenant is processed independently; an exception on tenant A is logged and the loop
        /// continues with tenant B (matches the existing maintenance behaviour: per-tenant
        /// failures must not cascade).
        /// </summary>
        public virtual async Task<FanoutResult> RunAsync(CancellationToken cancellationToken)
        {
            var result = new FanoutResult();
            var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync().ConfigureAwait(false);

            foreach (var tenantId in tenantIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Per-tenant kill-switch check so a flip-ON mid-run halts the remaining tenants.
                // The maintenance function gates entry; this gates iteration.
                if (await _adminConfig.IsSessionDeletionKillSwitchActiveAsync().ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: kill-switch flipped on mid-run — halting before tenant {TenantId}",
                        tenantId);
                    result.AbortedByKillSwitch = true;
                    break;
                }

                try
                {
                    await RunForTenantAsync(tenantId, result, cancellationToken).ConfigureAwait(false);
                    result.TenantsProcessed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SessionRetentionFanout failed for tenant {TenantId} — continuing with next tenant", tenantId);
                }

                // The inner loop sets this when the producer's Step 0 reports KillSwitchActive
                // mid-tenant. That's the authoritative fail-closed outcome — stop touching the
                // remaining tenants instead of relying on the next pre-check to catch the flip.
                if (result.AbortedByKillSwitch)
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: aborting outer loop after tenant {TenantId} reported KillSwitchActive from producer",
                        tenantId);
                    break;
                }
            }

            return result;
        }

        private async Task RunForTenantAsync(string tenantId, FanoutResult result, CancellationToken cancellationToken)
        {
            var config = await _tenantConfig.GetConfigurationAsync(tenantId).ConfigureAwait(false);
            var retentionDays = config?.DataRetentionDays ?? 90;

            if (retentionDays <= 0)
            {
                _logger.LogInformation("Tenant {TenantId}: DataRetentionDays=0 → skipping retention fanout", tenantId);
                return;
            }

            var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);

            // Server-bounded read: fetch one more than the per-run dispatch cap. The loop below
            // only ever advances MaxEnqueuesPerTenantPerRun sessions, so loading the whole backlog
            // every run is wasted I/O that grows with the backlog. The "+1" is a cheap probe that
            // lets us tell "exactly cap eligible, done" from "cap+ eligible, more deferred" without
            // scanning the rest — it is observability only and is never dispatched.
            // excludeInFlightDeletions: sessions already locked in a deletion state (Poisoned /
            // stranded Queued are never auto-cleared) must not occupy slots in the capped head —
            // ≥cap of them at the RowKey front would otherwise starve the tail on every run.
            const int fetchLimit = MaxEnqueuesPerTenantPerRun + 1;
            var oldSessions = await _maintenanceRepo.GetSessionsOlderThanAsync(
                tenantId, cutoffUtc, fetchLimit, excludeInFlightDeletions: true).ConfigureAwait(false);
            bool moreRemaining = oldSessions.Count > MaxEnqueuesPerTenantPerRun;

            if (oldSessions.Count == 0)
            {
                _logger.LogInformation("Tenant {TenantId}: no sessions older than {Days} days", tenantId, retentionDays);
                return;
            }

            int processed = 0;
            int enqueued = 0;
            int skipped = 0;

            foreach (var session in oldSessions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (processed >= MaxEnqueuesPerTenantPerRun)
                {
                    // We only fetched cap+1, so we know "more remain" but not the exact backlog
                    // size (that would require the full scan this change removes). Report it as a
                    // floor — the next run drains the following batch.
                    _logger.LogInformation(
                        "Tenant {TenantId}: hit rate limit ({Limit}/run) — at least one more batch deferred to next run",
                        tenantId, MaxEnqueuesPerTenantPerRun);
                    result.RateLimitedTenants++;
                    break;
                }

                // Per-session kill-switch check so an emergency flip-ON halts immediately instead
                // of after the rest of this tenant's backlog. Uncached read — uniform behaviour
                // across scaled-out instances within seconds.
                if (await _adminConfig.IsSessionDeletionKillSwitchActiveAsync().ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: kill-switch flipped on mid-tenant — halting at session {SessionId} of {TenantId}",
                        session.SessionId, tenantId);
                    result.AbortedByKillSwitch = true;
                    break;
                }

                var outcome = await EnqueueAsync(session, cancellationToken).ConfigureAwait(false);

                // The producer's own kill-switch check (Step 0) flipped between our pre-check
                // and the producer call. Abort the inner loop AND mark the fanout aborted so
                // the outer loop also stops — otherwise we'd keep pushing sessions through a
                // producer that's already 503'ing every call.
                if (outcome == SessionDeletionEnqueueOutcome.KillSwitchActive)
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: producer reported KillSwitchActive — aborting at session {SessionId} of {TenantId}",
                        session.SessionId, tenantId);
                    skipped++;
                    result.AbortedByKillSwitch = true;
                    break;
                }

                if (outcome == SessionDeletionEnqueueOutcome.Enqueued) enqueued++;
                else skipped++;

                processed++;

                // Rate-limit pacing — bounds the cost of a fanout when a tenant has months of
                // backlog (e.g. retention freshly shortened). Throttle is injected so unit tests
                // can exercise the loop without waiting real time.
                if (processed < oldSessions.Count && processed < MaxEnqueuesPerTenantPerRun)
                    await _throttle(EnqueueThrottleDelay, cancellationToken).ConfigureAwait(false);
            }

            // In-flight deletions are already excluded from the read, so a full batch with zero
            // enqueues means every dispatchable session failed at the producer (CAS exhausted,
            // races, …) — this run made no retention progress and the next run will fetch the
            // same head. Warning so it reaches App Insights (kill-switch aborts are intentional
            // and excluded).
            if (enqueued == 0 && moreRemaining && !result.AbortedByKillSwitch)
            {
                _logger.LogWarning(
                    "Tenant {TenantId}: retention fanout enqueued 0 of {Processed} dispatchable sessions with more remaining — no progress this run (skipped={Skipped})",
                    tenantId, processed, skipped);
            }

            result.SessionsEnqueued += enqueued;
            result.SessionsSkipped += skipped;

            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                "SessionDeletionMaintenanceFanout",
                "Session",
                $"{processed} sessions",
                "System.Maintenance",
                new Dictionary<string, string>
                {
                    { "RetentionDays", retentionDays.ToString() },
                    { "CutoffUtc", cutoffUtc.ToString("o") },
                    // Read is capped at MaxEnqueuesPerTenantPerRun+1, so this is a floor, not the
                    // exact backlog size. MoreRemaining=true ⇒ at least one further batch is pending.
                    { "EligibleThisRun", Math.Min(oldSessions.Count, MaxEnqueuesPerTenantPerRun).ToString() },
                    { "MoreRemaining", moreRemaining.ToString() },
                    { "Enqueued", enqueued.ToString() },
                    { "Skipped", skipped.ToString() },
                    { "AbortedByKillSwitch", result.AbortedByKillSwitch.ToString() },
                }).ConfigureAwait(false);

            _logger.LogInformation(
                "Tenant {TenantId}: retention fanout — cutoff={Cutoff:o} eligibleThisRun={Eligible} moreRemaining={More} enqueued={Enqueued} skipped={Skipped}",
                tenantId, cutoffUtc, Math.Min(oldSessions.Count, MaxEnqueuesPerTenantPerRun), moreRemaining, enqueued, skipped);
        }

        /// <summary>
        /// Enqueue a cascade for one session. The producer handles CAS-Preparing, manifest build,
        /// blob upload, and queue send; we just translate the outcome into a counter bump (or
        /// skip) and log the reason. Maintenance-side audits (<c>deletion_started</c>) are
        /// written by the producer.
        /// </summary>
        private async Task<SessionDeletionEnqueueOutcome> EnqueueAsync(SessionSummary session, CancellationToken cancellationToken)
        {
            var actor = new DeletionActor { Type = "maintenance", Actor = "System.Maintenance" };
            var result = await _enqueuer.EnqueueAsync(
                session.TenantId,
                session.SessionId,
                "retention_cutoff",
                actor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case SessionDeletionEnqueueOutcome.Enqueued:
                    return SessionDeletionEnqueueOutcome.Enqueued;
                case SessionDeletionEnqueueOutcome.AlreadyInFlight:
                    _logger.LogInformation(
                        "Retention fanout: session {SessionId} already has a cascade in flight (state={State}, manifestId={ManifestId}) — skipping",
                        session.SessionId, result.ExistingState, result.ManifestId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.Poisoned:
                    _logger.LogWarning(
                        "Retention fanout: session {SessionId} is in DeletionState=Poisoned (manifestId={ManifestId}) — operator must run POST /restore first",
                        session.SessionId, result.ManifestId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.KillSwitchActive:
                    // Should not normally reach here because the caller (maintenance function) is
                    // expected to gate the whole fanout on the kill-switch. Belt-and-suspenders.
                    _logger.LogWarning("Retention fanout: kill-switch flipped on mid-fanout — aborting tenant {TenantId}", session.TenantId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.SessionNotFound:
                    _logger.LogInformation("Retention fanout: session {SessionId} no longer exists — skipping", session.SessionId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.CasExhausted:
                    _logger.LogWarning(
                        "Retention fanout: ETag-CAS exhausted for session {SessionId} — will retry next run",
                        session.SessionId);
                    return result.Outcome;
                default:
                    _logger.LogError(
                        "Retention fanout: unexpected enqueue outcome {Outcome} for session {SessionId}",
                        result.Outcome, session.SessionId);
                    return result.Outcome;
            }
        }
    }
}
