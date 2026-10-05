using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Feedback;

/// <summary>
/// Free-text feedback from the portal's help menu ("Send feedback"), open to every member of a
/// tenant and to the platform roles (catalog policy <c>MemberRead</c>).
/// <para>
/// Each submission is its own row in the <c>Feedback</c> table (PK <c>General</c>), raises a
/// <c>FeedbackReceived</c> ops event that carries neither the text nor the sender, and leaves an
/// entry in the Global Admin bell. A user may send
/// <see cref="Constants.SubmissionLimits.GeneralFeedbackPerUserPerDay"/> per rolling 24 hours; the
/// count comes from the table, so it holds across instances.
/// </para>
/// </summary>
public class SubmitGeneralFeedbackFunction
{
    private const long MaxBodyBytes = 65_536;
    private const string ReportsPath = "/admin/reports/user-feedback";

    private readonly ILogger<SubmitGeneralFeedbackFunction> _logger;
    private readonly IFeedbackRepository _feedbackRepo;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly OpsEventService _opsEvents;
    private readonly GlobalNotificationService _globalNotifications;

    public SubmitGeneralFeedbackFunction(
        ILogger<SubmitGeneralFeedbackFunction> logger,
        IFeedbackRepository feedbackRepo,
        TenantConfigurationService tenantConfigService,
        OpsEventService opsEvents,
        GlobalNotificationService globalNotifications)
    {
        _logger = logger;
        _feedbackRepo = feedbackRepo;
        _tenantConfigService = tenantConfigService;
        _opsEvents = opsEvents;
        _globalNotifications = globalNotifications;
    }

    [Function("SubmitGeneralFeedback")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/general")] HttpRequestData req)
    {
        try
        {
            var requestCtx = req.GetRequestContext();

            var read = await req.ReadAsync<GeneralFeedbackRequest>(MaxBodyBytes);
            if (read.Error != null) return read.Error;

            var upn = requestCtx.UserPrincipalName;
            var displayName = req.FunctionContext.GetUser()?.GetDisplayName() ?? upn;

            var result = await ProcessSubmitAsync(requestCtx.TenantId, upn, displayName, read.Value!, DateTime.UtcNow);

            return result.Outcome switch
            {
                GeneralFeedbackOutcome.Ok => await req.OkAsync(new SuccessOnlyResponse { Success = true }),
                GeneralFeedbackOutcome.BadRequest => await req.BadRequestAsync(result.Message!),
                GeneralFeedbackOutcome.RateLimited => await req.ErrorAsync(
                    HttpStatusCode.TooManyRequests, Constants.ApiErrorCodes.RateLimited, result.Message!),
                _ => await req.ErrorAsync(
                    HttpStatusCode.InternalServerError, Constants.ApiErrorCodes.InternalError, result.Message!),
            };
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "SubmitGeneralFeedback");
        }
    }

    /// <summary>
    /// The submit decision without the HTTP shell, so tests drive every branch directly
    /// (same pattern as <c>SubmitOffboardingFeedbackFunction.ProcessSubmitAsync</c>).
    /// </summary>
    internal async Task<GeneralFeedbackResult> ProcessSubmitAsync(
        string tenantId, string upn, string displayName, GeneralFeedbackRequest body, DateTime nowUtc)
    {
        if (!SecurityValidator.IsValidGuid(tenantId) || string.IsNullOrWhiteSpace(upn))
            return GeneralFeedbackResult.Bad("The sign-in identity carries no tenant or user name.");

        var message = (body.Message ?? string.Empty).Trim();
        if (message.Length == 0)
            return GeneralFeedbackResult.Bad("Feedback text is required.");
        if (message.Length > Constants.SubmissionLimits.FeedbackTextMaxChars)
            return GeneralFeedbackResult.Bad(
                $"Feedback text is limited to {Constants.SubmissionLimits.FeedbackTextMaxChars} characters.");

        var contactEmail = string.IsNullOrWhiteSpace(body.ContactEmail) ? null : body.ContactEmail.Trim();
        if (TenantConfigValidation.ValidateContactEmail(contactEmail) is { } emailError)
            return GeneralFeedbackResult.Bad($"Invalid contact address: {emailError}");

        var recent = await _feedbackRepo.CountGeneralFeedbackSinceAsync(upn, nowUtc.AddHours(-24));
        if (recent >= Constants.SubmissionLimits.GeneralFeedbackPerUserPerDay)
        {
            _logger.LogWarning("General feedback refused for {Upn}: {Count} submissions in the last 24 hours",
                upn, recent);
            return new GeneralFeedbackResult(GeneralFeedbackOutcome.RateLimited,
                $"You have sent {Constants.SubmissionLimits.GeneralFeedbackPerUserPerDay} messages in the last 24 hours. " +
                "Please try again later.");
        }

        var normalizedTenantId = tenantId.ToLowerInvariant();
        // The domain makes the admin list and the alert readable. The lookup never creates a
        // configuration row; an unknown tenant simply keeps its GUID label.
        var (config, exists) = await _tenantConfigService.TryGetConfigurationAsync(normalizedTenantId);
        var domainName = exists && !string.IsNullOrWhiteSpace(config.DomainName) ? config.DomainName : null;

        var entry = new FeedbackEntry
        {
            TenantId = normalizedTenantId,
            Upn = upn,
            DisplayName = displayName,
            DomainName = domainName,
            Comment = message,
            ContactEmail = contactEmail,
            InteractedAt = nowUtc,
        };

        try
        {
            await _feedbackRepo.SaveGeneralFeedbackAsync(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store general feedback for {Upn} (tenant {TenantId})", upn, normalizedTenantId);
            return new GeneralFeedbackResult(GeneralFeedbackOutcome.InternalError, "The feedback could not be stored.");
        }

        // Both signals are best effort: a stored submission never fails because of them.
        try
        {
            await _opsEvents.RecordFeedbackReceivedAsync(
                normalizedTenantId, entry.Upn, domainName, entry.FeedbackId!, message.Length, contactEmail != null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FeedbackReceived ops event failed for tenant {TenantId}", normalizedTenantId);
        }

        _ = _globalNotifications.CreateNotificationAsync(
            "feedback", "New Feedback", $"{entry.Upn} ({domainName ?? normalizedTenantId})", href: ReportsPath);

        _logger.LogInformation("General feedback {FeedbackId} stored for tenant {TenantId} ({Length} chars)",
            entry.FeedbackId, normalizedTenantId, message.Length);

        return new GeneralFeedbackResult(GeneralFeedbackOutcome.Ok, null);
    }
}

internal enum GeneralFeedbackOutcome
{
    Ok,
    BadRequest,
    RateLimited,
    InternalError,
}

internal sealed record GeneralFeedbackResult(GeneralFeedbackOutcome Outcome, string? Message)
{
    public static GeneralFeedbackResult Bad(string message) => new(GeneralFeedbackOutcome.BadRequest, message);
}
