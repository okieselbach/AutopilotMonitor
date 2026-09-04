using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Captures the optional farewell-feedback an offboarding admin can submit while the
/// drain-barrier countdown is running (after <c>DELETE /tenants/{tenantId}/offboard</c>
/// returns 202 but before the worker starts Phase 2 ~6 min later).
/// <para>
/// Stored in the dedicated <c>Feedback</c> table (PK="Offboarding", RK=historyRowKey) so
/// it survives the offboarding wipe — the whole point of capturing it is to learn from
/// tenants that are leaving. The matching <c>OffboardingHistory</c> row provides the
/// canonical historyRowKey and InitiatedBy.
/// </para>
/// <para>
/// Authorization: tenant admin / GA on the target tenant (catalog policy). The marker is
/// re-validated here to defend against an old token reaching the endpoint after the
/// offboarding actually completed — only Initiated/InProgress markers accept feedback.
/// </para>
/// </summary>
public class SubmitOffboardingFeedbackFunction
{
    // Departing tenants often have more context to share than the in-app 500-char limit
    // allows. Azure Table Storage caps a single string property at 64 KB, so 4096 chars
    // (max ~16 KB UTF-8) sits well within both the table and the 16 KB request-body
    // guard above.
    private const int MaxCommentLength = 4096;

    private readonly ILogger<SubmitOffboardingFeedbackFunction> _logger;
    private readonly IOffboardingAuditRepository _offboardingRepo;
    private readonly IFeedbackRepository _feedbackRepo;
    private readonly OpsEventService _opsEvents;

    public SubmitOffboardingFeedbackFunction(
        ILogger<SubmitOffboardingFeedbackFunction> logger,
        IOffboardingAuditRepository offboardingRepo,
        IFeedbackRepository feedbackRepo,
        OpsEventService opsEvents)
    {
        _logger = logger;
        _offboardingRepo = offboardingRepo;
        _feedbackRepo = feedbackRepo;
        _opsEvents = opsEvents;
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/offboard/feedback
    /// Accessible by: Tenant Admins of the same tenant OR Global Admins (PolicyEnforcementMiddleware).
    /// </summary>
    [Function("SubmitOffboardingFeedback")]
    [Authorize]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/offboard/feedback")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;
        var targetTenantId = requestCtx.TargetTenantId;

        if (string.IsNullOrEmpty(targetTenantId) || !SecurityValidator.IsValidGuid(targetTenantId))
        {
            return await BadRequest(req, "tenantId must be a valid GUID");
        }

        // Reject oversized bodies before parsing — defense against accidental clipboard-paste
        // of huge logs into the textarea. 64 KB is the Azure Table Storage single-property
        // cap, which gives 4096 chars (max ~16 KB UTF-8) plus JSON envelope plenty of room
        // while still bouncing pathological multi-MB pastes before we read them.
        if (req.Headers.TryGetValues("Content-Length", out var clValues)
            && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
            && contentLength > 65_536)
        {
            return await BadRequest(req, "Request body too large");
        }

        SubmitOffboardingFeedbackRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<SubmitOffboardingFeedbackRequest>();
        }
        catch (Exception)
        {
            return await BadRequest(req, "Invalid request body");
        }

        var principal = req.FunctionContext.GetUser();
        var displayName = principal?.GetDisplayName() ?? upn;

        var result = await ProcessSubmitAsync(
            targetTenantId.ToLowerInvariant(), upn, displayName, body?.Comment);

        return result.Outcome switch
        {
            SubmitOutcome.Ok => await Ok(req),
            SubmitOutcome.BadRequest => await BadRequest(req, result.Message ?? "Bad request"),
            SubmitOutcome.NotFound => await NotFound(req, result.Message ?? "Not found"),
            SubmitOutcome.Conflict => await Conflict(req, result.Message ?? "Conflict"),
            _ => await Build500(req, result.Message ?? "Internal error"),
        };
    }

    /// <summary>
    /// Pure (well, side-effectful only via the injected repositories + OpsEvent) decision
    /// helper. Exposed internal so unit tests can drive every outcome branch without
    /// having to fake <see cref="HttpRequestData"/>. Mirrors the
    /// <see cref="TenantOffboardFunction.ResumeExistingMarkerAsync"/> pattern.
    /// </summary>
    internal async Task<SubmitResult> ProcessSubmitAsync(
        string normalizedTenantId, string upn, string displayName, string? rawComment)
    {
        var comment = rawComment?.Trim() ?? string.Empty;
        if (comment.Length == 0)
        {
            return new SubmitResult(SubmitOutcome.BadRequest, "Comment is required");
        }
        if (comment.Length > MaxCommentLength)
        {
            comment = comment.Substring(0, MaxCommentLength);
        }

        // Marker is the authoritative active-offboarding anchor. The endpoint is policy-gated
        // by tenant-admin/GA but we additionally require an in-flight marker, because:
        //   - PolicyEnforcementMiddleware only checks role membership, not whether an
        //     offboarding actually exists.
        //   - The textarea is only ever rendered while the banner is visible (after a
        //     successful DELETE), so a missing marker means either the user crafted a
        //     direct API call, or the offboarding completed and the marker was cleaned up.
        var marker = await _offboardingRepo.TryGetMarkerAsync(normalizedTenantId);
        if (marker == null)
        {
            _logger.LogInformation(
                "OffboardingFeedback rejected for {TenantId} from {Upn}: no active marker",
                normalizedTenantId, upn);
            return new SubmitResult(SubmitOutcome.NotFound, "No active offboarding for this tenant");
        }
        if (!IsOpenForFeedback(marker.Status))
        {
            _logger.LogInformation(
                "OffboardingFeedback rejected for {TenantId} from {Upn}: marker status is {Status} (only Initiated/InProgress accept feedback)",
                normalizedTenantId, upn, marker.Status);
            return new SubmitResult(SubmitOutcome.Conflict,
                $"Offboarding is in '{marker.Status}' state and no longer accepts feedback");
        }

        // History row carries DomainName so the Reports page can render a friendly label
        // after the tenant config is wiped in Phase 2.D.
        var history = await _offboardingRepo.TryGetHistoryAsync(marker.OffboardingHistoryRowKey);
        var domainName = history?.DomainName;

        var entry = new FeedbackEntry
        {
            HistoryRowKey = marker.OffboardingHistoryRowKey,
            TenantId = normalizedTenantId,
            Upn = upn,
            DisplayName = displayName,
            DomainName = domainName,
            Comment = comment,
            InteractedAt = DateTime.UtcNow,
        };

        try
        {
            await _feedbackRepo.SaveOffboardingFeedbackAsync(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist offboarding feedback for {TenantId} history={History}",
                normalizedTenantId, marker.OffboardingHistoryRowKey);
            return new SubmitResult(SubmitOutcome.InternalError, "Failed to persist feedback");
        }

        // Best-effort OpsEvent so operators see "feedback received" on the Ops dashboard.
        // Telegram-wiring done by the admin via OpsAlertRules UI (memory:
        // feedback_ops_event_types_dual_register).
        try
        {
            await _opsEvents.RecordOffboardingFeedbackReceivedAsync(
                normalizedTenantId, upn, domainName, marker.OffboardingHistoryRowKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "OpsEvent emit failed for offboarding feedback (tenant={Tenant}) — ignored, not a correctness contract",
                normalizedTenantId);
        }

        _logger.LogInformation(
            "Offboarding feedback recorded for tenant={Tenant} history={History} by {Upn} ({Length} chars)",
            normalizedTenantId, marker.OffboardingHistoryRowKey, upn, comment.Length);

        return new SubmitResult(SubmitOutcome.Ok, null);
    }

    private static bool IsOpenForFeedback(string? markerStatus) =>
        string.Equals(markerStatus, "Initiated", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(markerStatus, "InProgress", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpResponseData> Ok(HttpRequestData req)
    {
        var r = req.CreateResponse(HttpStatusCode.OK);
        await r.WriteAsJsonAsync(new SuccessOnlyResponse { Success = true });
        return r;
    }

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
    {
        return await req.BadRequestAsync(message);
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req, string message)
    {
        return await req.NotFoundAsync(message);
    }

    private static async Task<HttpResponseData> Conflict(HttpRequestData req, string message)
    {
        return await req.ConflictAsync(message);
    }

    private static async Task<HttpResponseData> Build500(HttpRequestData req, string message)
    {
        return await req.ErrorAsync(HttpStatusCode.InternalServerError, Constants.ApiErrorCodes.InternalError, message);
    }
}

public class SubmitOffboardingFeedbackRequest
{
    public string? Comment { get; set; }
}

internal enum SubmitOutcome
{
    Ok,
    BadRequest,
    NotFound,
    Conflict,
    InternalError,
}

internal sealed record SubmitResult(SubmitOutcome Outcome, string? Message);
