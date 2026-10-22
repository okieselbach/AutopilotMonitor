using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing the tenant-activation whitelist (table: PreviewWhitelist —
/// legacy name kept on purpose, no data migration). Tenants in this list are activated
/// for full portal access; others see the activation-pending page.
/// Caching and business logic layer — delegates storage to IConfigRepository.
/// </summary>
public class PreviewWhitelistService
{
    private readonly IConfigRepository _configRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PreviewWhitelistService> _logger;
    private readonly TenantConfigurationService _tenantConfigService;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PreviewWhitelistService(
        IConfigRepository configRepo,
        IMemoryCache cache,
        ILogger<PreviewWhitelistService> logger,
        TenantConfigurationService tenantConfigService)
    {
        _configRepo = configRepo;
        _cache = cache;
        _logger = logger;
        _tenantConfigService = tenantConfigService;
    }

    /// <summary>
    /// Checks whether a tenant is activated. Only an activation is cached: "not activated" is read
    /// fresh on every call, because the activation is written on the auto-approve worker (another
    /// instance, out of reach of this cache) while the activation page polls auth/me. The
    /// notification-email save path relies on this, too: it must see an activation that landed
    /// just before its own write (welcome-mail arbitration).
    /// </summary>
    public virtual async Task<bool> IsApprovedAsync(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return false;

        var cacheKey = $"preview:{tenantId}";
        if (_cache.TryGetValue<bool>(cacheKey, out var approved))
            return approved;

        try
        {
            var result = await _configRepo.IsInPreviewWhitelistAsync(tenantId);

            if (result)
                _cache.Set(cacheKey, true, CacheDuration);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking preview whitelist for tenant {TenantId}", tenantId);
            // Fail-closed: if we can't check, deny access
            return false;
        }
    }

    /// <summary>
    /// Uncached read of the activation, for the activation path's own pre-check: a cached activation can
    /// outlive a revoke on another instance by up to 5 minutes, and a re-activation must not be answered
    /// as "already activated" from that cache. Storage errors throw.
    /// </summary>
    public virtual Task<bool> IsApprovedFreshAsync(string tenantId)
        => _configRepo.IsInPreviewWhitelistAsync(tenantId);

    /// <summary>
    /// Activates a tenant (adds it to the whitelist). Returns true when this call created
    /// the entry, false when the tenant was already activated — the conditional insert at
    /// the storage layer arbitrates concurrent activations, so exactly one caller ever
    /// sees true and runs the activation side effects. Storage errors throw.
    /// </summary>
    public virtual async Task<bool> ApproveAsync(string tenantId, string approvedBy)
    {
        var newlyApproved = await _configRepo.AddToPreviewWhitelistAsync(tenantId, approvedBy);

        _cache.Remove($"preview:{tenantId}");
        if (newlyApproved)
        {
            _logger.LogInformation("Tenant {TenantId} approved for preview by {ApprovedBy}", tenantId, approvedBy);
        }
        return newlyApproved;
    }

    /// <summary>
    /// Revokes a tenant's activation.
    /// </summary>
    public async Task RevokeAsync(string tenantId)
    {
        await _configRepo.RemoveFromPreviewWhitelistAsync(tenantId);

        // A re-approve after revoke is a fresh activation and should send a fresh
        // welcome mail — drop the once-only marker along with the approval.
        await _configRepo.ClearWelcomeEmailSentMarkerAsync(tenantId);

        _cache.Remove($"preview:{tenantId}");
        _logger.LogInformation("Tenant {TenantId} revoked from preview", tenantId);
    }

    /// <summary>
    /// Returns all approved tenants (for Global Admin overview).
    /// </summary>
    public async Task<List<PreviewWhitelistTenantEntry>> GetAllApprovedAsync()
    {
        var tenantIds = await _configRepo.GetPreviewWhitelistAsync();

        return tenantIds.Select(id => new PreviewWhitelistTenantEntry { TenantId = id }).ToList();
    }

    /// <summary>
    /// Gets the notification email for a tenant (stored in PreviewWhitelist table).
    /// </summary>
    public virtual async Task<string?> GetNotificationEmailAsync(string tenantId)
    {
        return await _configRepo.GetNotificationEmailAsync(tenantId);
    }

    /// <summary>
    /// Every stored notification address, keyed by lowercased tenant id. Uncached: the operator
    /// console reads this on demand to resolve an address back to its tenant, and a stale map
    /// would silently hide a tenant that just saved one.
    /// </summary>
    public virtual async Task<Dictionary<string, string>> GetAllNotificationEmailsAsync()
    {
        return await _configRepo.GetAllNotificationEmailsAsync();
    }

    /// <summary>
    /// Consumes the once-per-activation welcome-email marker (conditional insert at the
    /// storage layer). True = this caller won and may send. Storage errors throw.
    /// </summary>
    public virtual async Task<bool> TryMarkWelcomeEmailSentAsync(string tenantId)
    {
        return await _configRepo.TryMarkWelcomeEmailSentAsync(tenantId);
    }

    /// <summary>
    /// Releases the welcome-email marker again after a send that never reached the customer.
    /// The marker records delivery, not an attempt — keeping it after a refused send would
    /// suppress this tenant's welcome mail forever. Fail-soft at the storage layer.
    /// </summary>
    public virtual async Task ClearWelcomeEmailSentMarkerAsync(string tenantId)
    {
        await _configRepo.ClearWelcomeEmailSentMarkerAsync(tenantId);
    }

    /// <summary>
    /// Saves (or clears) the notification email for a tenant, and seeds the tenant's
    /// contact address from it the first time one is given.
    /// <para>
    /// The seed is a side effect of a write that has already been persisted, so it must never
    /// fail this call — <see cref="TenantConfigurationService.TrySeedContactEmailAsync"/> is
    /// fail-soft and owns the "never overwrite what the tenant owns" invariant.
    /// </para>
    /// </summary>
    public async Task SaveNotificationEmailAsync(string tenantId, string? email)
    {
        await _configRepo.SaveNotificationEmailAsync(tenantId, email);
        await _tenantConfigService.TrySeedContactEmailAsync(tenantId, email);
    }
}
