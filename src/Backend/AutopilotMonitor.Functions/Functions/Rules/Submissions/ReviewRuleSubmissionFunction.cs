using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules.Submissions
{
    /// <summary>The reviewer's decision (portal or MCP). The submitter is told through a tenant notification.</summary>
    public class ReviewRuleSubmissionFunction
    {
        private readonly ILogger<ReviewRuleSubmissionFunction> _logger;
        private readonly RuleSubmissionService _service;
        private readonly TenantNotificationService _tenantNotifications;

        public ReviewRuleSubmissionFunction(
            ILogger<ReviewRuleSubmissionFunction> logger,
            RuleSubmissionService service,
            TenantNotificationService tenantNotifications)
        {
            _logger = logger;
            _service = service;
            _tenantNotifications = tenantNotifications;
        }

        [Function("ReviewRuleSubmission")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/rule-submissions/{submissionId}")] HttpRequestData req,
            string submissionId)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var reviewer = TenantHelper.GetUserIdentifier(req);
                if (string.IsNullOrWhiteSpace(submissionId)) return await req.BadRequestAsync("submissionId is required.");

                var read = await req.ReadAsync<ReviewRuleSubmissionRequest>();
                if (read.Error != null) return read.Error;

                RuleSubmission updated;
                try
                {
                    updated = await _service.ReviewAsync(submissionId, read.Value!, reviewer);
                }
                catch (KeyNotFoundException ex)
                {
                    return await req.NotFoundAsync(ex.Message);
                }
                catch (ArgumentException ex)
                {
                    return await req.BadRequestAsync(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return await req.ConflictAsync(ex.Message);
                }

                var approved = updated.Status == RuleSubmissionStatuses.Approved;
                var title = approved
                    ? $"Rule submission approved: {updated.SourceRuleId}"
                    : $"Rule submission declined: {updated.SourceRuleId}";
                var message = approved
                    ? (updated.WillBeAdapted
                        ? $"\"{updated.Title}\" will be published as {updated.PublishedRuleId} after a small adaptation for the community."
                        : $"\"{updated.Title}\" will be published as {updated.PublishedRuleId}.")
                    : $"\"{updated.Title}\" was not accepted for the community pool.";
                if (!string.IsNullOrEmpty(updated.ReviewComment)) message += $" Reviewer: {updated.ReviewComment}";

                // Best effort; the submissions list carries the same facts.
                _ = _tenantNotifications.CreateNotificationAsync(
                    updated.TenantId, "rule_submission_decided", title, message,
                    href: updated.RuleKind == RuleSubmissionKinds.Analyze ? "/analyze-rules" : "/gather-rules");

                _logger.LogInformation("Rule submission {SubmissionId} {Status} by {Reviewer} (published id {PublishedRuleId})",
                    submissionId, updated.Status, reviewer, updated.PublishedRuleId ?? "-");

                var item = (await _service.ToItemsAsync(new[] { updated }))[0];
                return await req.OkAsync(new ReviewRuleSubmissionResponse
                {
                    Success = true,
                    Message = approved ? "Submission approved." : "Submission declined.",
                    Submission = item,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "ReviewRuleSubmission");
            }
        }
    }
}
