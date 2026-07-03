using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Infrastructure
{
    public class SignalRNegotiateFunction
    {
        private readonly ILogger<SignalRNegotiateFunction> _logger;
        private readonly ISignalRNotificationService _signalRService;

        public SignalRNegotiateFunction(
            ILogger<SignalRNegotiateFunction> logger,
            ISignalRNotificationService signalRService)
        {
            _logger = logger;
            _signalRService = signalRService;
        }

        [Function("negotiate")]
        public async Task<HttpResponseData> Negotiate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/negotiate")] HttpRequestData req)
        {
            _logger.LogInformation("SignalR negotiate request");

            // Validate authentication (middleware already validated JWT)
            // Azure Functions Isolated Worker: Check FunctionContext.Items first
            bool isAuthenticated = false;

            if (req.FunctionContext.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
                && principalObj is System.Security.Claims.ClaimsPrincipal principal)
            {
                isAuthenticated = principal.Identity?.IsAuthenticated == true;
            }
            else
            {
                // Fallback to HTTP context
                var httpContext = req.FunctionContext.GetHttpContext();
                isAuthenticated = httpContext?.User?.Identity?.IsAuthenticated == true;
            }

            if (!isAuthenticated)
            {
                _logger.LogWarning("Unauthenticated SignalR negotiate attempt");
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorizedResponse;
            }

            // Negotiate via the Management SDK (not the SignalRConnectionInfoInput binding) so the
            // client access token is bound to a SignalR USER ID = the lowercased UPN. That binding is
            // what makes revocation enforceable: when a delegated grant / tenant-group assignment is
            // revoked, the revoke path calls DisconnectUserAsync(upn) to cut already-joined
            // live streams — anonymous connections (no user id) cannot be targeted. The UPN comes from
            // the middleware-validated JWT, never from client-controlled binding data.
            var userEmail = TenantHelper.GetUserIdentifier(req);
            var negotiation = await _signalRService.NegotiateClientAsync(userEmail.ToLowerInvariant());
            if (negotiation == null)
            {
                var unavailable = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                await unavailable.WriteAsJsonAsync(new { success = false, message = "SignalR negotiation unavailable" });
                return unavailable;
            }

            _logger.LogInformation($"SignalR connection negotiated for user: {userEmail}");

            var response = req.CreateResponse(HttpStatusCode.OK);
            // Exactly the shape the @microsoft/signalr client's negotiate protocol expects.
            await response.WriteAsJsonAsync(new { url = negotiation.Value.Url, accessToken = negotiation.Value.AccessToken });

            return response;
        }
    }
}
