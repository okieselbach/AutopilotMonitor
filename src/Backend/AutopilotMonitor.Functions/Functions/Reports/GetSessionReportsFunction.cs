using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    public class GetSessionReportsFunction
    {
        private readonly ILogger<GetSessionReportsFunction> _logger;
        private readonly SessionReportService _sessionReportService;

        public GetSessionReportsFunction(
            ILogger<GetSessionReportsFunction> logger,
            SessionReportService sessionReportService)
        {
            _logger = logger;
            _sessionReportService = sessionReportService;
        }

        [Function("GetSessionReports")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/session-reports")] HttpRequestData req)
        {
            _logger.LogInformation("GetSessionReports function processing request");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var callerTenantId = TenantHelper.GetTenantId(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = SessionReportsPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    return await req.BadRequestAsync(parsed.Error);
                }

                if (parsed.PageSize == null)
                {
                    var reports = await _sessionReportService.GetAllReportsAsync(parsed.FilterTenantId);
                    return await req.OkAsync(new SessionReportListResponse { Success = true, Count = reports.Count, Reports = reports });
                }

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!SessionReportsPagination.TryAcceptContinuation(
                            parsed.Continuation, callerTenantId, parsed.FilterTenantId,
                            out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("GetSessionReports: continuation rejected ({Reason})", rejectReason);
                        return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                    }
                }

                var page = await _sessionReportService.GetReportsPageAsync(
                    parsed.FilterTenantId, parsed.PageSize.Value, azureToken);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = SessionReportsPagination.Fingerprint(callerTenantId, parsed.FilterTenantId);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                    nextLink = SessionReportsPagination.BuildNextLink(
                        parsed.PageSize.Value, wireToken, parsed.FilterTenantId);
                }

                return await req.OkAsync(new SessionReportListResponse
                {
                    Success = true,
                    Count = page.Items.Count,
                    Reports = page.Items,
                    NextLink = nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetSessionReports");
            }
        }
    }
}
