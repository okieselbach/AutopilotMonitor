using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Tenant offboarding endpoint (Phase 1 of the tenant-offboarding cascade plan).
/// Replaces the previous synchronous <c>DeleteAllTenantDataAsync</c> teardown with a
/// queued cascade. Phase 1 commits the History/Pointer/Marker rows, flips
/// <c>TenantConfiguration.Disabled=true</c> (the actual auth gate), audits, and enqueues
/// a <see cref="TenantOffboardingEnvelope"/> with a <see cref="DrainBarrier"/> visibility
/// delay. The worker picks the message up after the cache-drain window, by which point
/// every function-host has expired its 5-min <c>TenantConfigurationService</c> cache and
/// the existing Disabled-gate carries the 403. Returns 202.
/// </summary>
public class TenantOffboardFunction
{
    private const int PointerCasMaxAttempts = 5;

    /// <summary>
    /// Cache-Drain-Barrier (plan v2 §2.2). Worker MUST NOT begin Phase 2 before this elapsed.
    /// 5 min = <see cref="TenantConfigurationService.CacheDuration"/> (absolute expiration).
    /// 1 min = clock-skew + GC + Storage-replication safety buffer.
    /// Hardcoded by design: this is a structural invariant tied to CacheDuration, not an
    /// operational knob. If CacheDuration is ever raised, this constant must move with it.
    /// </summary>
    internal static readonly TimeSpan DrainBarrier = TimeSpan.FromMinutes(6);

    private readonly ILogger<TenantOffboardFunction> _logger;
    private readonly IConfigRepository _configRepo;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly IMaintenanceRepository _maintenanceRepo;
    private readonly IOffboardingAuditRepository _offboardingRepo;
    private readonly ITenantOffboardingEnqueuer _offboardingEnqueuer;

    public TenantOffboardFunction(
        ILogger<TenantOffboardFunction> logger,
        IConfigRepository configRepo,
        TenantConfigurationService tenantConfigService,
        IMaintenanceRepository maintenanceRepo,
        IOffboardingAuditRepository offboardingRepo,
        ITenantOffboardingEnqueuer offboardingEnqueuer)
    {
        _logger = logger;
        _configRepo = configRepo;
        _tenantConfigService = tenantConfigService;
        _maintenanceRepo = maintenanceRepo;
        _offboardingRepo = offboardingRepo;
        _offboardingEnqueuer = offboardingEnqueuer;
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/offboard
    /// Phase 1 — synchronously commits the offboarding state (History/Pointer/Marker),
    /// flips <c>TenantConfiguration.Disabled=true</c> (the SOLE active auth-gate in the
    /// PR3-revised architecture — the agent + web hotpaths read <c>config.Disabled</c>
    /// only, never the marker), and enqueues a single tenant-offboarding envelope with
    /// a <see cref="DrainBarrier"/> visibility-delay so warm function-host caches drain
    /// before Phase 2 begins.
    /// Accessible by: Tenant Admins of the same tenant OR Global Admins (enforced by
    /// PolicyEnforcementMiddleware).
    /// </summary>
    [Function("OffboardTenant")]
    [Authorize]
    public async Task<HttpResponseData> OffboardTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "tenants/{tenantId}/offboard")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;
        var targetTenantId = requestCtx.TargetTenantId;

        if (string.IsNullOrEmpty(targetTenantId) || !SecurityValidator.IsValidGuid(targetTenantId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = "tenantId must be a valid GUID" });
            return badRequest;
        }

        var normalizedTenantId = targetTenantId.ToLowerInvariant();

        _logger.LogWarning(
            "TENANT OFFBOARD initiated for tenant {TenantId} by {Upn}",
            normalizedTenantId, upn);

        // 2. Capture DomainName from TenantConfiguration so the worker's Completed/Failed
        //    OpsEvents can render "{domain} ({tenantId})" instead of the bare GUID.
        string? domainName = null;
        try
        {
            var existingConfig = await _configRepo.GetTenantConfigurationAsync(targetTenantId);
            domainName = existingConfig?.DomainName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read tenant configuration for {TenantId} before offboard; proceeding without domain name",
                normalizedTenantId);
        }

        // 3. Idempotency: if an active marker exists, replay the queue-enqueue defensively
        //    when the offboarding is still in-flight. The previous implementation returned
        //    200 unconditionally, which left the tenant stuck whenever the first enqueue
        //    failed between marker-insert and queue-send: the marker would block all future
        //    re-clicks via the idempotency-200 path while the worker queue was empty.
        //    Resume-paths per marker status:
        //      - Initiated/InProgress → defensive re-enqueue (TenantOffboardingHandler is
        //        idempotent via History.DrainCompletedAt Rev-9-F1 skip-gate; worst case the
        //        queue carries two envelopes that both resolve cleanly).
        //      - Completed            → no re-enqueue (worker already finished; marker is
        //        the 15-min Rev-5-F2 grace blocker).
        //      - Failed               → no re-enqueue (R1 fail-closed by design; operator
        //        must inspect History.FailedPhase + clear the marker manually before retry).
        try
        {
            var existingMarker = await _offboardingRepo.TryGetMarkerAsync(normalizedTenantId);
            if (existingMarker != null)
            {
                return await HandleExistingMarkerAsync(
                    req, existingMarker, upn, normalizedTenantId, isRace: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to read offboarding marker for {TenantId} during idempotency check",
                normalizedTenantId);
            return await Build500Async(req, "Failed to read offboarding state");
        }

        var now = DateTime.UtcNow;
        var historyRowKey = $"{now:yyyyMMddHHmmssfff}_{normalizedTenantId}";
        var earliestProcessingAt = now + DrainBarrier;

        // 4. Insert History row FIRST (audit trail must exist before any side-effect).
        var history = new OffboardingHistoryEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.History,
            RowKey = historyRowKey,
            TenantId = normalizedTenantId,
            DomainName = domainName ?? string.Empty,
            InitiatedBy = upn,
            OffboardedAt = now,
            EarliestProcessingAt = earliestProcessingAt,
            Status = "Initiated",
            RetryCount = 0,
        };

        try
        {
            await _offboardingRepo.InsertHistoryAsync(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert OffboardingHistory row for {TenantId}", normalizedTenantId);
            return await Build500Async(req, "Failed to record offboarding history");
        }

        // 5. Upsert ByTenant pointer with ETag-CAS so OffboardCount increments race-safely.
        try
        {
            await UpsertPointerWithCasAsync(normalizedTenantId, historyRowKey, now);
        }
        catch (Exception ex)
        {
            // History row is already committed — operator can find it under
            // PartitionKey="OffboardingHistory" and retry manually. Graceful degradation
            // (plan §4.4): pointer is the re-onboarding index; without it PR4's auto-wipe
            // is no-op but the offboarding itself still proceeds.
            _logger.LogError(ex,
                "Failed to upsert OffboardingByTenant pointer for {TenantId} after history row inserted",
                normalizedTenantId);
            return await Build500Async(req, "Failed to update offboarding pointer");
        }

        // 6. Insert Marker LAST among the three rows. The marker is the idempotency anchor
        //    for admin re-clicks (the next click resolves through TryGetMarkerAsync and
        //    hits the resume path) and is later read by OffboardingMarkerCleanupFunction
        //    to drive post-completion cleanup sweeps. The Marker is NOT an auth-gate any
        //    more — TenantConfiguration.Disabled=true (committed in step 7 below) is the
        //    sole active gate.
        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = normalizedTenantId,
            TenantId = normalizedTenantId,
            OffboardingHistoryRowKey = historyRowKey,
            InitiatedAt = now,
            InitiatedBy = upn,
            Status = "Initiated",
        };

        try
        {
            await _offboardingRepo.InsertMarkerAsync(marker);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Race: another caller inserted a marker between our TryGet at step 3 and now.
            // Resolve to the existing marker and run the same defensive re-enqueue path as
            // the idempotency branch — the winner may still have failed its own enqueue and
            // we cannot tell from here, so re-enqueue is the safe default. The worker is
            // idempotent via the DrainCompletedAt skip-gate (Rev-9-F1).
            _logger.LogInformation(
                "OffboardingMarker insert raced for {TenantId}; another initiator won. Resolving to existing marker.",
                normalizedTenantId);

            var raced = await _offboardingRepo.TryGetMarkerAsync(normalizedTenantId);
            if (raced == null)
            {
                _logger.LogError(
                    "OffboardingMarker 409 race for {TenantId} but follow-up read returned null; cannot resolve race",
                    normalizedTenantId);
                return await Build500Async(req, "Failed to resolve offboarding race");
            }
            return await HandleExistingMarkerAsync(
                req, raced, upn, normalizedTenantId, isRace: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert OffboardingMarker for {TenantId}", normalizedTenantId);
            return await Build500Async(req, "Failed to commit offboarding marker");
        }

        // 7. Flip TenantConfiguration.Disabled=true. This is the SOLE active auth-gate now
        //    that the hotpath does not read the OffboardingMarker any more (PR3-revised).
        //    Fail-loud — without Disabled=true the cache-drain barrier offers no protection
        //    and Phase 2 would wipe data while warm function-hosts still accept agent
        //    traffic. Marker stays Initiated → next admin re-click hits the resume path
        //    which also calls EnsureTenantDisabledAsync before re-enqueuing.
        try
        {
            await EnsureTenantDisabledAsync(targetTenantId, upn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to set TenantConfiguration.Disabled=true for {TenantId}; refusing to enqueue (auth-gate not committed)",
                normalizedTenantId);
            return await Build500Async(req, "Failed to commit Disabled-gate; tenant offboarding NOT enqueued");
        }

        // 8. Audit row under AuditGlobalTenantId so it survives the eventual TenantConfiguration wipe.
        await _maintenanceRepo.LogAuditEntryAsync(
            Constants.AuditGlobalTenantId,
            "DELETE",
            "Tenant",
            normalizedTenantId,
            upn,
            new Dictionary<string, string>
            {
                { "Action", "Offboard" },
                { "Phase", "Initiated" },
                { "HistoryRowKey", historyRowKey },
                { "DomainName", domainName ?? string.Empty },
            });

        // 9. Enqueue with full DrainBarrier as visibility delay. The worker won't see this
        //    message until ~6min from now, by which time every function-host has refreshed
        //    its TenantConfiguration cache and the existing Disabled-gate is the active
        //    block. Fail-loud — marker stays Initiated on failure; the next admin re-click
        //    hits the idempotency branch which defensively re-enqueues with the remaining
        //    barrier so the tenant cannot get stuck.
        try
        {
            await EnqueueEnvelopeAsync(normalizedTenantId, historyRowKey, upn, now, DrainBarrier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to enqueue tenant-offboarding envelope for {TenantId}; marker remains Initiated for re-click resume",
                normalizedTenantId);
            return await Build500Async(req, "Failed to enqueue tenant offboarding");
        }

        _logger.LogWarning(
            "TENANT OFFBOARD queued for {TenantId} by {Upn}; historyRowKey={History}; earliestProcessingAt={ProcessAt}",
            normalizedTenantId, upn, historyRowKey, earliestProcessingAt);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new OffboardResponse
        {
            TenantId = normalizedTenantId,
            Status = "Queued",
            HistoryPartitionKey = Constants.OffboardingPartitionKeys.History,
            HistoryRowKey = historyRowKey,
            EarliestProcessingAt = earliestProcessingAt,
            Message = "Tenant offboarding queued for asynchronous processing"
        });
        return response;
    }

    // Resume-path for re-clicks and 409-race winners. Thin HTTP shell on top of
    // ResumeExistingMarkerAsync — the actual decision (re-enqueue or not, message text,
    // remaining drain-barrier) lives in the pure helper below so it can be unit-tested
    // without faking HttpRequestData. Plan Review Finding 1 (stuck-tenant fix).
    internal async Task<HttpResponseData> HandleExistingMarkerAsync(
        HttpRequestData req,
        OffboardingMarkerEntry existing,
        string upn,
        string normalizedTenantId,
        bool isRace)
    {
        var resume = await ResumeExistingMarkerAsync(existing, upn, normalizedTenantId, isRace);

        if (resume.Kind == ResumeOutcomeKind.ReEnqueueFailed)
        {
            return await Build500Async(req, "Failed to re-enqueue tenant offboarding");
        }

        var response = req.CreateResponse(resume.StatusCode);
        await response.WriteAsJsonAsync(new OffboardResponse
        {
            TenantId = normalizedTenantId,
            Status = existing.Status,
            HistoryPartitionKey = Constants.OffboardingPartitionKeys.History,
            HistoryRowKey = existing.OffboardingHistoryRowKey,
            EarliestProcessingAt = resume.EarliestProcessingAt,
            Message = resume.Message,
        });
        return response;
    }

    /// <summary>
    /// Pure resume decision: classifies the existing marker, optionally re-enqueues, and
    /// returns the (StatusCode, Message, ReEnqueued?) tuple the HTTP shell wraps. Exposed
    /// internal for unit tests — Finding-1 fix requires asserting "Initiated/InProgress
    /// triggers a queue write, Completed/Failed does not".
    /// </summary>
    internal async Task<ResumeOutcome> ResumeExistingMarkerAsync(
        OffboardingMarkerEntry existing,
        string upn,
        string normalizedTenantId,
        bool isRace)
    {
        var status = existing.Status ?? "Initiated";

        if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "TenantOffboard re-click for {TenantId}: marker is Completed; no re-enqueue (race={Race})",
                normalizedTenantId, isRace);
            return new ResumeOutcome(
                ResumeOutcomeKind.IdempotentCompleted,
                HttpStatusCode.OK,
                "Tenant offboarding already completed (marker in 15-min grace window)",
                ReEnqueued: false);
        }

        if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            // Failed is fail-closed by design (R1). Re-enqueuing a Failed offboarding would
            // just retrace the same failure path. Surface the FailedPhase to the operator
            // so they can clear the marker manually before retrying.
            _logger.LogWarning(
                "TenantOffboard re-click for {TenantId}: marker is Failed (phase={Phase}); operator action required, no re-enqueue",
                normalizedTenantId, existing.FailedPhase ?? "unknown");
            return new ResumeOutcome(
                ResumeOutcomeKind.IdempotentFailed,
                HttpStatusCode.OK,
                $"Tenant offboarding previously failed at phase '{existing.FailedPhase ?? "unknown"}' — operator action required before retry",
                ReEnqueued: false);
        }

        // Initiated / InProgress (or any unrecognised in-flight value): defensively
        // re-enqueue the envelope. The handler tolerates double-pickup via the
        // DrainCompletedAt skip-gate, so a duplicate envelope on the queue is safe.
        //
        // The visibility-delay computation below uses
        //   max(history.EarliestProcessingAt, tombstone.LastUpdated + DrainBarrier)
        // as the effective drain-deadline. History is best-effort here: if the read
        // fails or the field is missing, the tombstone anchor still gives a safe lower
        // bound (when the tombstone was just written, that's now + DrainBarrier; when
        // it was written long ago, the barrier is already elapsed).
        OffboardingHistoryEntry? history = null;
        try
        {
            history = await _offboardingRepo.TryGetHistoryAsync(existing.OffboardingHistoryRowKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read History row {HistoryRowKey} for {TenantId} during resume; falling through to tombstone.LastUpdated-based deadline",
                existing.OffboardingHistoryRowKey, normalizedTenantId);
        }
        // Re-affirm the Disabled-gate FIRST so we know whether we just wrote it for the
        // first time (= cache-drain barrier restarts now) or it was already there from a
        // prior attempt (= the History row's deadline is still valid). The prior run may
        // have crashed between marker-insert and Disabled-flip, or an admin may have
        // cleared the flag manually. Either way, we MUST NOT re-enqueue without the
        // auth-gate committed — otherwise the cache-drain barrier guards nothing.
        EnsureDisabledResult ensureResult;
        try
        {
            ensureResult = await EnsureTenantDisabledAsync(normalizedTenantId, upn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Resume: failed to re-affirm TenantConfiguration.Disabled=true for {TenantId}; refusing to re-enqueue",
                normalizedTenantId);
            return new ResumeOutcome(
                ResumeOutcomeKind.ReEnqueueFailed,
                HttpStatusCode.InternalServerError,
                "Failed to re-affirm Disabled-gate; tenant offboarding NOT re-enqueued",
                ReEnqueued: false);
        }

        // Resume-path visibility-delay decision (Codex finding "Resume can bypass cache-drain"):
        // We compute the effective drain-deadline as the MAX of two sources, then derive
        // the queue-message visibility-delay from that:
        //
        //   (a) history.EarliestProcessingAt — set on Phase 1 commit; patched here on
        //       fresh writes when we can.
        //   (b) ensureResult.TombstoneLastUpdated + DrainBarrier — the authoritative
        //       "cache-drain started here" timestamp, written atomically with the
        //       Disabled-flip, ALWAYS present even if the History-patch later fails.
        //
        // Using max() of both makes the History-patch non-load-bearing for safety: even if
        // a fast follow-up re-click reads stale EarliestProcessingAt, the TombstoneLastUpdated
        // anchor gives us a correct lower bound. The History-patch is now best-effort
        // documentation, not a critical write.
        var now = DateTime.UtcNow;
        var tombstoneDeadline = ensureResult.TombstoneLastUpdated + DrainBarrier;
        var historyDeadline = history?.EarliestProcessingAt ?? tombstoneDeadline;
        var effectiveDeadline = tombstoneDeadline > historyDeadline ? tombstoneDeadline : historyDeadline;

        var visibilityDelay = effectiveDeadline > now ? effectiveDeadline - now : TimeSpan.Zero;
        var earliestProcessingAtForResponse = effectiveDeadline;

        // Patch History.EarliestProcessingAt for new tombstone writes so the UI countdown
        // + any later admin tools see the new deadline. Failure here is NOT a safety issue
        // anymore (tombstone.LastUpdated covers us above) — purely cosmetic / observability.
        if (ensureResult.Outcome == EnsureDisabledOutcome.WroteNewTombstone && history != null)
        {
            history.EarliestProcessingAt = effectiveDeadline;
            try
            {
                await _offboardingRepo.UpsertHistoryAsync(history);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Resume: failed to patch History.EarliestProcessingAt for {TenantId} {History} — UI countdown may show stale value, but the queue visibility-delay + tombstone.LastUpdated guarantee the drain barrier on the worker side",
                    normalizedTenantId, existing.OffboardingHistoryRowKey);
            }

            _logger.LogWarning(
                "Resume: Disabled-gate was freshly written; restarted full cache-drain barrier ({Barrier}) for tenant={TenantId}",
                DrainBarrier, normalizedTenantId);
        }

        try
        {
            await EnqueueEnvelopeAsync(
                normalizedTenantId, existing.OffboardingHistoryRowKey, upn, existing.InitiatedAt, visibilityDelay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Defensive re-enqueue failed for {TenantId} (existing marker status={Status}, race={Race})",
                normalizedTenantId, status, isRace);
            return new ResumeOutcome(
                ResumeOutcomeKind.ReEnqueueFailed,
                HttpStatusCode.InternalServerError,
                "Failed to re-enqueue tenant offboarding",
                ReEnqueued: false);
        }

        _logger.LogInformation(
            "TenantOffboard re-click for {TenantId}: defensively re-enqueued with visibilityDelay={Delay} outcome={Outcome} tombstoneLastUpdated={TombstoneLastUpdated} (existing status={Status}, race={Race})",
            normalizedTenantId, visibilityDelay, ensureResult.Outcome, ensureResult.TombstoneLastUpdated, status, isRace);

        return new ResumeOutcome(
            ResumeOutcomeKind.ReEnqueuedInFlight,
            HttpStatusCode.Accepted,
            isRace
                ? "Tenant offboarding already in progress (race-winner resolved, defensively re-enqueued)"
                : $"Tenant offboarding resumed (existing status={status}, defensively re-enqueued)",
            ReEnqueued: true,
            EarliestProcessingAt: earliestProcessingAtForResponse);
    }

    /// <summary>
    /// Magic-string DisabledReason that identifies a TenantConfiguration row as the
    /// offboarding tombstone. Used by <c>EnsureTenantDisabledAsync</c> for the idempotent
    /// no-write check AND by <see cref="OffboardingMarkerCleanupFunction"/> to gate the
    /// defense-in-depth TenantConfiguration sweep so a self-service-re-onboarded fresh
    /// config does not get wiped by mistake.
    /// </summary>
    internal const string OffboardingDisabledReason = "Offboarding in progress";

    /// <summary>
    /// Result of <see cref="EnsureTenantDisabledAsync"/>. Carries both whether a fresh
    /// write happened AND the tombstone's <c>LastUpdated</c> timestamp so the resume-path
    /// can compute a safe drain-deadline without depending on the (optionally-failing)
    /// <c>History.EarliestProcessingAt</c> patch.
    /// <para>
    /// Drain-deadline math the caller applies:
    /// <c>effectiveDeadline = max(history.EarliestProcessingAt, TombstoneLastUpdated + DrainBarrier)</c>.
    /// Because the tombstone's <c>LastUpdated</c> is part of the same atomic Save as the
    /// Disabled-flip, it is always present once the auth-gate is committed — even if
    /// the History-row patch later fails on a fast re-click.
    /// </para>
    /// </summary>
    internal readonly record struct EnsureDisabledResult(
        EnsureDisabledOutcome Outcome,
        DateTime TombstoneLastUpdated);

    internal enum EnsureDisabledOutcome
    {
        /// <summary>Row was already a Disabled=true tombstone with the offboarding reason —
        /// no Save was issued. Existing <c>LastUpdated</c> is returned.</summary>
        AlreadyTombstone,
        /// <summary>We actively wrote the Disabled=true gate (either flipped an existing
        /// row or created a fresh one). <c>LastUpdated</c> is the timestamp of this write.</summary>
        WroteNewTombstone,
    }

    /// <summary>
    /// Commits the SOLE active auth-gate (<c>TenantConfiguration.Disabled=true</c>) and
    /// invalidates the local <see cref="TenantConfigurationService"/> cache. Fail-loud:
    /// every caller (initial enqueue + resume re-enqueue) MUST treat an exception as
    /// "do not enqueue" — without this gate committed, the cache-drain barrier protects
    /// nothing and Phase 2 would wipe data while warm function-hosts still accept agent
    /// traffic.
    /// <para>
    /// Return value distinguishes the two outcomes so the resume-path can decide whether
    /// to honour the old <c>History.EarliestProcessingAt</c> deadline
    /// (<see cref="EnsureDisabledResult.AlreadyTombstone"/>) or restart the cache-drain
    /// barrier from now (<see cref="EnsureDisabledResult.WroteNewTombstone"/>) — see
    /// resume-path Codex finding "Resume can bypass cache-drain when first attempt failed
    /// before Disabled-write".
    /// </para>
    /// <para>
    /// Behaviour:
    /// <list type="bullet">
    ///   <item>Existing TenantConfiguration row → read-modify-write so signup data + custom
    ///   rate limits etc. are preserved. Idempotent: if already
    ///   <c>Disabled=true</c> with the offboarding reason, no Save is issued and
    ///   <see cref="EnsureDisabledResult.AlreadyTombstone"/> is returned.</item>
    ///   <item>Missing TenantConfiguration row (edge case: very old tenant / manual delete)
    ///   → create a fresh <c>CreateDefault</c> row with <c>Disabled=true</c>. Necessary
    ///   because <c>AuthFunction</c> would otherwise auto-create a <c>Disabled=false</c>
    ///   default on the next <c>/api/auth/me</c> and slip the offboarded tenant back through.</item>
    ///   <item><c>SaveTenantConfigurationAsync</c> returns <c>false</c> on transient storage
    ///   failure (the production repo does NOT throw; see Codex-finding TableConfigRepository).
    ///   We turn that into <see cref="InvalidOperationException"/> so the caller treats it
    ///   as a hard fail.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal async Task<EnsureDisabledResult> EnsureTenantDisabledAsync(string targetTenantId, string upn)
    {
        var tenantConfig = await _configRepo.GetTenantConfigurationAsync(targetTenantId);

        if (tenantConfig != null)
        {
            // Idempotent: if Disabled is already true with our reason, no-op the write so
            // resume-clicks do not thrash the LastUpdated timestamp on the row.
            var alreadyDisabled = tenantConfig.Disabled
                && string.Equals(tenantConfig.DisabledReason, OffboardingDisabledReason, StringComparison.Ordinal);
            if (alreadyDisabled)
            {
                _tenantConfigService.InvalidateCache(targetTenantId);
                return new EnsureDisabledResult(EnsureDisabledOutcome.AlreadyTombstone, tenantConfig.LastUpdated);
            }
        }
        else
        {
            // Missing-row edge case: create a fresh Disabled=true row so AuthFunction's
            // auto-create-default cannot bring the tenant back enabled.
            tenantConfig = TenantConfiguration.CreateDefault(targetTenantId);
            _logger.LogWarning(
                "TenantOffboard: no TenantConfiguration row found for {TenantId} — creating fresh Disabled=true tombstone",
                targetTenantId);
        }

        tenantConfig.Disabled = true;
        tenantConfig.DisabledReason = OffboardingDisabledReason;
        tenantConfig.DisabledUntil = null;
        tenantConfig.UpdatedBy = upn;
        // Explicit LastUpdated stamp — we bypass TenantConfigurationService.SaveConfigurationAsync
        // (which would set this) and write to the repo directly, so we own the bookkeeping.
        // Critical for the resume-path drain-deadline math: this timestamp is the
        // authoritative "when did the cache-drain barrier start" anchor.
        var writtenAt = DateTime.UtcNow;
        tenantConfig.LastUpdated = writtenAt;

        var saved = await _configRepo.SaveTenantConfigurationAsync(tenantConfig);
        if (!saved)
        {
            // TableConfigRepository.SaveTenantConfigurationAsync returns false on transient
            // storage failure instead of throwing. Without throwing here, the caller would
            // proceed to enqueue without the auth-gate committed — defeating the whole
            // cache-drain barrier. Surface as a hard failure.
            throw new InvalidOperationException(
                $"SaveTenantConfigurationAsync returned false for tenant {targetTenantId}; Disabled-gate NOT committed");
        }
        _tenantConfigService.InvalidateCache(targetTenantId);
        return new EnsureDisabledResult(EnsureDisabledOutcome.WroteNewTombstone, writtenAt);
    }

    /// <summary>
    /// Enqueues the offboarding envelope with the given visibility delay (queue-side
    /// invisibility window). First-click: full <see cref="DrainBarrier"/>. Resume-click:
    /// remaining delay computed from <c>EarliestProcessingAt</c> so a re-enqueue does not
    /// reset the drain barrier (the message would otherwise become visible too early).
    /// </summary>
    internal Task EnqueueEnvelopeAsync(
        string normalizedTenantId, string historyRowKey, string upn, DateTime initiatedAt,
        TimeSpan? visibilityDelay)
    {
        var envelope = new TenantOffboardingEnvelope
        {
            EnvelopeVersion = "1",
            TenantId = normalizedTenantId,
            HistoryPartitionKey = Constants.OffboardingPartitionKeys.History,
            HistoryRowKey = historyRowKey,
            InitiatedBy = upn,
            InitiatedAt = initiatedAt,
            EnqueuedAt = DateTime.UtcNow,
            DrainPollCount = 0,
        };
        return _offboardingEnqueuer.EnqueueAsync(envelope, visibilityDelay);
    }

    // Plan §4.4 read-modify-write CAS-loop. Bounded retry; throws on exhaustion so the
    // caller turns it into a 500. The pointer is the O(1) re-onboarding index — failure
    // here is recoverable (graceful degradation in PR4) but we still surface it so the
    // operator can investigate.
    internal async Task UpsertPointerWithCasAsync(
        string normalizedTenantId, string historyRowKey, DateTime now)
    {
        for (var attempt = 1; attempt <= PointerCasMaxAttempts; attempt++)
        {
            var (existing, etag) = await _offboardingRepo.TryGetByTenantPointerAsync(normalizedTenantId);

            if (existing == null)
            {
                var fresh = new OffboardingByTenantPointer
                {
                    PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
                    RowKey = normalizedTenantId,
                    TenantId = normalizedTenantId,
                    LatestHistoryRowKey = historyRowKey,
                    LatestStatus = "Initiated",
                    LatestUpdatedAt = now,
                    OffboardCount = 1,
                };

                try
                {
                    await _offboardingRepo.InsertByTenantPointerAsync(fresh);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    _logger.LogInformation(
                        "OffboardingByTenant pointer raced on insert for {TenantId} (attempt {Attempt}); falling back to ETag update",
                        normalizedTenantId, attempt);
                    continue;
                }
            }

            existing.LatestHistoryRowKey = historyRowKey;
            existing.LatestStatus = "Initiated";
            existing.LatestUpdatedAt = now;
            existing.OffboardCount = existing.OffboardCount + 1;

            try
            {
                await _offboardingRepo.UpdateByTenantPointerWithEtagAsync(existing, etag!);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogInformation(
                    "OffboardingByTenant pointer ETag mismatch for {TenantId} (attempt {Attempt}); retrying",
                    normalizedTenantId, attempt);
            }
        }

        throw new InvalidOperationException(
            $"OffboardingByTenant pointer CAS exhausted for tenant {normalizedTenantId} after {PointerCasMaxAttempts} attempts");
    }

    private static async Task<HttpResponseData> Build500Async(HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.InternalServerError);
        await response.WriteAsJsonAsync(new { success = false, error = message });
        return response;
    }
}

/// <summary>
/// 202/200 response body for the offboarding endpoint. Fields point the caller at the
/// History row so subsequent reporting / status polling can resolve back to the audit
/// trail. <see cref="EarliestProcessingAt"/> drives the "data deletion starts in mm ss"
/// countdown in the Web UI's drain-barrier state.
/// </summary>
public class OffboardResponse
{
    public string TenantId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string HistoryPartitionKey { get; set; } = string.Empty;
    public string HistoryRowKey { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>UTC timestamp before which the worker MUST NOT start Phase 2. Drives the
    /// cache-drain-barrier countdown UI. Null on the idempotent-Completed/Failed branches.</summary>
    public DateTime? EarliestProcessingAt { get; set; }
}

/// <summary>
/// Decision the resume-path produced; consumed by <see cref="TenantOffboardFunction.HandleExistingMarkerAsync"/>
/// to shape the HTTP response. Test-visible so unit tests can assert the re-enqueue decision
/// without faking <c>HttpRequestData</c>.
/// <para>
/// <see cref="EarliestProcessingAt"/> is the History row's drain-barrier deadline for the
/// in-flight path (so the UI can keep counting down through a re-click); null on the
/// terminal idempotent branches.
/// </para>
/// </summary>
internal sealed record ResumeOutcome(
    ResumeOutcomeKind Kind,
    System.Net.HttpStatusCode StatusCode,
    string Message,
    bool ReEnqueued,
    DateTime? EarliestProcessingAt = null);

internal enum ResumeOutcomeKind
{
    /// <summary>Marker is Completed; 15-min grace blocker is held, no re-enqueue.</summary>
    IdempotentCompleted,
    /// <summary>Marker is Failed; operator action required, no re-enqueue.</summary>
    IdempotentFailed,
    /// <summary>Marker is Initiated/InProgress; defensive re-enqueue was sent.</summary>
    ReEnqueuedInFlight,
    /// <summary>Marker classified as in-flight but re-enqueue threw; surface 500.</summary>
    ReEnqueueFailed,
}
