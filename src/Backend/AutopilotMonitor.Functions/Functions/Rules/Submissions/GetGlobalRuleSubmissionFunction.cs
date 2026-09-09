using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules.Submissions
{
    /// <summary>One submission with the frozen rule, live fire stats, the suggested id and the repo file.</summary>
    public class GetGlobalRuleSubmissionFunction
    {
        private readonly ILogger<GetGlobalRuleSubmissionFunction> _logger;
        private readonly RuleSubmissionService _service;

        public GetGlobalRuleSubmissionFunction(ILogger<GetGlobalRuleSubmissionFunction> logger, RuleSubmissionService service)
        {
            _logger = logger;
            _service = service;
        }

        [Function("GetGlobalRuleSubmission")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/rule-submissions/{submissionId}")] HttpRequestData req,
            string submissionId)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                if (string.IsNullOrWhiteSpace(submissionId)) return await req.BadRequestAsync("submissionId is required.");

                var detail = await _service.GetDetailAsync(submissionId);
                if (detail == null) return await req.NotFoundAsync("Submission not found.");
                return await req.OkAsync(detail);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetGlobalRuleSubmission");
            }
        }
    }
}
