using System.Net;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    /// <summary>
    /// GET /api/global/raw/access-probe — a GlobalAdminOnly route that does nothing.
    /// <para>
    /// It exists for the deny path, not the allow path. When a caller without the GlobalAdmin role
    /// asks the MCP for a GA-only tool, the tool is not registered for them and the SDK answers
    /// "not found" before any backend call — so the backend, which owns the identity binding and the
    /// <c>PrivilegedRouteDenied</c> ops event, would never learn about the probe. The MCP therefore
    /// fires this route (fire-and-forget, <c>X-MCP-Tool-Name</c> = the attempted tool) and the policy
    /// middleware refuses it exactly like any other GlobalAdminOnly route: same catalog, same identity
    /// check, same throttled Critical event, no second writer and no caller-supplied event payload.
    /// A genuine Global Admin reaching it simply gets a typed OK.
    /// </para>
    /// </summary>
    public class AccessProbeFunction
    {
        [Function("GlobalAccessProbe")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/access-probe")] HttpRequestData req,
            FunctionContext context)
        {
            if (await RawGlobalAdminGate.DenyUnlessGlobalAdminAsync(req, context) is { } denied)
                return denied;

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new AccessProbeResponse { Success = true, Role = Constants.GlobalRoles.GlobalAdmin });
            return response;
        }
    }
}
