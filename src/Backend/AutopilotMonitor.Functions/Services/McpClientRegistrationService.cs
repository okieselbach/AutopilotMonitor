using System.Net;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Self-hosted MCP client registrations (see <see cref="IMcpClientRegistrationRepository"/>): validation,
/// the per-tenant cap, the operator switch and the audit trail. The MCP server's OAuth proxy resolves a
/// client id <c>amc_&lt;id&gt;</c> through <see cref="LookupAsync"/> and binds the flow to the registering tenant.
/// </summary>
public class McpClientRegistrationService
{
    /// <summary>Registrations a tenant may hold unless a Global Admin raised its limit on request.</summary>
    public const int DefaultRegistrationLimit = 1;
    /// <summary>Upper bound of the per-tenant override (<c>TenantConfiguration.McpClientRegistrationLimit</c>).</summary>
    public const int MaxRegistrationLimit = 10;
    public const int MaxNameLength = 64;
    /// <summary>Mirrors the MCP proxy's redirect_uri length limit (oauth-limits.ts).</summary>
    public const int MaxRedirectUriLength = 1024;
    public const string ClientIdPrefix = "amc_";
    private const string AuditEntity = "McpClientRegistration";

    private static readonly Regex RegistrationIdPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private readonly IMcpClientRegistrationRepository _repo;
    private readonly IMaintenanceRepository _maintenanceRepo;
    private readonly AdminConfigurationService _adminConfigService;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly ILogger<McpClientRegistrationService> _logger;

    public McpClientRegistrationService(
        IMcpClientRegistrationRepository repo,
        IMaintenanceRepository maintenanceRepo,
        AdminConfigurationService adminConfigService,
        TenantConfigurationService tenantConfigService,
        ILogger<McpClientRegistrationService> logger)
    {
        _repo = repo;
        _maintenanceRepo = maintenanceRepo;
        _adminConfigService = adminConfigService;
        _tenantConfigService = tenantConfigService;
        _logger = logger;
    }

    /// <summary>The tenant's registration limit: its Global-Admin override, else the default of one.</summary>
    public virtual async Task<int> GetLimitAsync(string tenantId)
    {
        var (config, _) = await _tenantConfigService.TryGetConfigurationAsync(tenantId.ToLowerInvariant());
        return config.McpClientRegistrationLimit is int limit && limit >= 1 ? Math.Min(limit, MaxRegistrationLimit) : DefaultRegistrationLimit;
    }

    /// <summary>The operator switch (5-minute cached admin configuration).</summary>
    public virtual async Task<bool> IsEnabledAsync()
        => (await _adminConfigService.GetConfigurationAsync()).McpClientRegistrationEnabled;

    public static string ClientIdOf(string registrationId) => ClientIdPrefix + registrationId;

    /// <summary>Null when the name is acceptable, else the reason.</summary>
    public static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "A name is required.";
        if (trimmed.Length > MaxNameLength) return $"The name may have at most {MaxNameLength} characters.";
        if (trimmed.Any(char.IsControl)) return "The name contains control characters.";
        return null;
    }

    /// <summary>
    /// Null when the callback is acceptable, else the reason: an absolute https URL, or http on a loopback host,
    /// with no query, fragment, user info or wildcard — the MCP proxy redirects authorization codes to it.
    /// </summary>
    public static string? ValidateRedirectUri(string? redirectUri)
    {
        var value = redirectUri?.Trim() ?? string.Empty;
        if (value.Length == 0) return "A callback URL is required.";
        if (value.Length > MaxRedirectUriLength) return $"The callback URL may have at most {MaxRedirectUriLength} characters.";
        if (value.Contains('*')) return "The callback URL must be exact; wildcards are not allowed.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "The callback URL is not a valid absolute URL.";
        var loopback = uri.Host is "localhost" or "127.0.0.1" or "[::1]";
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
            return "The callback URL must use https (http is allowed only for localhost).";
        if (!string.IsNullOrEmpty(uri.Query)) return "The callback URL must not contain a query.";
        if (!string.IsNullOrEmpty(uri.Fragment) || value.Contains('#')) return "The callback URL must not contain a fragment.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The callback URL must not contain user information.";
        return null;
    }

    public static bool IsRegistrationId(string? registrationId)
        => registrationId != null && RegistrationIdPattern.IsMatch(registrationId);

    public virtual async Task<List<McpClientRegistration>> ListAsync(string tenantId)
        => (await _repo.GetForTenantAsync(tenantId)).OrderBy(r => r.CreatedAt).ToList();

    /// <summary>Creates a registration for <paramref name="tenantId"/>; the result names the HTTP status to answer.</summary>
    public virtual async Task<McpClientRegistrationResult> CreateAsync(string tenantId, string? name, string? redirectUri, string actor)
    {
        if (!await IsEnabledAsync())
            return McpClientRegistrationResult.Fail(HttpStatusCode.Forbidden, "Self-hosted MCP client registration is not enabled on this platform.");

        var nameError = ValidateName(name);
        if (nameError != null) return McpClientRegistrationResult.Fail(HttpStatusCode.BadRequest, nameError);
        var uriError = ValidateRedirectUri(redirectUri);
        if (uriError != null) return McpClientRegistrationResult.Fail(HttpStatusCode.BadRequest, uriError);

        var tenant = tenantId.ToLowerInvariant();
        var callback = redirectUri!.Trim();
        var existing = await _repo.GetForTenantAsync(tenant);
        var limit = await GetLimitAsync(tenant);
        if (existing.Count >= limit)
            return McpClientRegistrationResult.Fail(HttpStatusCode.Conflict,
                $"This tenant can register {limit} self-hosted AI client{(limit == 1 ? "" : "s")}. Delete one first, or ask us to raise the limit.");
        if (existing.Any(r => string.Equals(r.RedirectUri, callback, StringComparison.OrdinalIgnoreCase)))
            return McpClientRegistrationResult.Fail(HttpStatusCode.Conflict, "This callback URL is already registered.");

        var registration = new McpClientRegistration
        {
            RegistrationId = Guid.NewGuid().ToString("N"),
            TenantId = tenant,
            Name = name!.Trim(),
            RedirectUri = callback,
            CreatedBy = actor,
            CreatedAt = DateTime.UtcNow,
        };
        if (!await _repo.CreateAsync(registration))
            return McpClientRegistrationResult.Fail(HttpStatusCode.InternalServerError, "The registration could not be stored.");

        await _maintenanceRepo.LogAuditEntryAsync(tenant, "CREATE", AuditEntity, registration.RegistrationId, actor,
            new Dictionary<string, string> { { "Name", registration.Name }, { "RedirectUri", registration.RedirectUri } });
        _logger.LogWarning("MCP client registration {RegistrationId} created for tenant {TenantId} by {Actor}",
            registration.RegistrationId, tenant, actor);
        return McpClientRegistrationResult.Ok(registration);
    }

    /// <summary>
    /// Deletes one of <paramref name="tenantId"/>'s registrations — also while the switch is off, so a tenant can
    /// always clean up. False when the id is unknown or belongs to another tenant.
    /// </summary>
    public virtual async Task<bool> DeleteAsync(string tenantId, string registrationId, string actor)
    {
        if (!IsRegistrationId(registrationId)) return false;
        var registration = await _repo.GetAsync(registrationId);
        if (registration == null || !string.Equals(registration.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!await _repo.DeleteAsync(registrationId)) return false;

        await _maintenanceRepo.LogAuditEntryAsync(registration.TenantId, "DELETE", AuditEntity, registrationId, actor,
            new Dictionary<string, string> { { "Name", registration.Name }, { "RedirectUri", registration.RedirectUri } });
        _logger.LogWarning("MCP client registration {RegistrationId} deleted for tenant {TenantId} by {Actor}",
            registrationId, registration.TenantId, actor);
        return true;
    }

    /// <summary>The MCP proxy's lookup: the registration, or null when unknown or while the switch is off.</summary>
    public virtual async Task<McpClientRegistration?> LookupAsync(string registrationId)
    {
        if (!IsRegistrationId(registrationId)) return null;
        if (!await IsEnabledAsync()) return null;
        return await _repo.GetAsync(registrationId);
    }
}

/// <summary>Outcome of <see cref="McpClientRegistrationService.CreateAsync"/>.</summary>
public sealed record McpClientRegistrationResult(McpClientRegistration? Registration, HttpStatusCode Status, string? Error)
{
    public static McpClientRegistrationResult Ok(McpClientRegistration registration) => new(registration, HttpStatusCode.Created, null);
    public static McpClientRegistrationResult Fail(HttpStatusCode status, string error) => new(null, status, error);
}
