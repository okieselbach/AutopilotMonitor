using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing MCP (Model Context Protocol) user access.
/// Access is governed by AdminConfiguration.McpAccessPolicy:
///   - Disabled: no one can access MCP
///   - WhitelistOnly: Global Admins + explicit McpUsers table entries
///   - AllMembers: any authenticated user
/// </summary>
public class McpUserService
{
    private readonly IAdminRepository _adminRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<McpUserService> _logger;
    private readonly GlobalAdminService _globalAdminService;
    private readonly AdminConfigurationService _adminConfigService;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public McpUserService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<McpUserService> logger,
        GlobalAdminService globalAdminService,
        AdminConfigurationService adminConfigService)
    {
        _adminRepo = adminRepo;
        _cache = cache;
        _logger = logger;
        _globalAdminService = globalAdminService;
        _adminConfigService = adminConfigService;
    }

    /// <summary>
    /// Checks if a user is allowed to access the MCP server based on current policy.
    /// </summary>
    public virtual async Task<McpAccessCheckResult> IsAllowedAsync(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn))
            return McpAccessCheckResult.Denied("Missing UPN");

        upn = upn.ToLowerInvariant();

        // Always resolve GA status — needed by the MCP server for cross-tenant routing
        // decisions (GA → /api/global/* with tenantId-as-filter; non-GA → /api/* JWT-bound).
        // Resolved unconditionally so downstream consumers don't need to re-check.
        var isGlobalAdmin = await _globalAdminService.IsGlobalAdminAsync(upn);

        var config = await _adminConfigService.GetConfigurationAsync();
        if (!Enum.TryParse<McpAccessPolicy>(config.McpAccessPolicy, out var policy))
            policy = McpAccessPolicy.WhitelistOnly;

        switch (policy)
        {
            case McpAccessPolicy.Disabled:
                return McpAccessCheckResult.Denied("MCP access is disabled");

            case McpAccessPolicy.AllMembers:
                return McpAccessCheckResult.Allowed(upn, "AllMembers", isGlobalAdmin);

            case McpAccessPolicy.WhitelistOnly:
            default:
                // Global Admins always have access
                if (isGlobalAdmin)
                    return McpAccessCheckResult.Allowed(upn, "GlobalAdmin", true);

                // Check McpUsers whitelist (cached)
                var cacheKey = $"mcp-user:{upn}";
                if (!_cache.TryGetValue<bool>(cacheKey, out var isMcpUser))
                {
                    isMcpUser = await _adminRepo.IsMcpUserAsync(upn);
                    _cache.Set(cacheKey, isMcpUser, _cacheDuration);
                }

                return isMcpUser
                    ? McpAccessCheckResult.Allowed(upn, "McpUser", false)
                    : McpAccessCheckResult.Denied("Not authorized for MCP access");
        }
    }

    public async Task<McpUserEntry> AddMcpUserAsync(string upn, string addedBy)
    {
        upn = upn.ToLowerInvariant();
        addedBy = addedBy.ToLowerInvariant();

        await _adminRepo.AddMcpUserAsync(upn, addedBy);
        _cache.Remove($"mcp-user:{upn}");

        _logger.LogInformation("MCP user added: {Upn} by {AddedBy}", upn, addedBy);

        return new McpUserEntry
        {
            Upn = upn,
            IsEnabled = true,
            AddedAt = DateTime.UtcNow,
            AddedBy = addedBy
        };
    }

    public async Task RemoveMcpUserAsync(string upn)
    {
        upn = upn.ToLowerInvariant();
        await _adminRepo.RemoveMcpUserAsync(upn);
        _cache.Remove($"mcp-user:{upn}");

        _logger.LogInformation("MCP user removed: {Upn}", upn);
    }

    public async Task SetMcpUserEnabledAsync(string upn, bool isEnabled)
    {
        upn = upn.ToLowerInvariant();
        await _adminRepo.SetMcpUserEnabledAsync(upn, isEnabled);
        _cache.Remove($"mcp-user:{upn}");

        _logger.LogInformation("MCP user {Action}: {Upn}", isEnabled ? "enabled" : "disabled", upn);
    }

    public async Task<McpUserEntry?> GetMcpUserAsync(string upn)
    {
        upn = upn.ToLowerInvariant();
        return await _adminRepo.GetMcpUserAsync(upn);
    }

    public async Task<List<McpUserEntry>> GetAllMcpUsersAsync()
    {
        return await _adminRepo.GetAllMcpUsersAsync();
    }

    public async Task<bool> SetMcpUserUsagePlanAsync(string upn, string? usagePlan)
    {
        upn = upn.ToLowerInvariant();
        var result = await _adminRepo.SetMcpUserUsagePlanAsync(upn, usagePlan);
        if (result)
            _logger.LogInformation("MCP user usage plan set: {Upn} -> {Plan}", upn, usagePlan ?? "(inherit)");
        return result;
    }

    /// <summary>
    /// Returns the current MCP access policy.
    /// </summary>
    public async Task<McpAccessPolicy> GetPolicyAsync()
    {
        var config = await _adminConfigService.GetConfigurationAsync();
        return Enum.TryParse<McpAccessPolicy>(config.McpAccessPolicy, out var policy)
            ? policy
            : McpAccessPolicy.WhitelistOnly;
    }
}

public class McpAccessCheckResult
{
    public bool IsAllowed { get; init; }
    public string Upn { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string AccessGrant { get; init; } = string.Empty;
    public bool IsGlobalAdmin { get; init; }

    public static McpAccessCheckResult Allowed(string upn, string accessGrant, bool isGlobalAdmin = false) => new()
    {
        IsAllowed = true,
        Upn = upn,
        AccessGrant = accessGrant,
        IsGlobalAdmin = isGlobalAdmin
    };

    public static McpAccessCheckResult Denied(string reason) => new()
    {
        IsAllowed = false,
        Reason = reason
    };
}

/// <summary>
/// Entity representing an MCP user in Table Storage
/// </summary>
public class McpUserEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "McpUsers";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    public string Upn { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime AddedDate { get; set; }
    public string AddedBy { get; set; } = string.Empty;
    public string? UsagePlan { get; set; }
}
