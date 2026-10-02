using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Progress;

/// <summary>
/// Tells the Progress Portal whether the caller's own organization looks unused. A member without a
/// role only ever sees the portal; without this hint they never learn that their organization already
/// runs the product, or that nobody looks after it anymore. Reveals no session data: the JWT tenant
/// only, a flag and the signup date.
/// </summary>
public class ProgressTenantStatusFunction
{
    /// <summary>A younger organization is still being set up, not abandoned.</summary>
    internal static readonly TimeSpan UnusedAfter = TimeSpan.FromDays(14);

    private readonly ILogger<ProgressTenantStatusFunction> _logger;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly ISessionRepository _sessionRepo;

    public ProgressTenantStatusFunction(
        ILogger<ProgressTenantStatusFunction> logger,
        TenantConfigurationService tenantConfigService,
        ISessionRepository sessionRepo)
    {
        _logger = logger;
        _tenantConfigService = tenantConfigService;
        _sessionRepo = sessionRepo;
    }

    /// <summary>GET /api/progress/tenant-status — the caller's own tenant (catalog: TenantScoping.Jwt).</summary>
    [Function("ProgressGetTenantStatus")]
    public async Task<HttpResponseData> GetTenantStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "progress/tenant-status")] HttpRequestData req)
    {
        try
        {
            var tenantId = req.GetRequestContext().TenantId;

            // Both reads are strict: a storage failure must surface as 500, never as "unused".
            var configTask = _tenantConfigService.GetConfigurationIfExistsAsync(tenantId);
            var hasSessionTask = _sessionRepo.HasAnySessionStrictAsync(tenantId);
            await Task.WhenAll(configTask, hasSessionTask);

            return await req.OkAsync(Evaluate(configTask.Result, hasSessionTask.Result, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "ProgressGetTenantStatus");
        }
    }

    internal static ProgressTenantStatusResponse Evaluate(TenantConfiguration? config, bool hasAnySession, DateTime utcNow)
    {
        // No configuration row: not an onboarded organization, nothing to call unused.
        if (config == null)
            return new ProgressTenantStatusResponse { Success = true };

        // A row from before OnboardedAt existed is old by definition.
        var signedUpAt = config.OnboardedAt;
        var settled = signedUpAt == null || utcNow - signedUpAt.Value >= UnusedAfter;

        return new ProgressTenantStatusResponse
        {
            Success = true,
            Unused = !hasAnySession && settled,
            SignedUpAt = signedUpAt
        };
    }
}
