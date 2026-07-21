using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing the Private Preview tenant whitelist.
/// Tenants in this list are allowed full portal access; others see a waitlist page.
/// Caching and business logic layer — delegates storage to IConfigRepository.
/// Temporary — remove after GA.
/// </summary>
public class PreviewWhitelistService
{
    private readonly IConfigRepository _configRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PreviewWhitelistService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PreviewWhitelistService(
        IConfigRepository configRepo,
        IMemoryCache cache,
        ILogger<PreviewWhitelistService> logger)
    {
        _configRepo = configRepo;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether a tenant is approved for Private Preview (cached).
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

            _cache.Set(cacheKey, result, CacheDuration);
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
    /// Approves a tenant for Private Preview access.
    /// </summary>
    public async Task ApproveAsync(string tenantId, string approvedBy)
    {
        await _configRepo.AddToPreviewWhitelistAsync(tenantId, approvedBy);

        _cache.Remove($"preview:{tenantId}");
        _logger.LogInformation("Tenant {TenantId} approved for preview by {ApprovedBy}", tenantId, approvedBy);
    }

    /// <summary>
    /// Revokes a tenant's Private Preview access.
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
    public async Task<string?> GetNotificationEmailAsync(string tenantId)
    {
        return await _configRepo.GetNotificationEmailAsync(tenantId);
    }

    /// <summary>
    /// Saves (or clears) the notification email for a tenant, and seeds the tenant's
    /// contact address from it the first time one is given.
    /// </summary>
    public async Task SaveNotificationEmailAsync(string tenantId, string? email)
    {
        await _configRepo.SaveNotificationEmailAsync(tenantId, email);
        await SeedContactEmailAsync(tenantId, email);
    }

    /// <summary>
    /// One-way seed: copies the preview notification address into
    /// <see cref="TenantConfiguration.ContactEmail"/> only while that field is still empty.
    /// Once the tenant owns a contact address — seeded or edited in the portal — this never
    /// touches it again, so a later change here cannot silently overwrite the tenant's choice.
    /// Best-effort: the notification email has already been persisted by the caller, and a
    /// failure to seed must not fail that write.
    /// </summary>
    private async Task SeedContactEmailAsync(string tenantId, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return;

        try
        {
            var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
            if (config == null || !string.IsNullOrWhiteSpace(config.ContactEmail))
                return;

            config.ContactEmail = email.Trim();
            await _configRepo.SaveTenantConfigurationAsync(config);

            _logger.LogInformation(
                "Seeded tenant contact address for {TenantId} from the preview notification email", tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not seed tenant contact address for {TenantId} — the notification email was still saved", tenantId);
        }
    }
}
