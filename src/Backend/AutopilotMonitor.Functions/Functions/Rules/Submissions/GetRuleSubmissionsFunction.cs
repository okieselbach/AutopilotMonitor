using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules.Submissions
{
    /// <summary>The submitting tenant's own submissions, newest first, with the effective status.</summary>
    public class GetRuleSubmissionsFunction
    {
        private readonly ILogger<GetRuleSubmissionsFunction> _logger;
        private readonly RuleSubmissionService _service;

        public GetRuleSubmissionsFunction(ILogger<GetRuleSubmissionsFunction> logger, RuleSubmissionService service)
        {
            _logger = logger;
            _service = service;
        }

        [Function("GetRuleSubmissions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/submissions")] HttpRequestData req)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware.
                // Always the JWT tenant — a GA reads foreign tenants through global/rule-submissions?tenantId=.
                var tenantId = req.GetRequestContext().TenantId;
                var rows = await _service.GetForTenantAsync(tenantId);
                var items = await _service.ToItemsAsync(rows);
                return await req.OkAsync(new RuleSubmissionListResponse { Success = true, Count = items.Count, Submissions = items });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetRuleSubmissions");
            }
        }
    }
}
