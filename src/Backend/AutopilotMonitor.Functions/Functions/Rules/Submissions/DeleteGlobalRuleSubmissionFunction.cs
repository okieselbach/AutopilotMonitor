using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules.Submissions
{
    /// <summary>
    /// Operator hard delete of a submission (test/demo rows, or a tenant's request). The row
    /// vanishes from both lists; a published rule in the repo is untouched.
    /// </summary>
    public class DeleteGlobalRuleSubmissionFunction
    {
        private readonly ILogger<DeleteGlobalRuleSubmissionFunction> _logger;
        private readonly RuleSubmissionService _service;

        public DeleteGlobalRuleSubmissionFunction(ILogger<DeleteGlobalRuleSubmissionFunction> logger, RuleSubmissionService service)
        {
            _logger = logger;
            _service = service;
        }

        [Function("DeleteGlobalRuleSubmission")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/rule-submissions/{submissionId}")] HttpRequestData req,
            string submissionId)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var user = TenantHelper.GetUserIdentifier(req);
                if (string.IsNullOrWhiteSpace(submissionId)) return await req.BadRequestAsync("submissionId is required.");

                var existing = await _service.GetAsync(submissionId);
                if (existing == null) return await req.NotFoundAsync("Submission not found.");

                if (!await _service.DeleteAsync(submissionId)) return await req.NotFoundAsync("Submission not found.");

                // Warning on purpose: an operator delete is rare and must be visible in App Insights.
                _logger.LogWarning("Rule submission {SubmissionId} ({RuleId}, tenant {TenantId}, status {Status}) deleted by {User}",
                    submissionId, existing.SourceRuleId, existing.TenantId, existing.Status, user);
                return await req.OkAsync(new SuccessMessageResponse { Success = true, Message = "Submission deleted." });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "DeleteGlobalRuleSubmission");
            }
        }
    }
}
