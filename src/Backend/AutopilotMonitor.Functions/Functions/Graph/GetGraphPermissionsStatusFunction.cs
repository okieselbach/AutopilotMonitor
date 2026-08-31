using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Graph;

/// <summary>
/// <c>GET /api/tenants/{tenantId}/graph-permissions/status</c> — drives the "Optional Graph
/// capabilities" section of the Admin Settings UI. Returns the currently-granted Microsoft
/// Graph roles on the Autopilot Monitor service principal in this tenant plus a per-feature
/// "granted/not-granted" verdict computed against <see cref="GraphFeatureCatalog"/>.
/// <para>
/// Also returns the AutopilotMonitor app's ClientId so the UI can render a copy-paste-ready
/// PS one-liner for the grant script without having to hard-code or surface configuration on
/// the client side.
/// </para>
/// <para>
/// The endpoint applies its own short token-acquire budget (<see cref="StatusBudget"/>) so the
/// admin click cannot get stuck in <see cref="Security.GraphTokenService"/>'s long retry chain
/// (5 + 15 + 30 s on consent-propagation paths). On budget exhaustion or any transient failure
/// the payload sets <c>isTransient: true</c> and the UI renders "try again" rather than a
/// misleading "0 of 0 granted" verdict.
/// </para>
/// </summary>
public class GetGraphPermissionsStatusFunction
{
    /// <summary>Hard wall on the token acquire used for status — admin clicks must feel responsive.</summary>
    internal static readonly TimeSpan StatusBudget = TimeSpan.FromSeconds(4);

    private readonly ILogger<GetGraphPermissionsStatusFunction> _logger;
    private readonly IGraphFeatureDetector _detector;
    private readonly IConfiguration _configuration;
    private readonly TelemetryClient _telemetry;
    private readonly Security.EntraAppRegistry _appRegistry;
    private readonly Services.TenantConfigurationService _tenantConfigService;

    public GetGraphPermissionsStatusFunction(
        ILogger<GetGraphPermissionsStatusFunction> logger,
        IGraphFeatureDetector detector,
        IConfiguration configuration,
        TelemetryClient telemetry,
        Security.EntraAppRegistry appRegistry,
        Services.TenantConfigurationService tenantConfigService)
    {
        _logger = logger;
        _detector = detector;
        _configuration = configuration;
        _telemetry = telemetry;
        _appRegistry = appRegistry;
        _tenantConfigService = tenantConfigService;
    }

    [Function("GetGraphPermissionsStatus")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "tenants/{tenantId}/graph-permissions/status")] HttpRequestData req,
        string tenantId)
    {
        try
        {
            var requestCtx = req.GetRequestContext();

            using var budgetCts = new CancellationTokenSource(StatusBudget);
            GraphPermissionSnapshot snapshot;
            try
            {
                snapshot = await _detector.GetSnapshotAsync(requestCtx.TargetTenantId, budgetCts.Token);
            }
            catch (OperationCanceledException)
            {
                snapshot = new GraphPermissionSnapshot { IsTransient = true };
            }

            var features = GraphFeatureCatalog.Features.Select(featureName => new GraphFeatureStatusItem
            {
                Name = featureName,
                Granted = snapshot.IsTransient
                    ? (bool?)null
                    : GraphFeatureCatalog.IsFeatureGranted(featureName, snapshot.GrantedRoles),
                RequiredPermissions = GraphFeatureCatalog.RequiredPermissions(featureName),
            }).ToList();

            EmitStatusChecked(requestCtx.TargetTenantId, requestCtx.UserPrincipalName, snapshot);

            // Dual app-reg window: the surfaced client id pre-fills the customer's grant-script
            // one-liner, so it must be the app that actually acts for this tenant (its homed app).
            var tenantConfig = await _tenantConfigService.GetConfigurationIfExistsAsync(requestCtx.TargetTenantId);
            var homedClientId = _appRegistry.ResolveForTenant(tenantConfig).ClientId;

            return await req.OkAsync(new GetGraphPermissionsStatusResponse
            {
                ClientId = homedClientId ?? string.Empty,
                IsTransient = snapshot.IsTransient,
                GrantedRoles = snapshot.GrantedRoles.ToArray(),
                Features = features,
            });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex,
                $"Get graph-permissions status for tenant '{tenantId}'");
        }
    }

    private void EmitStatusChecked(string tenantId, string userPrincipalName, GraphPermissionSnapshot snapshot)
    {
        try
        {
            _telemetry.TrackEvent("GraphAddOnStatusChecked", new Dictionary<string, string>
            {
                ["TenantId"] = tenantId,
                ["UserId"] = userPrincipalName,
                ["IsTransient"] = snapshot.IsTransient.ToString(CultureInfo.InvariantCulture),
                ["ScriptDisplayNamesGranted"] = GraphFeatureCatalog
                    .IsFeatureGranted(GraphFeatureCatalog.FeatureScriptDisplayNames, snapshot.GrantedRoles)
                    .ToString(CultureInfo.InvariantCulture),
                ["W365CloudPcValidationGranted"] = GraphFeatureCatalog
                    .IsFeatureGranted(GraphFeatureCatalog.FeatureW365CloudPcValidation, snapshot.GrantedRoles)
                    .ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GraphAddOnStatusChecked telemetry emit failed");
        }
    }
}
