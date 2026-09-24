using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Measures and — behind <c>AdminConfiguration.EnforceClientAppBinding</c> — enforces the client-app binding
/// of delegated tokens (<see cref="ClientAppBinding"/>). Runs after <see cref="AuthenticationMiddleware"/>
/// (it reads the validated principal) and before <see cref="PolicyEnforcementMiddleware"/> (the cap marker
/// must exist before any role resolves). Tokens from the platform's own registrations cost one set lookup.
/// </summary>
public class ClientAppBindingMiddleware : IFunctionsWorkerMiddleware
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromHours(1);
    private const int MaxLogKeys = 1000;

    // Worker traces reach App Insights at Warning+ only; the request dimensions carry the per-request
    // measurement, this line makes a new (tenant, client, outcome) visible without flooding.
    private static readonly ConcurrentDictionary<string, DateTime> LastLogged = new();

    private readonly ILogger<ClientAppBindingMiddleware> _logger;
    private readonly IConfiguration _configuration;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly AdminConfigurationService _adminConfigService;

    public ClientAppBindingMiddleware(
        ILogger<ClientAppBindingMiddleware> logger,
        IConfiguration configuration,
        TenantAdminsService tenantAdminsService,
        AdminConfigurationService adminConfigService)
    {
        _logger = logger;
        _configuration = configuration;
        _tenantAdminsService = tenantAdminsService;
        _adminConfigService = adminConfigService;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Only a principal the authentication step validated — never the empty httpContext.User of an
        // anonymous or device route, which names no client and would read as "unidentified".
        if (!context.Items.TryGetValue("ClaimsPrincipal", out var principalObj) || principalObj is not ClaimsPrincipal principal)
        {
            await next(context);
            return;
        }

        var trustedClientIds = ResolveTrustedClientApps(_configuration);
        if (!ClientAppBinding.IsForeignDelegatedToken(principal, trustedClientIds, out var clientAppId))
        {
            await next(context);
            return;
        }

        var tenantId = principal.GetTenantId();
        var registered = false;
        if (clientAppId != null && !string.IsNullOrWhiteSpace(tenantId))
        {
            var (state, _) = await _tenantAdminsService.GetTableMembershipAsync(
                tenantId, Constants.PrincipalKeys.ForApplication(clientAppId));
            registered = state == TableMemberState.Enabled;
        }

        var outcome = clientAppId == null
            ? ClientAppBinding.Outcomes.Unidentified
            : registered ? ClientAppBinding.Outcomes.Registered : ClientAppBinding.Outcomes.Unregistered;
        context.Items[ClientAppBinding.OutcomeItemKey] = outcome;
        if (clientAppId != null)
            context.Items[ClientAppBinding.ClientAppIdItemKey] = clientAppId;

        var enforced = (await _adminConfigService.GetConfigurationAsync()).EnforceClientAppBinding;
        LogOncePerInterval(tenantId, clientAppId, outcome, enforced);

        if (enforced)
        {
            if (!registered)
            {
                var httpContext = context.GetHttpContext();
                if (httpContext != null)
                {
                    await ApiErrorWriter.WriteAsync(
                        httpContext, context.GetCorrelationId(), HttpStatusCode.Forbidden,
                        Constants.ApiErrorCodes.ClientAppNotRegistered,
                        ClientAppBinding.RefusalMessage(clientAppId));
                }
                return;
            }

            ClientAppBinding.MarkCapped(principal, clientAppId!);
        }

        await next(context);
    }

    /// <summary>
    /// The client applications whose delegated tokens are never foreign: the audience trust set (primary,
    /// legacy, additional app registrations) plus <c>EntraId:TrustedClientAppIds</c> — clients of the platform's
    /// own that obtain tokens for the API app without being an audience themselves (the local development
    /// registration). The extra setting widens only this set, never the accepted token audiences.
    /// </summary>
    internal static IReadOnlyCollection<string> ResolveTrustedClientApps(IConfiguration configuration)
    {
        var audienceTrust = AuthenticationMiddleware.ResolveTrustedClientIds(configuration, out _, out _);
        var clientOnly = AuthenticationMiddleware.ResolveConfiguredClientIds(
            null, configuration["EntraId:TrustedClientAppIds"], out _);
        return audienceTrust.Concat(clientOnly).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void LogOncePerInterval(string? tenantId, string? clientAppId, string outcome, bool enforced)
    {
        var key = $"{tenantId}|{clientAppId}|{outcome}|{enforced}";
        var now = DateTime.UtcNow;
        if (LastLogged.TryGetValue(key, out var last) && now - last < LogInterval)
            return;
        if (LastLogged.Count >= MaxLogKeys)
            LastLogged.Clear();
        LastLogged[key] = now;

        _logger.LogWarning(
            "[ClientAppBinding] Delegated token from client app {ClientAppId} (tid={TenantId}): {Outcome}, enforced={Enforced}",
            LogSanitizer.Clean(clientAppId ?? "(none)"), LogSanitizer.Clean(tenantId ?? "(none)"), outcome, enforced);
    }
}
