using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules.Submissions
{
    /// <summary>The submitter takes a pending submission back. The row stays (status withdrawn) for the audit trail.</summary>
    public class WithdrawRuleSubmissionFunction
    {
        private readonly ILogger<WithdrawRuleSubmissionFunction> _logger;
        private readonly RuleSubmissionService _service;

        public WithdrawRuleSubmissionFunction(ILogger<WithdrawRuleSubmissionFunction> logger, RuleSubmissionService service)
        {
            _logger = logger;
            _service = service;
        }

        [Function("WithdrawRuleSubmission")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rules/submissions/{submissionId}")] HttpRequestData req,
            string submissionId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                if (string.IsNullOrWhiteSpace(submissionId)) return await req.BadRequestAsync("submissionId is required.");

                bool withdrawn;
                try
                {
                    // A foreign tenant's submission answers 404 for a non-GA — the row's existence is not theirs to learn.
                    withdrawn = await _service.WithdrawAsync(requestCtx.TenantId, submissionId, crossTenantAllowed: requestCtx.IsGlobalAdmin);
                }
                catch (InvalidOperationException ex)
                {
                    return await req.ConflictAsync(ex.Message);
                }

                if (!withdrawn) return await req.NotFoundAsync("Submission not found.");

                _logger.LogInformation("Rule submission {SubmissionId} withdrawn by {User}", submissionId, requestCtx.UserPrincipalName);
                return await req.OkAsync(new SuccessMessageResponse { Success = true, Message = "Submission withdrawn." });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "WithdrawRuleSubmission");
            }
        }
    }
}
