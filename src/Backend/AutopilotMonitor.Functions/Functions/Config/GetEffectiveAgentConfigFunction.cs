using System;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Global-Admin view of what an agent of the tenant receives from GET agent/config right
    /// now: the same <see cref="AgentConfigResolver"/> derivation the agent channel uses, minus
    /// the per-device kill verdict (no device here). Drives the runtime column of the Tenant
    /// Config Report, so the report cannot drift from the backend rules. Read-only, no
    /// side effects, no ops event.
    /// </summary>
    public class GetEffectiveAgentConfigFunction
    {
        private readonly ILogger<GetEffectiveAgentConfigFunction> _logger;
        private readonly AgentConfigResolver _resolver;

        public GetEffectiveAgentConfigFunction(
            ILogger<GetEffectiveAgentConfigFunction> logger,
            AgentConfigResolver resolver)
        {
            _logger = logger;
            _resolver = resolver;
        }

        [Function("GetEffectiveAgentConfig")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/effective-agent-config")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // GlobalAdminOnly + RouteParam scoping enforced by PolicyEnforcementMiddleware.
                var requestCtx = req.GetRequestContext();

                // Optional ?agentVersion= selects the hash-oracle line like the agent's
                // X-Agent-Version header would; absent → the current (V2) line, never the
                // legacy fallback an absent header means on the agent channel.
                var agentVersion = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["agentVersion"];
                var agentMajor = string.IsNullOrWhiteSpace(agentVersion)
                    ? AgentConfigResolver.CurrentAgentMajor
                    : AgentConfigResolver.ParseAgentMajor(agentVersion);

                var resolution = await _resolver.ResolveAsync(requestCtx.TargetTenantId, agentMajor);
                return await req.OkAsync(resolution.Response);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetEffectiveAgentConfig");
            }
        }
    }
}
