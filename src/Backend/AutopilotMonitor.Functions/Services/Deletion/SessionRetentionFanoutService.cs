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
        private readonly Func<DateTime> _utcNow;

        public SessionRetentionFanoutService(
            IMaintenanceRepository maintenanceRepo,
            TenantConfigurationService tenantConfig,
            ISessionDeletionEnqueuer enqueuer,
            AdminConfigurationService adminConfig,
            ILogger<SessionRetentionFanoutService> logger)
            : this(maintenanceRepo, tenantConfig, enqueuer, adminConfig, logger, throttle: Task.Delay, utcNow: () => DateTime.UtcNow)
        {
        }

        /// <summary>
        /// Test seam — tests inject a no-op throttle to make the rate-limit loop exercise its
        /// counter without waiting 50ms × N in real time, and a scripted clock so the run-budget
        /// deadline can be crossed deterministically.
        /// </summary>
        internal SessionRetentionFanoutService(
            IMaintenanceRepository maintenanceRepo,
            TenantConfigurationService tenantConfig,
            ISessionDeletionEnqueuer enqueuer,
            AdminConfigurationService adminConfig,
            ILogger<SessionRetentionFanoutService> logger,
            Func<TimeSpan, CancellationToken, Task> throttle,
            Func<DateTime>? utcNow = null)
        {
            _maintenanceRepo = maintenanceRepo;
            _tenantConfig = tenantConfig;
            _enqueuer = enqueuer;
            _adminConfig = adminConfig;
            _logger = logger;
            _throttle = throttle;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Aggregate result of a single fanout invocation; used for the per-tenant audit + the
        /// completion summary OpsEvent. The caller allocates it and <see cref="RunAsync"/> mutates
        /// it incrementally (per tenant / per session), so watchdog snapshots taken while the
        /// fanout is still running read real progress instead of zeros.
        /// </summary>
        public sealed class FanoutResult
        {
            public int TenantsProcessed { get; set; }
            public int SessionsEnqueued { get; set; }     // enqueued for cascade
            public int SessionsSkipped { get; set; }       // already locked / poisoned / kill-switch / etc.
            public int RateLimitedTenants { get; set; }    // tenants that hit MaxEnqueuesPerTenantPerRun
            public bool AbortedByKillSwitch { get; set; }  // kill-switch flipped mid-run
            public bool AbortedByBudget { get; set; }      // run-budget deadline crossed mid-run
        }

        /// <summary>
        /// Runs the fanout for every tenant returned by <see cref="IMaintenanceRepository.GetAllTenantIdsAsync"/>.
        /// Each tenant is processed independently; an exception on tenant A is logged and the loop
        /// continues with tenant B (matches the existing maintenance behaviour: per-tenant
        /// failures must not cascade).
        /// <para>
        /// <paramref name="deadlineUtc"/> is the run-budget cutoff: once crossed, the fanout stops
        /// cleanly at the next tenant/session boundary and sets <see cref="FanoutResult.AbortedByBudget"/>
        /// — the remaining backlog is picked up by the next run. This keeps the maintenance
        /// function comfortably inside the Flex Consumption 60min <c>functionTimeout</c> instead of
        /// being hard-aborted by the host mid-tenant.
        /// </para>
        /// </summary>
        public virtual async Task RunAsync(FanoutResult result, DateTime deadlineUtc, CancellationToken cancellationToken)
        {
            var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync().ConfigureAwait(false);

            foreach (var tenantId in tenantIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Run-budget check at the tenant boundary — cheapest clean stopping point.
                if (_utcNow() >= deadlineUtc)
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: run budget exhausted — halting before tenant {TenantId} (deadline {Deadline:o})",
                        tenantId, deadlineUtc);
                    result.AbortedByBudget = true;
                    break;
                }

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
                    await RunForTenantAsync(tenantId, deadlineUtc, result, cancellationToken).ConfigureAwait(false);
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

                // The inner loop sets these when the producer's Step 0 reports KillSwitchActive
                // mid-tenant, or when the budget deadline is crossed mid-tenant. Both are
                // authoritative stop signals — don't touch the remaining tenants.
                if (result.AbortedByKillSwitch || result.AbortedByBudget)
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: aborting outer loop after tenant {TenantId} (killSwitch={KillSwitch}, budget={Budget})",
                        tenantId, result.AbortedByKillSwitch, result.AbortedByBudget);
                    break;
                }
            }
        }

        private async Task RunForTenantAsync(string tenantId, DateTime deadlineUtc, FanoutResult result, CancellationToken cancellationToken)
        {
            var config = await _tenantConfig.GetConfigurationAsync(tenantId).ConfigureAwait(false);
            // Edition retention cap (fail-closed backstop): the stored value is clamped to the
            // effective edition's cap at read time (Community 90 / Enterprise 365). A tenant whose
            // trial expired, or whose stored value predates the cap, is enforced here even though
            // the stored DataRetentionDays is left untouched. days <= 0 stays the GA-only
            // "infinite" escape hatch (skipped below, never clamped).
            var storedDays = config?.DataRetentionDays ?? 90;
            var retentionDays = config == null
                ? 90
                : TenantEntitlementService.GetEffectiveRetentionDays(config, DateTime.UtcNow);
            if (retentionDays > 0 && retentionDays < storedDays)
            {
                _logger.LogWarning(
                    "Tenant {TenantId}: DataRetentionDays={Stored} exceeds the {Edition}-edition cap — enforcing {Effective} days",
                    tenantId, storedDays,
                    TenantEntitlementService.ResolveEdition(config!, DateTime.UtcNow), retentionDays);
            }

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

                // Run-budget check per session — a single tenant's batch can take tens of
                // minutes (manifest build ~7-25s per session), so the tenant-boundary check
                // alone would not stop a run drifting into the host's hard functionTimeout.
                if (_utcNow() >= deadlineUtc)
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: run budget exhausted mid-tenant — halting at session {SessionId} of {TenantId}",
                        session.SessionId, tenantId);
                    result.AbortedByBudget = true;
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
                    result.SessionsSkipped++;
                    result.AbortedByKillSwitch = true;
                    break;
                }

                // Live counters on the shared result (in addition to the per-tenant locals used
                // for the audit entry) so the maintenance watchdog reports real progress.
                if (outcome == SessionDeletionEnqueueOutcome.Enqueued) { enqueued++; result.SessionsEnqueued++; }
                else { skipped++; result.SessionsSkipped++; }

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

            // result.SessionsEnqueued / SessionsSkipped are already bumped live inside the loop.
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
                    { "AbortedByBudget", result.AbortedByBudget.ToString() },
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
