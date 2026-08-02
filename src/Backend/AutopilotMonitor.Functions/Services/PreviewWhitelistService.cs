using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;
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

    /// <summary>
    /// Short TTL for a negative ("not approved") result. The activation page polls auth/me
    /// while the auto-approve worker activates the tenant ~1 minute after signup on another
    /// instance — a 5-minute negative cache would hide that activation for up to 4 extra
    /// minutes. Positive results keep the long TTL (activation is rarely revoked).
    /// </summary>
    private static readonly TimeSpan NotApprovedCacheDuration = TimeSpan.FromSeconds(30);

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
    /// Checks whether a tenant is activated (cached).
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

            _cache.Set(cacheKey, result, result ? CacheDuration : NotApprovedCacheDuration);
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

        _cache.Remove($"preview:{tenantId}");
        _logger.LogInformation("Tenant {TenantId} revoked from preview", tenantId);
    }

    /// <summary>
    /// Returns all approved tenants (for Global Admin overview).
    /// Returns PreviewWhitelistEntity list for backward compatibility with existing API consumers.
    /// </summary>
    public async Task<List<PreviewWhitelistEntity>> GetAllApprovedAsync()
    {
        var tenantIds = await _configRepo.GetPreviewWhitelistAsync();

        // Convert string list back to entity list for backward compatibility
        return tenantIds.Select(id => new PreviewWhitelistEntity
        {
            PartitionKey = id,
            RowKey = "approved"
        }).ToList();
    }

    /// <summary>
    /// Gets the notification email for a tenant (stored in PreviewWhitelist table).
    /// </summary>
    public virtual async Task<string?> GetNotificationEmailAsync(string tenantId)
    {
        return await _configRepo.GetNotificationEmailAsync(tenantId);
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
