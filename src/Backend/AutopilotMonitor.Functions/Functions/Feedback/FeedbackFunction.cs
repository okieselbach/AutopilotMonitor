using System;
using System.Linq;
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

namespace AutopilotMonitor.Functions.Functions.Feedback
{
    public class FeedbackFunction
    {
        private readonly ILogger<FeedbackFunction> _logger;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TenantAdminsService _tenantAdminsService;
        private readonly TelegramNotificationService _telegramNotificationService;
        private readonly ISessionRepository _sessionRepo;
        private readonly IFeedbackRepository _feedbackRepo;
        private readonly OpsEventService _opsEvents;
        private readonly GlobalNotificationService _globalNotifications;

        public FeedbackFunction(
            ILogger<FeedbackFunction> logger,
            TenantConfigurationService tenantConfigService,
            AdminConfigurationService adminConfigService,
            TenantAdminsService tenantAdminsService,
            TelegramNotificationService telegramNotificationService,
            ISessionRepository sessionRepo,
            IFeedbackRepository feedbackRepo,
            OpsEventService opsEvents,
            GlobalNotificationService globalNotifications)
        {
            _logger = logger;
            _tenantConfigService = tenantConfigService;
            _adminConfigService = adminConfigService;
            _tenantAdminsService = tenantAdminsService;
            _telegramNotificationService = telegramNotificationService;
            _sessionRepo = sessionRepo;
            _feedbackRepo = feedbackRepo;
            _opsEvents = opsEvents;
            _globalNotifications = globalNotifications;
        }

        /// <summary>
        /// Checks whether the current user is eligible to see the feedback bubble.
        /// Evaluates: kill-switch, role, tenant age, session existence, and the ask rules
        /// (<see cref="FeedbackPromptRules"/>: at most two asks, never after a rating).
        /// </summary>
        [Function("GetFeedbackStatus")]
        public async Task<HttpResponseData> GetStatus(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "feedback/status")] HttpRequestData req,
            FunctionContext context)
        {
            try
            {
                // 1. Kill-switch — gate first so a disabled feature doesn't fan out
                //    four extra Table reads on every poll from every active user.
                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                if (!adminConfig.FeedbackEnabled)
                    return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = false });

                // 2. User identity
                string tenantId = TenantHelper.GetTenantId(req);
                string upn = TenantHelper.GetUserIdentifier(req);

                // 3-6. The remaining checks each hit a different Table partition and
                //      depend only on (tenantId, upn) — fan them out in parallel so
                //      the endpoint is bounded by the slowest read instead of summing
                //      four sequential round-trips on Flex Consumption.
                var membershipTask = _tenantAdminsService.GetTableMembershipAsync(tenantId, upn);
                var tenantConfigTask = _tenantConfigService.GetConfigurationIfExistsAsync(tenantId);
                var sessionsTask = _sessionRepo.GetSessionsPageAsync(tenantId, days: null, pageSize: 1, continuation: null);
                var feedbackTask = _feedbackRepo.GetInAppFeedbackAsync(upn);

                await Task.WhenAll(membershipTask, tenantConfigTask, sessionsTask, feedbackTask);

                // Tenant config is needed both for the app-role opt-in flag (role check) and the
                // tenant age check below. No row means not onboarded, so not eligible — and this
                // poll runs for every signed-in user, so it must never create the row.
                var tenantConfig = tenantConfigTask.Result;
                if (tenantConfig == null)
                    return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = false });

                // 3. Role check — only Admin + Operator. Use the effective role so Entra-only
                //    members (app-role claim, no table row) are eligible too, consistent with
                //    auth/me and the protected APIs. A disabled table row stays ineligible.
                var (tableState, tableRole) = membershipTask.Result;
                var roleInfo = EntraAppRoleResolver.Resolve(
                    tableState, tableRole, context.GetUser()?.GetAppRoles(), tenantConfig.EntraAppRolesEnabled);
                if (roleInfo == null || roleInfo.Role == Constants.TenantRoles.Viewer)
                    return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = false });

                // 4. Tenant age check
                if (tenantConfig.OnboardedAt == null ||
                    (DateTime.UtcNow - tenantConfig.OnboardedAt.Value).TotalDays < adminConfig.FeedbackMinTenantAgeDays)
                    return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = false });

                // 5. Sessions check — at least 1 session exists.
                var sessionPage = sessionsTask.Result;
                if (sessionPage.Items.Count == 0)
                    return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = false });

                // 6. Ask at most twice: a rating ends it, the second ask waits for the pause.
                if (!FeedbackPromptRules.IsEligible(feedbackTask.Result, adminConfig.FeedbackCooldownDays, DateTime.UtcNow))
                    return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = false });

                return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = true });
            }
            catch (UnauthorizedAccessException)
            {
                var response = req.CreateResponse(HttpStatusCode.Unauthorized);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking feedback status");
                // Fail closed — don't show bubble on errors
                return await WriteJson(req, new FeedbackEligibilityResponse { Eligible = false });
            }
        }

        /// <summary>
        /// Records one answer to the rating bubble (<see cref="FeedbackPromptRules"/>): a rating is
        /// stored once per person, a dismissal is counted. After a rating the person's row is final:
        /// further answers change nothing.
        /// </summary>
        [Function("SubmitFeedback")]
        public async Task<HttpResponseData> Submit(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback")] HttpRequestData req)
        {
            try
            {
                string tenantId = TenantHelper.GetTenantId(req);
                string upn = TenantHelper.GetUserIdentifier(req);
                var principal = req.FunctionContext.GetUser();
                string displayName = principal?.GetDisplayName() ?? upn;

                // Request body size limit (1 MB)
                var read = await req.ReadAsync<FeedbackRequest>(1_048_576);
                if (read.Error != null) return read.Error;

                var error = await ProcessSubmitAsync(tenantId, upn, displayName, read.Value!, DateTime.UtcNow);
                return error != null
                    ? await req.BadRequestAsync(error)
                    : await WriteJson(req, new SuccessOnlyResponse { Success = true });
            }
            catch (UnauthorizedAccessException)
            {
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Feedback");
            }
        }

        /// <summary>
        /// The answer decision without the HTTP shell, so tests drive every branch directly (same
        /// pattern as <c>SubmitGeneralFeedbackFunction.ProcessSubmitAsync</c>). Returns the validation
        /// error, or null once the answer is handled.
        /// </summary>
        internal async Task<string?> ProcessSubmitAsync(
            string tenantId, string upn, string displayName, FeedbackRequest body, DateTime nowUtc)
        {
            if (!body.Dismissed && body.Rating is not (>= 1 and <= 5))
                return "Rating must be between 1 and 5";

            // Trim comment to the shared feedback limit (Azure Tables caps a single property
            // at 64 KB, so this stays well within).
            var comment = body.Comment?.Trim();
            if (comment?.Length > Constants.SubmissionLimits.FeedbackTextMaxChars)
                comment = comment.Substring(0, Constants.SubmissionLimits.FeedbackTextMaxChars);

            var answer = new FeedbackAnswer(upn, tenantId, displayName, body.Dismissed, body.Rating, comment);
            var stored = await _feedbackRepo.UpdateInAppFeedbackAsync(
                upn, current => FeedbackPromptRules.Apply(current, answer, nowUtc));

            // Only a rating that was actually recorded is announced — at most once per person.
            if (stored is { Submitted: true } && stored.Rating is int rating)
                await AnnounceRatingAsync(tenantId, upn, displayName, rating, comment);

            _logger.LogInformation("Feedback {Action} by {Upn} (tenant {TenantId})",
                stored == null ? "unchanged" : body.Dismissed ? "dismissed" : "submitted", upn, tenantId);

            return null;
        }

        /// <summary>
        /// The signals of a recorded rating, all best effort: a stored rating never fails because of
        /// them. The ops event carries the stars and the comment length only, so an alert channel
        /// receives no personal data; the Telegram ping keeps sender and comment until it is retired.
        /// </summary>
        private async Task AnnounceRatingAsync(string tenantId, string upn, string displayName, int rating, string? comment)
        {
            _ = _telegramNotificationService.SendFeedbackAsync(tenantId, upn, displayName, rating, comment);

            var normalizedTenantId = tenantId.ToLowerInvariant();
            // The domain makes the alert and the bell readable. The lookup never creates a
            // configuration row; an unknown tenant simply keeps its GUID label.
            var (config, exists) = await _tenantConfigService.TryGetConfigurationAsync(normalizedTenantId);
            var domainName = exists && !string.IsNullOrWhiteSpace(config.DomainName) ? config.DomainName : null;

            try
            {
                await _opsEvents.RecordFeedbackRatingReceivedAsync(
                    normalizedTenantId, upn, domainName, rating, comment?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FeedbackRatingReceived ops event failed for tenant {TenantId}", normalizedTenantId);
            }

            _ = _globalNotifications.CreateNotificationAsync(
                "feedback", "New Rating", $"{upn} ({domainName ?? normalizedTenantId}) · {rating}/5",
                href: Constants.PortalUserFeedbackPath);
        }

        /// <summary>
        /// Returns all feedback entries for the Global Admin dashboard.
        /// </summary>
        [Function("GetAllFeedback")]
        public async Task<HttpResponseData> GetAll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "feedback/all")] HttpRequestData req)
        {
            try
            {
                var allEntries = await _feedbackRepo.GetAllAsync();
                var entries = allEntries.Select(e => new FeedbackEntryWire
                {
                    Type = e.Type,
                    Upn = e.Upn,
                    TenantId = e.TenantId,
                    DisplayName = e.DisplayName,
                    Rating = e.Rating,
                    Comment = e.Comment,
                    Dismissed = e.Dismissed,
                    Submitted = e.Submitted,
                    InteractedAt = e.InteractedAt?.ToString("o"),
                    HistoryRowKey = e.HistoryRowKey,
                    DomainName = e.DomainName,
                    FeedbackId = e.FeedbackId,
                    ContactEmail = e.ContactEmail,
                    DismissCount = e.Type == FeedbackEntryType.InApp ? e.DismissCount : null,
                }).ToList();

                return await WriteJson(req, new FeedbackListResponse { Feedback = entries });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Feedback");
            }
        }

        // Generic with an IApiResponse constraint (NOT an IApiResponse parameter): success
        // bodies through this wrapper are typed by construction — an anonymous body no longer
        // compiles — while the CONCRETE type still reaches the serializer (a declared interface
        // type would serialize zero properties under System.Text.Json).
        private static async Task<HttpResponseData> WriteJson<T>(HttpRequestData req, T data) where T : IApiResponse
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(data);
            return response;
        }
    }
}
