using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Maintenance
{
    /// <summary>
    /// Marker housekeeping + defense-in-depth cleanup for the tenant-offboarding cascade.
    /// Plan §7.5 (PR3.B-revised).
    /// <para>
    /// Tenant-offboarding markers (<see cref="OffboardingMarkerEntry"/> under
    /// <c>OffboardingAudit/OffboardingMarker</c>) live past the Completed transition by
    /// design — they are the anchor that lets this function find lingering side-effects
    /// (Expectations blob, possibly-stale <c>TenantConfiguration</c> tombstone) and clean
    /// them up if the handler's Phase 2.F-final/2.G ordering hit a crash. Failed markers
    /// are never auto-removed; they require operator action.
    /// </para>
    /// <para>
    /// Note: in the PR3-revised architecture the marker is NO LONGER read in the
    /// agent/web auth hotpath. The active gate is <c>TenantConfiguration.Disabled=true</c>;
    /// the cache-drain barrier (6 min visibility delay on the worker envelope) plus
    /// <see cref="MinCompletedAgeForCleanup"/> together absorb the
    /// <c>TenantConfigurationService</c> cache TTL (5 min). The marker's lifetime here is
    /// purely about cleanup ordering and crash recovery, not auth.
    /// </para>
    /// <para>
    /// Cleanup order per Completed marker, each step's failure keeps the marker for retry:
    /// (1) Expectations blob delete, (2) probe TenantConfiguration → conditional sweep if
    /// still the offboarding tombstone (skipped when the user has self-service-re-onboarded
    /// and the row carries a non-offboarding state), (3) marker delete.
    /// </para>
    /// </summary>
    public class OffboardingMarkerCleanupFunction
    {
        /// <summary>
        /// 3× <c>TenantConfigurationService.CacheDuration</c> (5min) — safety margin that
        /// absorbs clock-skew + GC pauses + cache-read-after-save anomalies on warm
        /// function instances. Originally sized to guarantee the marker outlives every
        /// cached TenantConfiguration on every host; retained at 15min because the
        /// defense-in-depth sweeps (Expectations blob + TenantConfiguration tombstone)
        /// still benefit from waiting for the dust to settle.
        /// </summary>
        public static readonly TimeSpan MinCompletedAgeForCleanup = TimeSpan.FromMinutes(15);

        /// <summary>Failed markers older than this trigger a warn-log every run (operator nudge).</summary>
        internal static readonly TimeSpan FailedMarkerWarnAge = TimeSpan.FromHours(1);

        // Every 2h — matches the broader Maintenance cadence and gives at most a 2h lag between
        // 15min-eligible and actual cleanup. Plan §7.5: "Function läuft im selben Maintenance-
        // Schedule wie SessionDeletionMaintenance".
        private const string Cron = "0 0 */2 * * *";

        private readonly IOffboardingAuditRepository _auditRepo;
        private readonly IOffboardingExpectationsStore _expectations;
        private readonly SafeWipeService _safeWipe;
        private readonly IConfigRepository _configRepo;
        private readonly ILogger<OffboardingMarkerCleanupFunction> _logger;
        private readonly TimeProvider _timeProvider;

        public OffboardingMarkerCleanupFunction(
            IOffboardingAuditRepository auditRepo,
            IOffboardingExpectationsStore expectations,
            SafeWipeService safeWipe,
            IConfigRepository configRepo,
            ILogger<OffboardingMarkerCleanupFunction> logger)
            : this(auditRepo, expectations, safeWipe, configRepo, logger, TimeProvider.System)
        {
        }

        /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> so age comparisons are deterministic.</summary>
        internal OffboardingMarkerCleanupFunction(
            IOffboardingAuditRepository auditRepo,
            IOffboardingExpectationsStore expectations,
            SafeWipeService safeWipe,
            IConfigRepository configRepo,
            ILogger<OffboardingMarkerCleanupFunction> logger,
            TimeProvider timeProvider)
        {
            _auditRepo = auditRepo;
            _expectations = expectations;
            _safeWipe = safeWipe;
            _configRepo = configRepo;
            _logger = logger;
            _timeProvider = timeProvider;
        }

        [Function("OffboardingMarkerCleanup")]
        public async Task Run([TimerTrigger(Cron)] object timer, CancellationToken cancellationToken)
        {
            await RunCoreAsync(cancellationToken);
        }

        /// <summary>Testable core — bypasses TimerInfo / FunctionContext.</summary>
        internal async Task<CleanupResult> RunCoreAsync(CancellationToken ct)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var result = new CleanupResult();

            await foreach (var marker in _auditRepo.QueryMarkersAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                result.Scanned++;

                if (marker.Status == "Completed"
                    && marker.CompletedAt is { } completedAt
                    && completedAt < now - MinCompletedAgeForCleanup)
                {
                    // Cleanup order — each step's failure must keep the marker for the next run:
                    //   1. Expectations blob (Rev-9 hygiene)
                    //   2. TenantConfiguration sweep (PR3.B-revised Finding 2 defense-in-depth)
                    //   3. Marker delete (last anchor)
                    try
                    {
                        await _expectations.DeleteAsync(marker.TenantId, marker.OffboardingHistoryRowKey, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "OffboardingMarkerCleanup: Expectations blob delete failed for tenant={Tenant} history={History} — keeping marker for next-run retry",
                            marker.TenantId, marker.OffboardingHistoryRowKey);
                        result.BlobDeleteRetries++;
                        continue;
                    }

                    // PR3.B Defense-in-depth: if Phase 2.F-final failed in the handler
                    // (crash between 2.G commit and 2.F-final, or transient storage), the
                    // TenantConfiguration row would still carry Disabled=true and block the
                    // tenant from self-service re-onboarding indefinitely. Sweep it here.
                    //
                    // CRITICAL: must be CONDITIONAL on the row still being the offboarding
                    // tombstone. The handler intentionally deletes the row at the end of
                    // Phase 2.F-final precisely so the next /api/auth/me triggers a fresh
                    // Default-Disabled=false config (self-service re-onboarding). If the
                    // user has re-onboarded between Phase 2.F-final and now, blind wiping
                    // would destroy their fresh tenant. So: read first, only wipe when
                    // (Disabled=true AND DisabledReason matches OffboardingDisabledReason).
                    try
                    {
                        var existingConfig = await _configRepo.GetTenantConfigurationAsync(marker.TenantId);
                        if (existingConfig == null)
                        {
                            // Normal happy path: Phase 2.F-final succeeded, row already gone.
                        }
                        else if (existingConfig.Disabled
                            && string.Equals(existingConfig.DisabledReason, TenantOffboardFunction.OffboardingDisabledReason, StringComparison.Ordinal))
                        {
                            // Still the offboarding tombstone → Phase 2.F-final must have failed.
                            var swept = await _safeWipe.WipeByExactPartitionAsync(
                                Constants.TableNames.TenantConfiguration, marker.TenantId, ct);
                            if (swept > 0)
                            {
                                result.TenantConfigsSwept++;
                                _logger.LogWarning(
                                    "OffboardingMarkerCleanup: swept lingering TenantConfiguration tombstone for tenant={Tenant} (Phase 2.F-final must have failed in the handler)",
                                    marker.TenantId);
                            }
                        }
                        else
                        {
                            // User has self-service-re-onboarded between Phase 2.F-final and
                            // now. Leave their fresh config alone.
                            result.TenantConfigsSpared++;
                            _logger.LogInformation(
                                "OffboardingMarkerCleanup: tenant={Tenant} has a non-tombstone TenantConfiguration row (Disabled={Disabled}, Reason={Reason}) — user re-onboarded, skip sweep",
                                marker.TenantId, existingConfig.Disabled, existingConfig.DisabledReason ?? "<null>");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "OffboardingMarkerCleanup: TenantConfiguration probe/sweep failed for tenant={Tenant} — keeping marker for next-run retry",
                            marker.TenantId);
                        result.TenantConfigSweepRetries++;
                        continue;
                    }

                    await _auditRepo.DeleteMarkerAsync(marker.RowKey, ct);
                    result.MarkersDeleted++;
                    _logger.LogInformation(
                        "OffboardingMarkerCleanup: deleted Completed marker for tenant={Tenant} (age={Age})",
                        marker.TenantId, now - completedAt);
                }
                else if (marker.Status == "Failed")
                {
                    result.FailedMarkersSeen++;
                    var failedAt = marker.FailedAt ?? marker.InitiatedAt;
                    var age = now - failedAt;
                    if (age > FailedMarkerWarnAge)
                    {
                        _logger.LogWarning(
                            "OffboardingMarkerCleanup: stale Failed marker tenant={Tenant} age={Age} phase={Phase} — operator action required",
                            marker.TenantId, age, marker.FailedPhase ?? "unknown");
                    }
                }
                // Initiated / InProgress → still working, ignore.
            }

            _logger.LogInformation(
                "OffboardingMarkerCleanup completed: scanned={Scanned} deleted={Deleted} blobRetries={BlobRetries} tenantConfigSwept={ConfigSwept} tenantConfigSpared={ConfigSpared} tenantConfigRetries={ConfigRetries} failed={Failed}",
                result.Scanned, result.MarkersDeleted, result.BlobDeleteRetries,
                result.TenantConfigsSwept, result.TenantConfigsSpared, result.TenantConfigSweepRetries, result.FailedMarkersSeen);

            return result;
        }

        /// <summary>Summary counters returned from <see cref="RunCoreAsync"/> so tests can assert outcomes.</summary>
        internal sealed class CleanupResult
        {
            public int Scanned { get; set; }
            public int MarkersDeleted { get; set; }
            public int BlobDeleteRetries { get; set; }
            public int FailedMarkersSeen { get; set; }
            /// <summary>Number of stale TenantConfiguration tombstones the defense-in-depth sweep removed.</summary>
            public int TenantConfigsSwept { get; set; }
            /// <summary>Number of TenantConfiguration rows the sweep deliberately left alone because the
            /// tenant had re-onboarded (DisabledReason is no longer the offboarding tombstone string).</summary>
            public int TenantConfigsSpared { get; set; }
            /// <summary>Number of markers kept alive because the TenantConfiguration probe/sweep failed transiently.</summary>
            public int TenantConfigSweepRetries { get; set; }
        }
    }
}
