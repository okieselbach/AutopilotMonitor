using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    public class GetDiagnosticsPathsFunction
    {
        private readonly ILogger<GetDiagnosticsPathsFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;

        public GetDiagnosticsPathsFunction(
            ILogger<GetDiagnosticsPathsFunction> logger,
            AdminConfigurationService adminConfigService)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
        }

        /// <summary>
        /// GET /api/diagnostics/paths
        /// What every diagnostics package collects before a tenant's own entries: the built-in
        /// section catalog (compiled into the agent, shared through
        /// <see cref="DiagnosticsBuiltInSections"/>) and the platform-wide global paths set by
        /// Global Admins. Member-readable by design — the catalog is code, the global list is
        /// non-secret admin data, and every settings viewer needs to see what is collected from
        /// the tenant's devices. Tenant-less: the payload is platform-wide, not tenant data.
        /// </summary>
        [Function("GetDiagnosticsPaths")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/paths")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var adminConfig = await _adminConfigService.GetConfigurationAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(BuildPayload(adminConfig));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting diagnostics paths");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>
        /// Static projection so unit tests pin the wire shape without an HttpRequestData mock.
        /// <c>condition</c> travels as the enum NAME ("Always" | "RealmJoinWatcher" |
        /// "DevicePreparation"), never the integer — the web switches on the string.
        /// </summary>
        internal static object BuildPayload(AdminConfiguration adminConfig) => new
        {
            builtIn = DiagnosticsBuiltInSections.All.Select(s => new
            {
                id = s.Id,
                zipFolder = s.ZipFolder,
                sourceFolder = s.SourceFolder,
                patterns = s.Patterns,
                includeSubfolders = s.IncludeSubfolders,
                description = s.Description,
                condition = s.Condition.ToString(),
            }).ToArray(),
            globalPaths = adminConfig.GetDiagnosticsGlobalLogPaths(),
        };
    }
}
