using AutopilotMonitor.Shared;
using System.Linq;
using System.Net;
using System.Threading;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

public class AutopilotDeviceValidationConsentFunction
{
    /// <summary>
    /// Hard wall on the access-check probe's token acquire — the admin click must feel
    /// responsive and never get stuck in <see cref="GraphTokenService"/>'s long
    /// consent-propagation retry chain (5 + 15 + 30 s). Mirrors
    /// <c>GetGraphPermissionsStatusFunction.StatusBudget</c>.
    /// </summary>
    internal static readonly TimeSpan AccessCheckBudget = TimeSpan.FromSeconds(4);

    private readonly ILogger<AutopilotDeviceValidationConsentFunction> _logger;
    private readonly IConfiguration _configuration;
    private readonly GraphTokenService _graphTokenService;
    private readonly IGraphFeatureDetector _graphFeatureDetector;
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
        IGraphFeatureDetector graphFeatureDetector,
        TelemetryClient telemetryClient,
        OpsEventService opsEventService)
    {
        _logger = logger;
        _configuration = configuration;
        _graphTokenService = graphTokenService;
        _graphFeatureDetector = graphFeatureDetector;
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
            $"{Constants.EntraLoginBaseUrl}/{Uri.EscapeDataString(requestCtx.TargetTenantId)}/adminconsent" +
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
    /// <c>GET /api/config/{tenantId}/autopilot-device-validation/access-check</c> — probes whether
    /// the Autopilot Monitor service principal already holds the core validation permission
    /// (<see cref="GraphAppPermissions.DeviceManagementServiceConfigReadAll"/>) in this tenant,
    /// regardless of who granted consent.
    /// <para>
    /// This is the "rights-less admin" escape hatch: in larger tenants the multi-tenant app is
    /// often pre-approved by someone with consent rights, so the app + permission exist even though
    /// the admin operating our UI cannot complete the <c>/adminconsent</c> redirect. When this probe
    /// reports <c>accessPresent: true</c>, the frontend silently persists
    /// <c>ValidateAutopilotDevice</c>/<c>ValidateCorporateIdentifier</c> via the normal config PUT —
    /// opening both the UI badge AND the agent hard gate — without ever running the redirect.
    /// </para>
    /// <para>
    /// Read-only: this endpoint never persists. It checks the SP token's <c>roles</c> claim (via
    /// <see cref="IGraphFeatureDetector"/>) rather than mere token-acquirability, so we never flip
    /// the gate "enabled" when the SP exists but the role was not actually consented (which would
    /// otherwise leave the agent stuck in an endless 503 at the real Graph call).
    /// </para>
    /// </summary>
    [Function("GetAutopilotDeviceValidationAccessCheck")]
    public async Task<HttpResponseData> GetAccessCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/autopilot-device-validation/access-check")] HttpRequestData req,
        string tenantId)
    {
        // Authentication + tenant-admin policy enforced by PolicyEnforcementMiddleware
        var requestCtx = req.GetRequestContext();

        // Always read fresh. This probe is admin-triggered (reconcile on consent-fail, the
        // "detect existing access" button, post-consent verification) and low-frequency, so a
        // stale cached snapshot must never decide it. In particular, a snapshot acquired during
        // AAD consent propagation can carry an empty roles claim and would otherwise be cached for
        // the token lifetime (~55 min) — wedging the admin out long after consent has propagated.
        // Invalidating here makes every click re-acquire from AAD, so a retry always reflects reality.
        _graphFeatureDetector.InvalidateTenant(requestCtx.TargetTenantId);

        bool isTransient;
        bool accessPresent;
        using (var budgetCts = new CancellationTokenSource(AccessCheckBudget))
        {
            GraphPermissionSnapshot snapshot;
            try
            {
                snapshot = await _graphFeatureDetector.GetSnapshotAsync(requestCtx.TargetTenantId, budgetCts.Token);
            }
            catch (OperationCanceledException)
            {
                snapshot = new GraphPermissionSnapshot { IsTransient = true };
            }

            isTransient = snapshot.IsTransient;
            accessPresent = ResolveAccessPresent(snapshot);
        }

        _telemetryClient.TrackEvent("ConsentAccessDetected", new Dictionary<string, string>
        {
            ["TenantId"]      = requestCtx.TargetTenantId,
            ["UserId"]        = requestCtx.UserPrincipalName,
            ["AccessPresent"] = accessPresent.ToString(),
            ["IsTransient"]   = isTransient.ToString(),
        });

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            accessPresent,
            isTransient,
            requiredPermission = GraphAppPermissions.DeviceManagementServiceConfigReadAll,
        });
        return response;
    }

    /// <summary>
    /// Pure decision for the access-check probe: does the SP snapshot prove the core validation
    /// role is effectively granted? Extracted so the reconcile decision is unit testable without
    /// mocking the HTTP pipeline.
    /// <para>
    /// A transient snapshot is NEVER "present" — we must not flip the agent gate to enabled on an
    /// inconclusive probe (timeout / Graph 5xx), or the persisted bool could open the gate while the
    /// role is actually absent, leaving the agent stuck in an endless 503 at the real Graph call.
    /// Role comparison is case-insensitive: Azure AD may issue the role with slightly different
    /// casing across tokens.
    /// </para>
    /// </summary>
    internal static bool ResolveAccessPresent(GraphPermissionSnapshot snapshot)
    {
        if (snapshot == null || snapshot.IsTransient)
        {
            return false;
        }

        return snapshot.GrantedRoles.Any(r => string.Equals(
            r, GraphAppPermissions.DeviceManagementServiceConfigReadAll, StringComparison.OrdinalIgnoreCase));
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
