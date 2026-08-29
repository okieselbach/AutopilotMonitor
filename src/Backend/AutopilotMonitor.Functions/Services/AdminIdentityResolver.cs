using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Resolves the Entra identity (home tenant id + object id) behind a UPN at grant time WITHOUT operator
/// input, so granting a cross-tenant role is "type the UPN" and the identity binding is self-maintaining:
/// <list type="number">
/// <item><b>Sign-in history</b> (<see cref="Constants.TableNames.UserActivity"/>): the person has signed in
/// before ⇒ tid + oid are known and verified (both came from validated tokens). Ambiguous history (the same
/// UPN string seen under more than one tenant) is NOT auto-resolved — that is the very situation the binding
/// exists for, and only the operator can say which one is meant.</item>
/// <item><b>UPN domain → onboarded tenant</b>: the person has never signed in, but their UPN domain is the
/// domain of an onboarded tenant ⇒ tid is known; the oid is pinned on their first sign-in from that tenant.</item>
/// </list>
/// Neither ⇒ null; the caller asks the operator to pick the home tenant (the only manual step left, and only
/// for a UPN whose domain the platform has never seen).
/// </summary>
public class AdminIdentityResolver
{
    private readonly IMetricsRepository _metricsRepo;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly ILogger<AdminIdentityResolver> _logger;

    public AdminIdentityResolver(
        IMetricsRepository metricsRepo,
        TenantConfigurationService tenantConfigService,
        ILogger<AdminIdentityResolver> logger)
    {
        _metricsRepo = metricsRepo;
        _tenantConfigService = tenantConfigService;
        _logger = logger;
    }

    public virtual async Task<ResolvedAdminIdentity?> ResolveAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        var signIns = await _metricsRepo.GetSignInIdentitiesByUpnAsync(upn);
        var tenants = signIns.Select(s => s.TenantId.ToLowerInvariant()).Distinct().ToList();
        if (tenants.Count == 1)
        {
            // Most recent sign-in wins for the oid (a re-created account in the same tenant shows up as a
            // newer oid; the older one would be refused at sign-in anyway).
            var latest = signIns.OrderByDescending(s => s.LastLoginAt).First();
            return new ResolvedAdminIdentity(tenants[0],
                string.IsNullOrWhiteSpace(latest.ObjectId) ? null : latest.ObjectId.ToLowerInvariant(),
                ResolvedAdminIdentity.SourceSignIn);
        }
        if (tenants.Count > 1)
        {
            _logger.LogWarning("[IdentityBinding] {Upn} has sign-ins from {Count} different tenants — not auto-resolving the home tenant",
                upn, tenants.Count);
            return null;
        }

        var at = upn.LastIndexOf('@');
        if (at <= 0 || at == upn.Length - 1)
            return null;
        var domain = upn[(at + 1)..];

        var configs = await _tenantConfigService.GetAllConfigurationsAsync();
        var byDomain = configs
            .Where(c => !string.IsNullOrWhiteSpace(c.DomainName)
                        && string.Equals(c.DomainName.Trim(), domain, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.TenantId.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (byDomain.Count == 1)
            return new ResolvedAdminIdentity(byDomain[0], null, ResolvedAdminIdentity.SourceDomain);

        return null;
    }
}

/// <summary>
/// Outcome of <see cref="AdminIdentityResolver.ResolveAsync"/>: the home tenant, the object id when the
/// person has signed in before (null ⇒ pinned on first sign-in), and where the answer came from.
/// </summary>
public sealed record ResolvedAdminIdentity(string TenantId, string? ObjectId, string Source)
{
    public const string SourceSignIn = "sign-in";
    public const string SourceDomain = "domain";
}
