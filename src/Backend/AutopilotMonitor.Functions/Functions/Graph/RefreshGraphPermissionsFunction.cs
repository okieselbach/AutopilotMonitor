using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Graph;

/// <summary>
/// <c>POST /api/tenants/{tenantId}/graph-permissions/refresh</c> — invalidates the
/// detector's cached token/roles for the tenant so the next call re-acquires fresh from
/// Azure AD. Triggered by the Admin UI right after the customer ran the grant script, to
/// avoid a 1h wait before the new permissions show up.
/// </summary>
public class RefreshGraphPermissionsFunction
{
    private readonly ILogger<RefreshGraphPermissionsFunction> _logger;
    private readonly IGraphFeatureDetector _detector;
    private readonly TelemetryClient _telemetry;

    public RefreshGraphPermissionsFunction(
        ILogger<RefreshGraphPermissionsFunction> logger,
        IGraphFeatureDetector detector,
        TelemetryClient telemetry)
    {
        _logger = logger;
        _detector = detector;
        _telemetry = telemetry;
    }

    [Function("RefreshGraphPermissions")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "tenants/{tenantId}/graph-permissions/refresh")] HttpRequestData req,
        string tenantId)
    {
        try
        {
            var requestCtx = req.GetRequestContext();
            _detector.InvalidateTenant(requestCtx.TargetTenantId);
            _logger.LogInformation("Graph-permissions cache invalidated for tenant {Tenant} by request",
                requestCtx.TargetTenantId);

            try
            {
                // High-signal event: admin just ran the grant script and asked us to pick
                // up the new appRoleAssignment. Pulses cluster around tenants newly opting in.
                _telemetry.TrackEvent("GraphAddOnRefreshTriggered", new Dictionary<string, string>
                {
                    ["TenantId"] = requestCtx.TargetTenantId,
                    ["UserId"] = requestCtx.UserPrincipalName,
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GraphAddOnRefreshTriggered telemetry emit failed");
            }

            return await req.OkAsync(new SuccessOnlyResponse { Success = true });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex,
                $"Refresh graph permissions for tenant '{tenantId}'");
        }
    }
}
