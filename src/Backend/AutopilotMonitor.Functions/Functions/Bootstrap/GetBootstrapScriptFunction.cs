using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// GET /api/bootstrap/go/{code} — the OOBE bootstrap script endpoint.
    /// Customer-facing as <c>irm https://go.autopilotmonitor.com/{code} | iex</c>
    /// (Front Door rewrites /{code} on the go domain to this route). Successor of the
    /// portal's Next.js /go/[code] route, moved here so the portal can become a static
    /// export; validation happens in-process (BootstrapSessionService), no HTTP hop.
    ///
    /// Contract: ALWAYS HTTP 200 with text/plain — error conditions return a small
    /// PowerShell script whose Write-Host surfaces the message, so irm | iex shows the
    /// error on the OOBE console instead of crashing on a non-2xx status.
    /// Cache-Control: no-store comes from NoStoreCacheMiddleware (the script embeds the
    /// bearer token); X-Content-Type-Options: nosniff from SecurityHeadersMiddleware.
    /// </summary>
    public class GetBootstrapScriptFunction
    {
        private readonly ILogger<GetBootstrapScriptFunction> _logger;
        private readonly BootstrapSessionService _bootstrapService;
        private readonly RateLimitService _rateLimitService;
        private readonly TenantConfigurationService _configService;

        // Same gate the portal route enforced: 4-10 alphanumeric chars, checked before
        // any service call. Stricter than the length-only check in
        // ValidateBootstrapCodeFunction on purpose.
        private static readonly Regex CodeFormatRegex = new(
            "^[a-z0-9]{4,10}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static bool IsValidCodeFormat(string? code) =>
            !string.IsNullOrEmpty(code) && CodeFormatRegex.IsMatch(code);

        public GetBootstrapScriptFunction(
            ILogger<GetBootstrapScriptFunction> logger,
            BootstrapSessionService bootstrapService,
            RateLimitService rateLimitService,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _rateLimitService = rateLimitService;
            _configService = configService;
        }

        [Function("GetBootstrapScript")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bootstrap/go/{code}")] HttpRequestData req,
            string code)
        {
            try
            {
                // Rate limit by source IP (prevent brute-force enumeration of short codes).
                // Own bucket, deliberately not shared with bootstrap-validate. On breach the
                // response is still a 200 error script (irm | iex must surface the message);
                // Retry-After is informational for non-iex clients.
                var clientIp = ClientIpExtractor.GetTrustedClientIp(req);
                var rateLimitResult = _rateLimitService.CheckRateLimit($"bootstrap-script:{clientIp}", 20); // 20 req/min
                if (!rateLimitResult.IsAllowed)
                {
                    _logger.LogWarning("Bootstrap script rate limit hit from IP {ClientIp}", clientIp);
                    var limited = await ScriptResponseAsync(
                        req, OobeBootstrapScriptGenerator.GenerateErrorScript("Rate limit exceeded. Try again later."));
                    if (rateLimitResult.RetryAfter.HasValue)
                        limited.Headers.Add("Retry-After", ((int)rateLimitResult.RetryAfter.Value.TotalSeconds).ToString());
                    return limited;
                }

                if (!IsValidCodeFormat(code))
                {
                    return await ScriptResponseAsync(
                        req, OobeBootstrapScriptGenerator.GenerateErrorScript("Invalid bootstrap code format."));
                }

                var session = await _bootstrapService.ValidateCodeAsync(code);
                if (session == null)
                {
                    _logger.LogWarning("Bootstrap script request failed for code {Code} from IP {ClientIp}", code, clientIp);
                    return await ScriptResponseAsync(
                        req, OobeBootstrapScriptGenerator.GenerateErrorScript("Bootstrap code not found, expired, or revoked."));
                }

                // Feature gate — same generic message as not-found so a disabled tenant is
                // indistinguishable from an unknown code (no enumeration oracle).
                var tenantConfig = await _configService.GetConfigurationAsync(session.TenantId);
                if (!tenantConfig.BootstrapTokenEnabled)
                {
                    _logger.LogWarning("Bootstrap script for code {Code} rejected — feature disabled for tenant {TenantId}", code, session.TenantId);
                    return await ScriptResponseAsync(
                        req, OobeBootstrapScriptGenerator.GenerateErrorScript("Bootstrap code not found, expired, or revoked."));
                }

                var agentDownloadUrl = $"{Constants.AgentDownloadBaseUrl}/{Constants.AgentZipFileName}";
                var expiresAtUtc = session.ExpiresAt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc)
                    : session.ExpiresAt.ToUniversalTime();

                if (!BootstrapScriptValueValidator.TryValidate(
                        session.TenantId, session.Token, agentDownloadUrl, expiresAtUtc,
                        out var values, out var reason))
                {
                    // Log the failure category only — never the offending value, to keep
                    // potential attack payloads out of Application Insights.
                    _logger.LogWarning("Bootstrap script value validation failed for code {Code}: {Reason}", code, reason);
                    return await ScriptResponseAsync(
                        req, OobeBootstrapScriptGenerator.GenerateErrorScript("Bootstrap response failed validation. Contact support."));
                }

                var script = OobeBootstrapScriptGenerator.GenerateSuccessScript(values, DateTime.UtcNow);
                return await ScriptResponseAsync(req, script);
            }
            catch (Exception ex)
            {
                // Never inline exception detail into the script — it would execute on the
                // OOBE console and could leak internals.
                _logger.LogError(ex, "Error generating bootstrap script for code {Code}", code);
                return await ScriptResponseAsync(
                    req, OobeBootstrapScriptGenerator.GenerateErrorScript("Failed to generate bootstrap script. Please try again."));
            }
        }

        private static async Task<HttpResponseData> ScriptResponseAsync(HttpRequestData req, string script)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            await response.WriteStringAsync(script);
            return response;
        }
    }
}
