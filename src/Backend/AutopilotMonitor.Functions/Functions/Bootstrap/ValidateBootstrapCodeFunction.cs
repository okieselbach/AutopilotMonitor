using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// GET /api/bootstrap/validate/{code} — Validate a bootstrap code and return the token.
    /// Anonymous endpoint — called by the Next.js /go/{code} route (server-side).
    /// Rate-limited by IP to prevent brute-force enumeration.
    /// </summary>
    public class ValidateBootstrapCodeFunction
    {
        private readonly ILogger<ValidateBootstrapCodeFunction> _logger;
        private readonly BootstrapSessionService _bootstrapService;
        private readonly RateLimitService _rateLimitService;
        private readonly TenantConfigurationService _configService;

        public ValidateBootstrapCodeFunction(
            ILogger<ValidateBootstrapCodeFunction> logger,
            BootstrapSessionService bootstrapService,
            RateLimitService rateLimitService,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _rateLimitService = rateLimitService;
            _configService = configService;
        }

        [Function("ValidateBootstrapCode")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bootstrap/validate/{code}")] HttpRequestData req,
            string code)
        {
            try
            {
                // Rate limit by source IP (prevent brute-force enumeration of short codes).
                // Use the rightmost X-Forwarded-For entry — App Service appends the real
                // client/proxy hop there; leftmost values are attacker-controlled and would
                // let any caller rotate the rate-limit key for free.
                var clientIp = ClientIpExtractor.GetTrustedClientIp(req);

                var rateLimitKey = $"bootstrap-validate:{clientIp}";
                var rateLimitResult = _rateLimitService.CheckRateLimit(rateLimitKey, 20); // 20 req/min
                if (!rateLimitResult.IsAllowed)
                {
                    var tooMany = req.CreateResponse(HttpStatusCode.TooManyRequests);
                    if (rateLimitResult.RetryAfter.HasValue)
                        tooMany.Headers.Add("Retry-After", ((int)rateLimitResult.RetryAfter.Value.TotalSeconds).ToString());
                    await tooMany.WriteAsJsonAsync(new { success = false, message = "Rate limit exceeded. Try again later." });
                    return tooMany;
                }

                // Validate code format (6 chars, alphanumeric)
                if (string.IsNullOrEmpty(code) || code.Length < 4 || code.Length > 10)
                {
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteAsJsonAsync(new { success = false, message = "Invalid code format" });
                    return badReq;
                }

                var session = await _bootstrapService.ValidateCodeAsync(code);

                if (session == null)
                {
                    _logger.LogWarning("Bootstrap code validation failed for code {Code} from IP {ClientIp}", code, clientIp);
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { success = false, message = "Code not found, expired, or revoked" });
                    return notFound;
                }

                // Check if bootstrap token feature is enabled for the session's tenant
                var tenantConfig = await _configService.GetConfigurationAsync(session.TenantId);
                if (!tenantConfig.BootstrapTokenEnabled)
                {
                    _logger.LogWarning("Bootstrap code {Code} rejected — feature disabled for tenant {TenantId}", code, session.TenantId);
                    var disabled = req.CreateResponse(HttpStatusCode.NotFound);
                    await disabled.WriteAsJsonAsync(new { success = false, message = "Code not found, expired, or revoked" });
                    return disabled;
                }

                var responseData = new ValidateBootstrapCodeResponse
                {
                    Success = true,
                    TenantId = session.TenantId,
                    Token = session.Token,
                    // Download alias — the portal's bootstrapValidation.ts allowlists this
                    // host (plus the legacy blob host for transition); keep the two in sync.
                    AgentDownloadUrl = $"{Constants.AgentDownloadBaseUrl}/{Constants.AgentZipFileName}",
                    ExpiresAt = session.ExpiresAt,
                    Message = "Bootstrap code validated"
                };

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(responseData);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating bootstrap code {Code}", code);
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { success = false, message = "Validation failed" });
                return error;
            }
        }
    }
}
