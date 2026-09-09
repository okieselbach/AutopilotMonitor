using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules.Submissions
{
    /// <summary>Operator list across tenants: <c>?tenantId=</c> and <c>?status=</c> are server-side filters, paging as for session reports.</summary>
    public class GetGlobalRuleSubmissionsFunction
    {
        private readonly ILogger<GetGlobalRuleSubmissionsFunction> _logger;
        private readonly RuleSubmissionService _service;

        public GetGlobalRuleSubmissionsFunction(ILogger<GetGlobalRuleSubmissionsFunction> logger, RuleSubmissionService service)
        {
            _logger = logger;
            _service = service;
        }

        [Function("GetGlobalRuleSubmissions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/rule-submissions")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var callerTenantId = TenantHelper.GetTenantId(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = RuleSubmissionsPagination.ParseQuery(query);
                if (parsed.Error != null) return await req.BadRequestAsync(parsed.Error);

                var pageSize = parsed.PageSize ?? RuleSubmissionsPagination.DefaultPageSize;

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!RuleSubmissionsPagination.TryAcceptContinuation(
                            parsed.Continuation, callerTenantId, parsed.FilterTenantId, parsed.FilterStatus,
                            out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("GetGlobalRuleSubmissions: continuation rejected ({Reason})", rejectReason);
                        return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                    }
                }

                var page = await _service.GetPageAsync(parsed.FilterTenantId, parsed.FilterStatus, pageSize, azureToken);
                var items = await _service.ToItemsAsync(page.Items);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = RuleSubmissionsPagination.Fingerprint(callerTenantId, parsed.FilterTenantId, parsed.FilterStatus);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                    nextLink = RuleSubmissionsPagination.BuildNextLink(pageSize, wireToken, parsed.FilterTenantId, parsed.FilterStatus);
                }

                return await req.OkAsync(new RuleSubmissionListResponse
                {
                    Success = true,
                    Count = items.Count,
                    Submissions = items,
                    NextLink = nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetGlobalRuleSubmissions");
            }
        }
    }
}
