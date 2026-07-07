using AutopilotMonitor.Shared;
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
/// An EXPLICITLY DISABLED McpUsers row is an operator kill-switch that denies access under every
/// policy and every grant path (platform role, delegated auto-grant, AllMembers).
/// </summary>
public class McpUserService
{
    private readonly IAdminRepository _adminRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<McpUserService> _logger;
    private readonly GlobalAdminService _globalAdminService;
    private readonly DelegatedAdminService _delegatedAdminService;
    private readonly AdminConfigurationService _adminConfigService;
    // Per-process cache for the McpUsers whitelist lookup. On scaled-out Flex Consumption an add/remove
    // of an McpUsers row only invalidates the mutating instance, so other instances serve a stale
    // allow/deny until expiry. A short TTL caps that cross-instance window so an MCP-access grant/revoke
    // self-heals in seconds. The lookup is a single Table Storage point-read. Do NOT raise this back to
    // minutes "for performance" — it reintroduces the access flip-flop (see TenantAdminsService).
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    public McpUserService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<McpUserService> logger,
        GlobalAdminService globalAdminService,
        DelegatedAdminService delegatedAdminService,
        AdminConfigurationService adminConfigService)
    {
        _adminRepo = adminRepo;
        _cache = cache;
        _logger = logger;
        _globalAdminService = globalAdminService;
        _delegatedAdminService = delegatedAdminService;
        _adminConfigService = adminConfigService;
    }

    /// <summary>
    /// Checks if a user is allowed to access the MCP server based on current policy.
    /// <paramref name="homeTenantId"/> is the caller's JWT tid (home tenant) — it gates the
    /// delegated (MSP) auto-grant path, which requires an Enterprise home tenant. Null (unknown)
    /// fails closed to an empty delegated scope; the other grant paths are unaffected.
    /// </summary>
    public virtual async Task<McpAccessCheckResult> IsAllowedAsync(string? upn, string? homeTenantId = null)
    {
        if (string.IsNullOrWhiteSpace(upn))
            return McpAccessCheckResult.Denied("Missing UPN");

        upn = upn.ToLowerInvariant();

        // Always resolve the platform role — needed by the MCP server for cross-tenant routing
        // decisions (global scope → /api/global/* with tenantId-as-filter; non-global → /api/* JWT-bound).
        // GlobalReader has the SAME cross-tenant read scope as GA in the (read-only) MCP, so both
        // platform roles route the same way; only isGlobalAdmin (write power) differs. Resolved
        // unconditionally so downstream consumers don't need to re-check.
        var globalRole = await _globalAdminService.GetGlobalRoleAsync(upn);
        var isGlobalAdmin = globalRole == Constants.GlobalRoles.GlobalAdmin;

        // Resolve the delegated (scoped-global / MSP) scope unconditionally — independent of which policy
        // branch grants access — so it is always surfaced when present. A user may hold a platform role AND
        // a delegated assignment; both are reported. The MCP server uses delegatedTenantIds to route a
        // delegated caller to /api/global/*?tenantId=<managed> (the path the auth middleware authorizes)
        // and to reject any tool call that does not name one of the managed tenants.
        var delegatedScope = await _delegatedAdminService.GetScopeAsync(upn, homeTenantId);
        var delegatedTenantIds = delegatedScope.IsEmpty ? null : delegatedScope.TenantIds;
        var delegatedRole = delegatedScope.IsEmpty ? null : StrongestDelegatedRole(delegatedScope);

        // Resolve the caller's McpUsers whitelist state ONCE (cached): NotPresent / Enabled / Disabled.
        var whitelistState = await ResolveWhitelistStateAsync(upn);

        // SECURITY (operator kill-switch): an EXPLICITLY DISABLED McpUsers row denies MCP access under
        // EVERY policy and EVERY grant path — platform roles, the delegated (MSP) auto-grant, and
        // AllMembers alike. Checked BEFORE the policy switch so the one lever an operator reaches for
        // ("disable this account's MCP access") can never be silently inert under a permissive policy.
        // A MISSING row is NOT a kill-switch: most callers (delegated admins, AllMembers users) have no
        // McpUsers row at all and must still be granted by their policy branch below.
        if (whitelistState == McpWhitelistState.Disabled)
            return McpAccessCheckResult.Denied("User not enabled for MCP usage (account is disabled on the MCP whitelist)");

        var config = await _adminConfigService.GetConfigurationAsync();
        if (!Enum.TryParse<McpAccessPolicy>(config.McpAccessPolicy, out var policy))
            policy = McpAccessPolicy.WhitelistOnly;

        switch (policy)
        {
            case McpAccessPolicy.Disabled:
                // Disabled blocks everyone — platform roles and delegated admins alike.
                return McpAccessCheckResult.Denied("MCP access is disabled");

            case McpAccessPolicy.AllMembers:
                return McpAccessCheckResult.Allowed(
                    upn, "AllMembers", isGlobalAdmin, globalRole, delegatedTenantIds, delegatedRole);

            case McpAccessPolicy.WhitelistOnly:
            default:
                // Any platform role (GlobalAdmin or read-only GlobalReader) always has MCP access
                // (unless explicitly disabled via the kill-switch above).
                if (globalRole != null)
                    return McpAccessCheckResult.Allowed(
                        upn, globalRole, isGlobalAdmin, globalRole, delegatedTenantIds, delegatedRole);

                // A delegated (MSP) admin with an active scope is granted MCP access automatically under
                // WhitelistOnly — "delegated = scoped global", and they are already curated via the
                // Delegated Admins UI, so a separate enabled McpUsers row would be redundant friction. Their
                // reach is bounded client- and server-side to the managed tenant set; no platform/aggregate
                // path. (An explicit Disabled row above still revokes this.)
                if (!delegatedScope.IsEmpty)
                    return McpAccessCheckResult.Allowed(
                        upn, "DelegatedAdmin", isGlobalAdmin: false, globalRole: null,
                        delegatedTenantIds: delegatedTenantIds, delegatedRole: delegatedRole);

                return whitelistState == McpWhitelistState.Enabled
                    ? McpAccessCheckResult.Allowed(upn, "McpUser", false, null, delegatedTenantIds, delegatedRole)
                    // Surfaced to the end user by the MCP server's access guard. Keep it
                    // self-explanatory so a denied colleague understands they simply need
                    // to be whitelisted, rather than reading it as an auth failure.
                    : McpAccessCheckResult.Denied("User not enabled for MCP usage (account is not on the MCP whitelist)");
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

    /// <summary>
    /// The strongest delegated role held across the scope's tenants: DelegatedAdmin if the caller can write
    /// on any managed tenant, otherwise DelegatedReader. Surfaced for forward-compat (a future write tier
    /// over MCP) — the read-only MCP server gates nothing on it today.
    /// </summary>
    private static string StrongestDelegatedRole(DelegatedScope scope)
        => scope.TenantIds.Any(scope.CanWrite)
            ? Constants.DelegatedRoles.DelegatedAdmin
            : Constants.DelegatedRoles.DelegatedReader;

    private enum McpWhitelistState { NotPresent, Enabled, Disabled }

    /// <summary>
    /// Resolves (and briefly caches) the caller's McpUsers whitelist state: NotPresent (no row),
    /// Enabled (row + IsEnabled), or Disabled (row + !IsEnabled). A Disabled row is an operator
    /// kill-switch that denies access under EVERY policy and grant path — see <see cref="IsAllowedAsync"/>.
    /// Cached under the same key the mutation methods (Add/Remove/SetEnabled) invalidate, so an
    /// enable/disable self-heals within the TTL (and immediately on the mutating instance).
    /// </summary>
    private async Task<McpWhitelistState> ResolveWhitelistStateAsync(string upn)
    {
        var cacheKey = $"mcp-user:{upn}";
        if (_cache.TryGetValue<McpWhitelistState>(cacheKey, out var cached))
            return cached;

        var entry = await _adminRepo.GetMcpUserAsync(upn);
        var state = entry == null
            ? McpWhitelistState.NotPresent
            : entry.IsEnabled ? McpWhitelistState.Enabled : McpWhitelistState.Disabled;
        _cache.Set(cacheKey, state, _cacheDuration);
        return state;
    }
}

public class McpAccessCheckResult
{
    public bool IsAllowed { get; init; }
    public string Upn { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string AccessGrant { get; init; } = string.Empty;
    public bool IsGlobalAdmin { get; init; }

    /// <summary>
    /// Platform role of the caller: "GlobalAdmin", "GlobalReader", or null (no platform role). The MCP
    /// server uses this to grant cross-tenant routing to BOTH platform roles (the server is read-only).
    /// </summary>
    public string? GlobalRole { get; init; }

    /// <summary>
    /// Managed tenant IDs (lowercase) when the caller is a delegated (scoped-global / MSP) admin, else null.
    /// The MCP server routes a delegated caller to /api/global/*?tenantId=&lt;managed&gt; and rejects any tool
    /// call whose effective tenantId is not in this set. Distinct from <see cref="GlobalRole"/>: a delegated
    /// admin has NO platform role yet still gets bounded cross-tenant read access to exactly these tenants.
    /// </summary>
    public IReadOnlyCollection<string>? DelegatedTenantIds { get; init; }

    /// <summary>
    /// Strongest delegated role across the managed tenants ("DelegatedAdmin" | "DelegatedReader"), or null
    /// when the caller has no delegated scope. Surfaced for forward-compat; the read-only MCP gates nothing
    /// on it today.
    /// </summary>
    public string? DelegatedRole { get; init; }

    public static McpAccessCheckResult Allowed(
        string upn,
        string accessGrant,
        bool isGlobalAdmin = false,
        string? globalRole = null,
        IReadOnlyCollection<string>? delegatedTenantIds = null,
        string? delegatedRole = null) => new()
    {
        IsAllowed = true,
        Upn = upn,
        AccessGrant = accessGrant,
        IsGlobalAdmin = isGlobalAdmin,
        GlobalRole = globalRole,
        DelegatedTenantIds = delegatedTenantIds,
        DelegatedRole = delegatedRole
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
