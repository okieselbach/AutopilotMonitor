using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// One-time admin retro-reconciliation of misclassified historical sessions
    /// (misclassification audit 2026-07-16). Two modes, both dry-run capable:
    /// <list type="bullet">
    /// <item><b>legacy_timeouts</b> — Failed rows carrying the pre-classifier blanket
    /// "Session timed out after ..." verdict are re-run through
    /// <see cref="EnrollmentTimeoutClassifier"/>; sessions the classifier would NOT fail
    /// graduate to their honest verdict (Incomplete, or Succeeded on completion evidence).
    /// The audit sample projected ~85–90% of these as misdeclared.</item>
    /// <item><b>pending_orphans</b> — Pending (WhiteGlove Part 1) rows whose device provably
    /// registered a LATER session (Part 2 under a fresh session id, or re-provisioning) are
    /// resolved as Incomplete("Superseded by ..."). Forward-looking coverage lives in the
    /// registration supersede pass; this mode clears the existing backlog.</item>
    /// <item><b>self_deploying_silent</b> — self-deploying-profile rows parked as
    /// Incomplete / AwaitingUser / Stalled by the pre-2026-08-23 classifier (which had no
    /// notion of a profile without a user phase) are re-run through the classifier; only a
    /// Succeeded verdict (<see cref="EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned"/>)
    /// is applied, everything else keeps its verdict.</item>
    /// </list>
    /// Hand-marked sessions (AdminMarkedAction) are never touched.
    /// </summary>
    public class LegacyReclassificationService
    {
        private readonly ILogger<LegacyReclassificationService> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly TenantConfigurationService _configService;

        public const string ModeLegacyTimeouts = "legacy_timeouts";
        public const string ModePendingOrphans = "pending_orphans";
        public const string ModeSelfDeployingSilent = "self_deploying_silent";
        private const int MaxSamplesInResponse = 100;

        public LegacyReclassificationService(
            ILogger<LegacyReclassificationService> logger,
            ISessionRepository sessionRepo,
            IMaintenanceRepository maintenanceRepo,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _maintenanceRepo = maintenanceRepo;
            _configService = configService;
        }

        // ReclassificationResult/ReclassificationSample are wire contract and live in
        // AutopilotMonitor.Shared.Models (AdminApiModels.cs).

        public async Task<ReclassificationResult> ReclassifyLegacyTimeoutsAsync(
            string? tenantIdScope, bool dryRun, int maxSessions, string triggeredBy)
        {
            var result = new ReclassificationResult { Mode = ModeLegacyTimeouts, DryRun = dryRun };
            var now = DateTime.UtcNow;

            foreach (var tenantId in await ResolveTenantsAsync(tenantIdScope))
            {
                if (result.SessionsExamined >= maxSessions) { result.CapReached = true; break; }
                result.TenantsExamined++;

                int? tenantGraceHours = null, absoluteMaxHours = null;
                try
                {
                    var config = await _configService.GetConfigurationAsync(tenantId);
                    tenantGraceHours = config?.SessionGraceHours;
                    absoluteMaxHours = config?.AbsoluteMaxSessionHours;
                }
                catch { /* defaults */ }
                var graceHours = EnrollmentTimeoutClassifier.ResolveGraceHours(tenantGraceHours, absoluteMaxHours);

                var candidates = await _maintenanceRepo.GetLegacyTimeoutFailedSessionsAsync(
                    tenantId, maxSessions - result.SessionsExamined + 1);
                if (candidates.Count > maxSessions - result.SessionsExamined)
                {
                    result.CapReached = true;
                    candidates = candidates.Take(maxSessions - result.SessionsExamined).ToList();
                }

                foreach (var session in candidates)
                {
                    result.SessionsExamined++;
                    try
                    {
                        var events = await _sessionRepo.GetSessionEventsAsync(tenantId, session.SessionId, maxResults: 1000);
                        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
                        var effectiveStart = session.ResumedAt ?? session.StartedAt;
                        var (target, reason, rule) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
                            rollup, effectiveStart, now, graceHours, session.LastEventAt,
                            isPreProvisioned: session.IsPreProvisioned, resumedAt: session.ResumedAt,
                            isSelfDeployingProfile: session.IsSelfDeployingProfile);

                        if (target == SessionStatus.Failed)
                        {
                            result.KeptFailed++;
                            continue;
                        }
                        // These sessions are weeks old — the grace window has long expired, so the
                        // classifier cannot return AwaitingUser; guard anyway so a surprise
                        // non-terminal verdict is skipped rather than applied.
                        if (target != SessionStatus.Succeeded && target != SessionStatus.Incomplete)
                        {
                            result.Skipped++;
                            continue;
                        }

                        if (target != SessionStatus.Succeeded)
                            reason += " Retro-reclassified from the legacy blanket timeout verdict.";

                        RecordSample(result, tenantId, session, target, reason, oldStatus: "Failed");

                        if (dryRun)
                        {
                            result.WouldChange++;
                            continue;
                        }

                        var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                            tenantId, session.SessionId, target, VerdictPaths.Compose(VerdictPaths.OriginRetro, rule),
                            failureReason: reason,
                            failureSource: target == SessionStatus.Incomplete ? "legacy_timeout_retro_reconcile" : null,
                            allowTerminalReclassification: true);
                        if (transitioned)
                        {
                            result.Changed++;
                            if (target == SessionStatus.Succeeded) result.ToSucceeded++; else result.ToIncomplete++;
                        }
                        else
                        {
                            result.Skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        _logger.LogWarning(ex, "Legacy timeout reclassification failed for session {SessionId} (tenant {TenantId})",
                            session.SessionId, tenantId);
                    }
                }
            }

            await WriteAuditAsync(result, tenantIdScope, triggeredBy);
            return result;
        }

        public async Task<ReclassificationResult> ReconcileSelfDeployingSilentAsync(
            string? tenantIdScope, bool dryRun, int maxSessions, string triggeredBy)
        {
            var result = new ReclassificationResult { Mode = ModeSelfDeployingSilent, DryRun = dryRun };
            var now = DateTime.UtcNow;

            foreach (var tenantId in await ResolveTenantsAsync(tenantIdScope))
            {
                if (result.SessionsExamined >= maxSessions) { result.CapReached = true; break; }
                result.TenantsExamined++;

                int? tenantGraceHours = null, absoluteMaxHours = null;
                try
                {
                    var config = await _configService.GetConfigurationAsync(tenantId);
                    tenantGraceHours = config?.SessionGraceHours;
                    absoluteMaxHours = config?.AbsoluteMaxSessionHours;
                }
                catch { /* defaults */ }
                var graceHours = EnrollmentTimeoutClassifier.ResolveGraceHours(tenantGraceHours, absoluteMaxHours);

                var candidates = await _maintenanceRepo.GetSelfDeployingSilentSessionsAsync(
                    tenantId, maxSessions - result.SessionsExamined + 1);
                if (candidates.Count > maxSessions - result.SessionsExamined)
                {
                    result.CapReached = true;
                    candidates = candidates.Take(maxSessions - result.SessionsExamined).ToList();
                }

                foreach (var session in candidates)
                {
                    result.SessionsExamined++;
                    try
                    {
                        if (!string.IsNullOrEmpty(session.AdminMarkedAction))
                        {
                            result.Skipped++;
                            continue;
                        }

                        var events = await _sessionRepo.GetSessionEventsAsync(tenantId, session.SessionId, maxResults: 1000);
                        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
                        var effectiveStart = session.ResumedAt ?? session.StartedAt;
                        var (target, reason, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
                            rollup, effectiveStart, now, graceHours, session.LastEventAt,
                            isPreProvisioned: session.IsPreProvisioned, resumedAt: session.ResumedAt,
                            isSelfDeployingProfile: session.IsSelfDeployingProfile);

                        // Heal-only: a verdict other than Succeeded means the device never reached
                        // Device ESP all-succeeded (or failed explicitly) — the existing label stands.
                        if (target != SessionStatus.Succeeded)
                        {
                            result.Skipped++;
                            continue;
                        }

                        RecordSample(result, tenantId, session, target, reason, oldStatus: session.Status.ToString());

                        if (dryRun)
                        {
                            result.WouldChange++;
                            continue;
                        }

                        // Succeeded may upgrade Incomplete / AwaitingUser / Stalled through the normal
                        // reconcile gate (no terminal-reclassification override needed); the storage
                        // layer additionally refuses admin-marked rows.
                        var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                            tenantId, session.SessionId, SessionStatus.Succeeded, VerdictPaths.RetroSelfDeployingReconcile,
                            failureReason: reason);
                        if (transitioned)
                        {
                            result.Changed++;
                            result.ToSucceeded++;
                        }
                        else
                        {
                            result.Skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        _logger.LogWarning(ex, "Self-deploying retro-reconcile failed for session {SessionId} (tenant {TenantId})",
                            session.SessionId, tenantId);
                    }
                }
            }

            await WriteAuditAsync(result, tenantIdScope, triggeredBy);
            return result;
        }

        public async Task<ReclassificationResult> ResolvePendingOrphansAsync(
            string? tenantIdScope, bool dryRun, int maxSessions, string triggeredBy)
        {
            var result = new ReclassificationResult { Mode = ModePendingOrphans, DryRun = dryRun };

            foreach (var tenantId in await ResolveTenantsAsync(tenantIdScope))
            {
                if (result.SessionsExamined >= maxSessions) { result.CapReached = true; break; }
                result.TenantsExamined++;

                var lean = await _maintenanceRepo.GetSessionsLeanAsync(tenantId);
                var bySerial = lean
                    .Where(s => SerialNumberHeuristics.IsUsableSerialNumber(s.SerialNumber))
                    .GroupBy(s => s.SerialNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var pending in lean.Where(s => s.Status == SessionStatus.Pending))
                {
                    if (result.SessionsExamined >= maxSessions) { result.CapReached = true; break; }
                    if (!SerialNumberHeuristics.IsUsableSerialNumber(pending.SerialNumber))
                        continue;

                    result.SessionsExamined++;
                    try
                    {
                        var successor = bySerial[pending.SerialNumber!.Trim()]
                            .Where(s => !string.Equals(s.SessionId, pending.SessionId, StringComparison.OrdinalIgnoreCase)
                                        && s.StartedAt > pending.StartedAt)
                            .OrderByDescending(s => s.StartedAt)
                            .FirstOrDefault();
                        if (successor == null)
                        {
                            result.Skipped++;
                            continue;
                        }

                        var reason = $"Superseded by session {successor.SessionId}: the device registered a new " +
                                     $"enrollment session at {successor.StartedAt:yyyy-MM-dd HH:mm} UTC before this one " +
                                     "reached a terminal state.";
                        RecordSample(result, tenantId, pending, SessionStatus.Incomplete, reason, oldStatus: "Pending");

                        if (dryRun)
                        {
                            result.WouldChange++;
                            continue;
                        }

                        // Pending is non-terminal — the standard transition guard covers this write.
                        var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                            tenantId, pending.SessionId, SessionStatus.Incomplete, VerdictPaths.RetroSuperseded,
                            failureReason: reason, failureSource: "superseded_by_reregistration");
                        if (transitioned) { result.Changed++; result.ToIncomplete++; }
                        else result.Skipped++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        _logger.LogWarning(ex, "Pending-orphan resolution failed for session {SessionId} (tenant {TenantId})",
                            pending.SessionId, tenantId);
                    }
                }
            }

            await WriteAuditAsync(result, tenantIdScope, triggeredBy);
            return result;
        }

        private async Task<List<string>> ResolveTenantsAsync(string? tenantIdScope)
        {
            if (!string.IsNullOrEmpty(tenantIdScope))
                return new List<string> { tenantIdScope };
            return await _maintenanceRepo.GetAllTenantIdsAsync();
        }

        private static void RecordSample(
            ReclassificationResult result, string tenantId, SessionSummary session,
            SessionStatus target, string reason, string oldStatus)
        {
            if (result.Samples.Count >= MaxSamplesInResponse) return;
            result.Samples.Add(new ReclassificationSample
            {
                TenantId = tenantId,
                SessionId = session.SessionId,
                OldStatus = oldStatus,
                NewStatus = target.ToString(),
                Reason = reason,
            });
        }

        private async Task WriteAuditAsync(ReclassificationResult result, string? tenantIdScope, string triggeredBy)
        {
            // Dry-runs leave no audit trail — nothing changed. Real runs record the counts under
            // the scoped tenant (or the platform marker tenant scope "00000000-...": audit rows
            // require a tenant partition, so an all-tenant run logs one entry per touched tenant
            // would be noisy — a single summary entry under the scope keeps it queryable).
            if (result.DryRun || result.Changed == 0) return;
            try
            {
                await _maintenanceRepo.LogAuditEntryAsync(
                    tenantIdScope ?? Guid.Empty.ToString(),
                    "LegacyReclassification",
                    "Session",
                    $"{result.Changed} sessions",
                    triggeredBy,
                    new Dictionary<string, string>
                    {
                        { "Mode", result.Mode },
                        { "SessionsExamined", result.SessionsExamined.ToString() },
                        { "Changed", result.Changed.ToString() },
                        { "ToSucceeded", result.ToSucceeded.ToString() },
                        { "ToIncomplete", result.ToIncomplete.ToString() },
                        { "KeptFailed", result.KeptFailed.ToString() },
                        { "Errors", result.Errors.ToString() },
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write audit entry for legacy reclassification run");
            }
        }
    }
}
