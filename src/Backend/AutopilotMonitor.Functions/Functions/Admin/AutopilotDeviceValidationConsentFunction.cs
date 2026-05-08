using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

public class AutopilotDeviceValidationConsentFunction
{
    private readonly ILogger<AutopilotDeviceValidationConsentFunction> _logger;
    private readonly IConfiguration _configuration;
    private readonly GraphTokenService _graphTokenService;
    private readonly TelemetryClient _telemetryClient;
    private readonly OpsEventService _opsEventService;

    /// <summary>
    /// Known redirect URIs registered in the Entra ID app registration for the admin consent flow.
    /// If the frontend sends a redirect URI not in this list, the consent will fail at Azure AD
    /// with AADSTS50011 and the user will be stuck — so we log a critical warning.
    /// </summary>
    internal static readonly HashSet<string> RegisteredConsentRedirectPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/settings/tenant/autopilot",
        "/settings",
    };

    public AutopilotDeviceValidationConsentFunction(
        ILogger<AutopilotDeviceValidationConsentFunction> logger,
        IConfiguration configuration,
        GraphTokenService graphTokenService,
        TelemetryClient telemetryClient,
        OpsEventService opsEventService)
    {
        _logger = logger;
        _configuration = configuration;
        _graphTokenService = graphTokenService;
        _telemetryClient = telemetryClient;
        _opsEventService = opsEventService;
    }

    [Function("GetAutopilotDeviceValidationConsentUrl")]
    public async Task<HttpResponseData> GetConsentUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/autopilot-device-validation/consent-url")] HttpRequestData req,
        string tenantId)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = req.GetRequestContext();

        var validatorClientId = _configuration["EntraId:ClientId"];
        if (string.IsNullOrWhiteSpace(validatorClientId))
        {
            var badConfig = req.CreateResponse(HttpStatusCode.InternalServerError);
            await badConfig.WriteAsJsonAsync(new
            {
                error = "Validator app client ID is not configured on the backend."
            });
            return badConfig;
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var redirectUri = query["redirectUri"];
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            redirectUri = $"{req.Url.Scheme}://{req.Url.Authority}/settings";
        }

        // Validate redirect URI path against known registered paths
        var redirectPath = new Uri(redirectUri).AbsolutePath;
        if (!RegisteredConsentRedirectPaths.Contains(redirectPath))
        {
            _logger.LogCritical(
                "ConsentRedirectUriMismatch: Redirect URI path '{RedirectPath}' (full: '{RedirectUri}') is NOT in RegisteredConsentRedirectPaths for tenant {TenantId}. " +
                "Azure AD will reject this with AADSTS50011 — consent flow is BROKEN. " +
                "Update RegisteredConsentRedirectPaths or the Entra ID app registration.",
                redirectPath, redirectUri, requestCtx.TargetTenantId);

            _telemetryClient.TrackEvent("ConsentRedirectUriMismatch", new Dictionary<string, string>
            {
                ["TenantId"]    = requestCtx.TargetTenantId,
                ["UserId"]      = requestCtx.UserPrincipalName,
                ["RedirectUri"] = redirectUri,
                ["RedirectPath"] = redirectPath,
            });

            await _opsEventService.RecordConsentRedirectUriMismatchAsync(
                requestCtx.TargetTenantId, requestCtx.UserPrincipalName, redirectUri, redirectPath);
        }

        var consentUrl =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(requestCtx.TargetTenantId)}/adminconsent" +
            $"?client_id={Uri.EscapeDataString(validatorClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString("autopilot-device-validation-enable")}";

        _logger.LogInformation(
            "ConsentFlowStarted: tenant={TenantId} user={UserId} redirectUri={RedirectUri}",
            requestCtx.TargetTenantId, requestCtx.UserPrincipalName, redirectUri);

        _telemetryClient.TrackEvent("ConsentFlowStarted", new Dictionary<string, string>
        {
            ["TenantId"]    = requestCtx.TargetTenantId,
            ["UserId"]      = requestCtx.UserPrincipalName,
            ["RedirectUri"] = redirectUri,
        });

        await _opsEventService.RecordConsentFlowStartedAsync(
            requestCtx.TargetTenantId, requestCtx.UserPrincipalName, redirectUri);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            consentUrl
        });
        return response;
    }

    [Function("GetAutopilotDeviceValidationConsentStatus")]
    public async Task<HttpResponseData> GetConsentStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/autopilot-device-validation/consent-status")] HttpRequestData req,
        string tenantId)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = req.GetRequestContext();

        var result = await _graphTokenService.GetConsentStatusAsync(requestCtx.TargetTenantId);

        _telemetryClient.TrackEvent("ConsentStatusChecked", new Dictionary<string, string>
        {
            ["TenantId"]    = requestCtx.TargetTenantId,
            ["UserId"]      = requestCtx.UserPrincipalName,
            ["IsConsented"] = result.IsConsented.ToString(),
            ["IsTransient"] = result.IsTransient.ToString(),
        });

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            isConsented = result.IsConsented,
            message = result.Message
        });
        return response;
    }

    /// <summary>
    /// Known consent triggers — frontend flows that initiate the AAD admin-consent dialog.
    /// "device-preparation" is reserved for a future DevPrep-specific consent flow; it is
    /// accepted by the success endpoint so the value can land in ops events when wired up.
    /// </summary>
    internal static readonly HashSet<string> KnownConsentTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "autopilot",
        "corporate",
        "device-preparation",
    };

    /// <summary>
    /// Frontend confirms a consent flow succeeded — called from the post-redirect callback
    /// after <c>consent-status</c> reports <c>isConsented=true</c>. Pairs with
    /// <c>ConsentFlowStarted</c> / <c>ConsentFlowFailed</c> so ops can see whether
    /// repeated failures eventually resolved into a success.
    /// </summary>
    [Function("ReportConsentSuccess")]
    public async Task<HttpResponseData> ReportConsentSuccess(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "config/{tenantId}/autopilot-device-validation/consent-success")] HttpRequestData req,
        string tenantId)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = req.GetRequestContext();

        ConsentSuccessReport? report;
        try
        {
            report = await req.ReadFromJsonAsync<ConsentSuccessReport>();
        }
        catch
        {
            report = null;
        }

        var rawTrigger = report?.Trigger ?? string.Empty;
        var trigger = KnownConsentTriggers.Contains(rawTrigger) ? rawTrigger.ToLowerInvariant() : "unknown";

        _logger.LogInformation(
            "ConsentFlowSuccess: tenant={TenantId} user={UserId} trigger={Trigger}",
            requestCtx.TargetTenantId, requestCtx.UserPrincipalName, trigger);

        _telemetryClient.TrackEvent("ConsentFlowSuccess", new Dictionary<string, string>
        {
            ["TenantId"] = requestCtx.TargetTenantId,
            ["UserId"]   = requestCtx.UserPrincipalName,
            ["Trigger"]  = trigger,
        });

        await _opsEventService.RecordConsentFlowSuccessAsync(
            requestCtx.TargetTenantId, requestCtx.UserPrincipalName, trigger);

        return req.CreateResponse(HttpStatusCode.OK);
    }

    private sealed class ConsentSuccessReport
    {
        public string? Trigger { get; set; }
    }

    /// <summary>
    /// Frontend reports consent flow failures (Azure AD errors) so we have visibility
    /// into broken consent flows that never reach our callback.
    /// KQL: customEvents | where name == "ConsentFlowFailed"
    /// </summary>
    [Function("ReportConsentFailure")]
    public async Task<HttpResponseData> ReportConsentFailure(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "config/{tenantId}/autopilot-device-validation/consent-failure")] HttpRequestData req,
        string tenantId)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = req.GetRequestContext();

        ConsentFailureReport? report;
        try
        {
            report = await req.ReadFromJsonAsync<ConsentFailureReport>();
        }
        catch
        {
            report = null;
        }

        var errorCode = report?.Error ?? "unknown";
        var errorDescription = report?.ErrorDescription ?? string.Empty;

        _logger.LogError(
            "ConsentFlowFailed: tenant={TenantId} user={UserId} error={ErrorCode} description={ErrorDescription}",
            requestCtx.TargetTenantId, requestCtx.UserPrincipalName, errorCode, errorDescription);

        _telemetryClient.TrackEvent("ConsentFlowFailed", new Dictionary<string, string>
        {
            ["TenantId"]         = requestCtx.TargetTenantId,
            ["UserId"]           = requestCtx.UserPrincipalName,
            ["Error"]            = errorCode,
            ["ErrorDescription"] = errorDescription.Length > 500 ? errorDescription[..500] : errorDescription,
        });

        await _opsEventService.RecordConsentFlowFailedAsync(
            requestCtx.TargetTenantId, requestCtx.UserPrincipalName, errorCode, errorDescription);

        return req.CreateResponse(HttpStatusCode.OK);
    }

    private sealed class ConsentFailureReport
    {
        public string? Error { get; set; }
        public string? ErrorDescription { get; set; }
    }
}
