using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Global Admin (or Global Reader) endpoint: lists web users currently active across all tenants,
    /// i.e. those who made an authenticated request within the requested window (default 5 min).
    /// Backed by the self-maintaining UserPresence table (see <c>UserPresenceMiddleware</c>).
    /// Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetActiveUsersFunction
    {
        private const int DefaultWindowMinutes = 5;
        private const int MinWindowMinutes = 1;
        private const int MaxWindowMinutes = 60;

        private readonly ILogger<GetActiveUsersFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetActiveUsersFunction(ILogger<GetActiveUsersFunction> logger, IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetActiveUsers")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/presence")] HttpRequestData req)
        {
            try
            {
                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var windowMinutes = DefaultWindowMinutes;
                if (int.TryParse(query["windowMinutes"], out var parsed))
                    windowMinutes = Math.Clamp(parsed, MinWindowMinutes, MaxWindowMinutes);

                var now = DateTime.UtcNow;
                var active = await _metricsRepo.GetActivePresenceAsync(TimeSpan.FromMinutes(windowMinutes));

                var users = active.Select(u => new ActiveUserItem
                {
                    TenantId = u.TenantId,
                    Upn = u.Upn,
                    UserRole = u.UserRole,
                    LastSeen = u.LastSeen,
                    SecondsAgo = (int)Math.Max(0, (now - u.LastSeen).TotalSeconds)
                }).ToList();

                return await req.OkAsync(new GetActiveUsersResponse
                {
                    Success = true,
                    WindowMinutes = windowMinutes,
                    ActiveCount = users.Count,
                    Users = users
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get active users");
            }
        }
    }
}
