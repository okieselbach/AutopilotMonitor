using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Security;

/// <summary>
/// Tenant scoping mode for endpoint access control.
/// Determines how the middleware enforces tenant isolation.
/// </summary>
public enum TenantScoping
{
    /// <summary>No tenant scoping needed (public, device-auth, global-admin-only routes).</summary>
    None,

    /// <summary>TenantId comes from JWT tid claim (inherently safe, no middleware check needed).</summary>
    Jwt,

    /// <summary>TenantId comes from {tenantId} route parameter. Middleware enforces cross-tenant check.</summary>
    RouteParam,

    /// <summary>TenantId comes from ?tenantId= query parameter (optional, falls back to JWT tenant). Middleware enforces cross-tenant check.</summary>
    QueryParam,
}

/// <summary>
/// Authorization policy tiers for endpoint access control.
/// Ordered from least restrictive to most restrictive.
/// </summary>
public enum EndpointPolicy
{
    /// <summary>No authentication required. Fully public.</summary>
    PublicAnonymous,

    /// <summary>Device certificate or bootstrap token auth. No JWT.</summary>
    DeviceOrBootstrapAuth,

    /// <summary>Valid JWT token required. Any authenticated user.</summary>
    AuthenticatedUser,

    /// <summary>Tenant member with Admin, Operator, or Viewer role. Tenant-scoped read access.</summary>
    MemberRead,

    /// <summary>Tenant Admin or Global Admin. Tenant-scoped write access.</summary>
    TenantAdminOrGA,

    /// <summary>Admin (always) or Operator with CanManageBootstrapTokens permission, or Global Admin.</summary>
    BootstrapManagerOrGA,

    /// <summary>Global Admin only. Platform-wide access.</summary>
    GlobalAdminOnly,
}

/// <summary>
/// A single entry in the endpoint access policy catalog.
/// </summary>
public sealed class EndpointPolicyEntry
{
    public string HttpMethod { get; }
    public string RouteTemplate { get; }
    public EndpointPolicy Policy { get; }
    public TenantScoping TenantScoping { get; }

    // Pre-compiled regex for matching actual request paths against the route template
    internal Regex RouteRegex { get; }

    public EndpointPolicyEntry(string httpMethod, string routeTemplate, EndpointPolicy policy,
        TenantScoping tenantScoping = TenantScoping.None)
    {
        HttpMethod = httpMethod.ToUpperInvariant();
        RouteTemplate = routeTemplate;
        Policy = policy;
        TenantScoping = tenantScoping;
        RouteRegex = BuildRouteRegex(routeTemplate);
    }

    /// <summary>
    /// Converts a route template like "sessions/{sessionId}/events" into a regex
    /// that matches actual paths like "sessions/abc-123/events".
    /// Uses a named capture group for {tenantId} so the middleware can extract it.
    /// </summary>
    private static Regex BuildRouteRegex(string routeTemplate)
    {
        // Escape regex special chars, then replace {param} placeholders
        // {tenantId} gets a named capture group; all others get a generic [^/]+
        var escaped = Regex.Escape(routeTemplate);
        var pattern = Regex.Replace(escaped, @"\\\{([^}]+)}", m =>
            m.Groups[1].Value == "tenantId" ? "(?<tenantId>[^/]+)" : "[^/]+");
        return new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

/// <summary>
/// Single source of truth for endpoint authorization policies.
/// Every HTTP route must be registered here. Unregistered routes fail closed.
/// </summary>
public static class EndpointAccessPolicyCatalog
{
    private static readonly EndpointPolicyEntry[] _entries =
    {
        // ── PublicAnonymous ─────────────────────────────────────────────
        new("GET",    "health",                    EndpointPolicy.PublicAnonymous),
        new("GET",    "stats/platform",            EndpointPolicy.PublicAnonymous),
        new("GET",    "bootstrap/validate/{code}", EndpointPolicy.PublicAnonymous),
        new("POST",   "agent/distress",             EndpointPolicy.PublicAnonymous),

        // ── DeviceOrBootstrapAuth ───────────────────────────────────────
        new("POST",   "agent/register-session",    EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "agent/ingest",              EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "agent/telemetry",           EndpointPolicy.DeviceOrBootstrapAuth),
        new("GET",    "agent/config",              EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "agent/upload-url",          EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "agent/error",               EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "bootstrap/register-session", EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "bootstrap/ingest",          EndpointPolicy.DeviceOrBootstrapAuth),
        new("GET",    "bootstrap/config",          EndpointPolicy.DeviceOrBootstrapAuth),
        new("POST",   "bootstrap/error",           EndpointPolicy.DeviceOrBootstrapAuth),

        // ── AuthenticatedUser ───────────────────────────────────────────
        new("GET",    "auth/me",                   EndpointPolicy.AuthenticatedUser),
        new("GET",    "auth/is-global-admin",      EndpointPolicy.AuthenticatedUser),
        new("POST",   "realtime/negotiate",        EndpointPolicy.AuthenticatedUser),
        new("POST",   "realtime/groups/join",      EndpointPolicy.MemberRead),
        new("POST",   "realtime/groups/leave",     EndpointPolicy.MemberRead),
        new("GET",    "progress/sessions",         EndpointPolicy.AuthenticatedUser),
        new("GET",    "progress/sessions/{sessionId}/events", EndpointPolicy.AuthenticatedUser, TenantScoping.QueryParam),
        new("PUT",    "preview/notification-email", EndpointPolicy.AuthenticatedUser),
        new("GET",    "feedback/status",           EndpointPolicy.AuthenticatedUser),
        new("POST",   "feedback",                  EndpointPolicy.AuthenticatedUser),
        new("GET",    "config/latest-versions",    EndpointPolicy.AuthenticatedUser),

        // ── MemberRead (Admin + Operator, later + Viewer) ───────────────
        new("GET",    "raw/sessions",                        EndpointPolicy.MemberRead),
        new("GET",    "raw/events",                          EndpointPolicy.MemberRead),
        new("GET",    "raw/events/search",                   EndpointPolicy.MemberRead),
        new("GET",    "search/quick",                   EndpointPolicy.MemberRead),
        new("GET",    "search/sessions",                EndpointPolicy.MemberRead),
        new("GET",    "search/sessions-by-event",       EndpointPolicy.MemberRead),
        new("GET",    "search/sessions-by-cve",         EndpointPolicy.MemberRead),
        new("GET",    "metrics/summary",              EndpointPolicy.MemberRead),
        new("GET",    "sessions",                  EndpointPolicy.MemberRead),
        new("GET",    "stats/sessions",            EndpointPolicy.MemberRead),
        new("GET",    "sessions/{sessionId}",      EndpointPolicy.MemberRead, TenantScoping.QueryParam),
        new("GET",    "sessions/{sessionId}/events", EndpointPolicy.MemberRead, TenantScoping.QueryParam),
        // sessions/{sessionId}/signals + /decision-graph live in the GlobalAdminOnly block below
        // (Inspector v1 — Plan §M6). Lift back to MemberRead+QueryParam at the v2 adminMode lift.
        new("GET",    "sessions/{sessionId}/analysis", EndpointPolicy.MemberRead, TenantScoping.QueryParam),
        new("GET",    "sessions/{sessionId}/vulnerability-report", EndpointPolicy.MemberRead, TenantScoping.QueryParam),
        new("GET",    "metrics/app",               EndpointPolicy.MemberRead),
        new("GET",    "apps/list",                 EndpointPolicy.MemberRead),
        new("GET",    "apps/{appName}/analytics",  EndpointPolicy.MemberRead),
        new("GET",    "apps/{appName}/sessions",   EndpointPolicy.MemberRead),
        new("GET",    "metrics/usage",             EndpointPolicy.MemberRead),
        new("GET",    "metrics/sla",               EndpointPolicy.MemberRead),
        new("GET",    "metrics/rule-stats",        EndpointPolicy.MemberRead),
        new("GET",    "metrics/geographic",        EndpointPolicy.MemberRead),
        new("GET",    "metrics/mcp-usage/user/{userId}", EndpointPolicy.TenantAdminOrGA),
        new("GET",    "metrics/geographic/sessions", EndpointPolicy.MemberRead),
        new("GET",    "metrics/ime-versions",      EndpointPolicy.MemberRead),
        new("GET",    "audit/logs",                EndpointPolicy.MemberRead),
        new("GET",    "audit/hardware-rejected", EndpointPolicy.MemberRead),
        new("GET",    "diagnostics/download-url",  EndpointPolicy.MemberRead, TenantScoping.QueryParam),
        new("GET",    "rules/gather",              EndpointPolicy.MemberRead),
        new("GET",    "rules/analyze",             EndpointPolicy.MemberRead),
        new("GET",    "rules/ime-log-patterns",    EndpointPolicy.MemberRead),
        new("GET",    "config/{tenantId}",         EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("GET",    "config/{tenantId}/feature-flags", EndpointPolicy.MemberRead, TenantScoping.RouteParam),

        // ── TenantAdminOrGA ─────────────────────────────────────────────
        new("PUT",    "config/{tenantId}",         EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "config/{tenantId}",         EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "rules/gather",              EndpointPolicy.TenantAdminOrGA),
        new("PUT",    "rules/gather/{ruleId}",     EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "rules/gather/{ruleId}",     EndpointPolicy.TenantAdminOrGA),
        new("POST",   "rules/analyze",             EndpointPolicy.TenantAdminOrGA),
        new("POST",   "rules/analyze/{ruleId}/create-from-template", EndpointPolicy.TenantAdminOrGA),
        new("PUT",    "rules/analyze/{ruleId}",    EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "rules/analyze/{ruleId}",    EndpointPolicy.TenantAdminOrGA),
        new("PUT",    "rules/ime-log-patterns/{patternId}", EndpointPolicy.TenantAdminOrGA),
        new("POST",   "sessions/{sessionId}/mark-failed",     EndpointPolicy.TenantAdminOrGA),
        new("POST",   "sessions/{sessionId}/mark-succeeded", EndpointPolicy.TenantAdminOrGA),
        new("POST",   "sessions/{sessionId}/actions",          EndpointPolicy.TenantAdminOrGA),
        new("POST",   "sessions/{sessionId}/report",        EndpointPolicy.TenantAdminOrGA),
        new("POST",   "diag-files-reports",                 EndpointPolicy.TenantAdminOrGA),
        new("DELETE", "sessions/{sessionId}",      EndpointPolicy.TenantAdminOrGA, TenantScoping.QueryParam),
        new("GET",    "tenants/{tenantId}/admins",           EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "tenants/{tenantId}/admins",           EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("DELETE", "tenants/{tenantId}/admins/{adminUpn}", EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("PATCH",  "tenants/{tenantId}/admins/{adminUpn}/disable",     EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("PATCH",  "tenants/{tenantId}/admins/{adminUpn}/enable",      EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("PATCH",  "tenants/{tenantId}/admins/{adminUpn}/permissions", EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("DELETE", "tenants/{tenantId}/offboard", EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "tenants/{tenantId}/offboard/feedback", EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("GET",    "config/{tenantId}/autopilot-device-validation/consent-url",     EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("GET",    "config/{tenantId}/autopilot-device-validation/consent-status",  EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "config/{tenantId}/autopilot-device-validation/consent-failure", EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "config/{tenantId}/autopilot-device-validation/consent-success", EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "config/{tenantId}/test-notification",                           EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),

        // ── Graph add-on permissions ────────────────────────────────────────
        // Script display-name resolution is a *read* surface available to every tenant
        // member — Member-tier callers benefit from the resolved names in the session
        // timeline. The Admin UI controls (status + refresh) stay Admin/GA because they
        // expose ClientId and invalidate caches.
        // POST (not GET) so payloads with 100+ script refs comfortably fit in the request
        // body without hitting URL-length limits in browsers / Azure Functions front door.
        new("POST",   "tenants/{tenantId}/scripts/display-names",     EndpointPolicy.MemberRead, TenantScoping.RouteParam),
        new("GET",    "tenants/{tenantId}/graph-permissions/status",  EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),
        new("POST",   "tenants/{tenantId}/graph-permissions/refresh", EndpointPolicy.TenantAdminOrGA, TenantScoping.RouteParam),

        // ── BootstrapManagerOrGA ────────────────────────────────────────
        new("GET",    "bootstrap/sessions",        EndpointPolicy.BootstrapManagerOrGA, TenantScoping.QueryParam),
        new("POST",   "bootstrap/sessions",        EndpointPolicy.BootstrapManagerOrGA, TenantScoping.QueryParam),
        new("DELETE", "bootstrap/sessions/{code}", EndpointPolicy.BootstrapManagerOrGA, TenantScoping.QueryParam),

        // ── MCP Access Check (any authenticated user can check their own access) ──
        new("GET",    "auth/mcp",                              EndpointPolicy.AuthenticatedUser),

        // ── MCP Usage (self-service) ──────────────────────────────────
        new("GET",    "metrics/mcp-usage/me",                  EndpointPolicy.AuthenticatedUser),

        // ── GlobalAdminOnly ────────────────────────────────────────────
        // Inspector v1 endpoints (Plan §M6 — temporarily GlobalAdminOnly while the UI
        // matures; lift to MemberRead+QueryParam scoping at the v2 adminMode lift).
        // Cross-tenant resolution for GAs happens inside the functions via SessionsIndex,
        // so TenantScoping.None on the catalog is fine.
        new("GET",    "sessions/{sessionId}/signals",              EndpointPolicy.GlobalAdminOnly),
        new("GET",    "sessions/{sessionId}/decision-graph",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "sessions/{sessionId}/reducer-verification", EndpointPolicy.GlobalAdminOnly),
        // Cascade-delete admin endpoints live under /api/global/* (not /api/admin/*) because the
        // latter prefix collides with the Azure Functions runtime's own admin routes, which are
        // mTLS-gated — browsers triggering preflight on /api/admin/* get TLS-renegotiation
        // failures instead of CORS responses. Keep all cross-tenant GA-only HTTP routes under
        // /api/global/.
        new("GET",    "global/sessions/{sessionId}/delete/preview",   EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/sessions/{sessionId}/restore",          EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/sessions/{sessionId}/deletion-manifest", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/tenants/{tenantId}/deletion-manifests",  EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("GET",    "global/session-deletions",                    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/sessions",                  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/events",                    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/events/search",              EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/tables",                    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/raw/tables/{tableName}",        EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/raw/logs",                      EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/search/sessions",              EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/search/sessions-by-event",   EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/search/sessions-by-cve",     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/summary",           EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/config",             EndpointPolicy.GlobalAdminOnly),
        new("PUT",    "global/config",             EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/config",             EndpointPolicy.GlobalAdminOnly),
        new("GET",    "config/all",                EndpointPolicy.GlobalAdminOnly),
        new("GET",    "auth/global-admins",        EndpointPolicy.GlobalAdminOnly),
        new("POST",   "auth/global-admins",        EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "auth/global-admins/{upn}",  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "preview/whitelist",          EndpointPolicy.GlobalAdminOnly),
        new("POST",   "preview/whitelist/{tenantId}", EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("DELETE", "preview/whitelist/{tenantId}", EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("GET",    "preview/notification-email/{tenantId}", EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("POST",   "preview/send-welcome-email/{tenantId}", EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("GET",    "global/sessions",            EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/stats/sessions",      EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/audit/logs",          EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/tenants-with-deletion-manifests", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/platform",    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/app",         EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/apps/list",           EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/apps/{appName}/analytics", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/apps/{appName}/sessions",  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/geographic",  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/geographic/sessions", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/usage",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/sla",         EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/rule-stats",  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/mcp-usage",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/metrics/mcp-usage/daily", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/distress-reports",    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/session-reports",     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/session-reports/download-url", EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/session-reports/{reportId}/note", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/rules/gather",        EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/rules/analyze",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/devices/blocked",     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "devices/blocked",            EndpointPolicy.GlobalAdminOnly),
        new("POST",   "devices/block",              EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "devices/block/{encodedSerialNumber}", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "versions/blocked",           EndpointPolicy.GlobalAdminOnly),
        new("POST",   "versions/block",             EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "versions/block/{encodedPattern}", EndpointPolicy.GlobalAdminOnly),
        new("POST",   "maintenance/trigger",        EndpointPolicy.GlobalAdminOnly),

        // ── Critical-Table Backup (plan §PR1) ──────────────────────────────────
        new("POST",   "global/backups/trigger",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/backups",               EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/backups/{backupId}",    EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/backups/jobs/{jobId}",  EndpointPolicy.GlobalAdminOnly),

        // ── Tenant Offboarding Customs Archive (PR3.B) ─────────────────────────
        // Snapshot of each tenant's custom GatherRules / AnalyzeRules / ImeLogPatterns
        // rows from prior offboarding runs. Operator-driven cleanup: GA reviews + deletes.
        // The {tenantId} route param is the OFFBOARDED tenant whose data was archived;
        // the operator (GA) does not belong to that tenant. RouteParam scoping declares
        // that the route's tenantId is supplied from the URL, not from the caller's JWT.
        new("GET",    "global/customs-archive",                                                  EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/customs-archive/{tenantId}/{historyRowKey}",                       EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("GET",    "global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey}",       EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("DELETE", "global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey}",       EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("DELETE", "global/customs-archive/{tenantId}/{historyRowKey}",                       EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("GET",    "health/detailed",            EndpointPolicy.AuthenticatedUser),
        new("POST",   "rules/reseed-from-github",   EndpointPolicy.GlobalAdminOnly),
        new("GET",    "vulnerability/unmatched-software", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "vulnerability/software-inventory", EndpointPolicy.GlobalAdminOnly),
        new("POST",   "vulnerability/sync",              EndpointPolicy.GlobalAdminOnly),
        new("POST",   "vulnerability/sync-msrc",         EndpointPolicy.GlobalAdminOnly),
        new("POST",   "vulnerability/cpe-mapping",       EndpointPolicy.GlobalAdminOnly),
        new("POST",   "vulnerability/cpe-mapping/auto-resolve", EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "vulnerability/cpe-mapping",       EndpointPolicy.GlobalAdminOnly),
        new("GET",    "vulnerability/cpe-mappings",      EndpointPolicy.GlobalAdminOnly),
        new("GET",    "vulnerability/ignored-software", EndpointPolicy.GlobalAdminOnly),
        new("POST",   "vulnerability/ignored-software", EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "vulnerability/ignored-software", EndpointPolicy.GlobalAdminOnly),
        new("POST",   "rules/ime-log-patterns/reseed", EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/mcp-users",                     EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/mcp-users",                     EndpointPolicy.GlobalAdminOnly),
        new("DELETE", "global/mcp-users/{upn}",               EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/mcp-users/{upn}/enable",        EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/mcp-users/{upn}/disable",       EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "global/mcp-users/{upn}/usage-plan",    EndpointPolicy.GlobalAdminOnly),
        new("PATCH",  "config/{tenantId}/plan",                            EndpointPolicy.GlobalAdminOnly, TenantScoping.RouteParam),
        new("GET",    "global/config/plan-tiers",                           EndpointPolicy.GlobalAdminOnly),
        new("PUT",    "global/config/plan-tiers",                           EndpointPolicy.GlobalAdminOnly),
        new("GET",    "feedback/all",                                     EndpointPolicy.GlobalAdminOnly),
        new("GET",    "global/notifications",                            EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/notifications/dismiss-all",                EndpointPolicy.GlobalAdminOnly),
        new("POST",   "global/notifications/{notificationId}/dismiss",   EndpointPolicy.GlobalAdminOnly),
        // Tenant-scoped persistent notifications (bell). TenantId comes from JWT — middleware enforces.
        // GET is MemberRead (Admin/Operator/Viewer) — per-type visibility is filtered server-side
        // via TenantNotificationAudienceCatalog, so Member-tier callers transparently see only the
        // notifications they are entitled to.
        // Dismiss endpoints stay TenantAdminOrGA: dismissal is currently tenant-shared (clearing for
        // one user clears for all). When per-user dismiss lands, dismiss can drop to MemberRead too.
        new("GET",    "notifications",                                   EndpointPolicy.MemberRead, TenantScoping.Jwt),
        new("POST",   "notifications/dismiss-all",                       EndpointPolicy.TenantAdminOrGA, TenantScoping.Jwt),
        new("POST",   "notifications/{notificationId}/dismiss",          EndpointPolicy.TenantAdminOrGA, TenantScoping.Jwt),
        new("GET",    "global/ops-events",                              EndpointPolicy.GlobalAdminOnly),
    };

    /// <summary>
    /// All registered policy entries. Used by completeness tests.
    /// </summary>
    public static IReadOnlyList<EndpointPolicyEntry> Entries => _entries;

    /// <summary>
    /// Finds the policy for a given HTTP method and request path.
    /// Path should include /api/ prefix (e.g., "/api/sessions/abc-123/events").
    /// Returns null if no matching entry is found (fail-closed: caller should deny).
    /// </summary>
    public static EndpointPolicyEntry? FindPolicy(string httpMethod, string path)
    {
        // Strip /api/ prefix for matching against route templates
        var normalizedPath = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(5)
            : path;

        var method = httpMethod.ToUpperInvariant();

        // Find the best match. Priority:
        // 1. Literal (no {param}) matches over parameterized ones
        // 2. Among same type, longer template wins (more specific)
        EndpointPolicyEntry? bestMatch = null;
        var bestIsLiteral = false;

        foreach (var entry in _entries)
        {
            if (entry.HttpMethod != method)
                continue;

            if (!entry.RouteRegex.IsMatch(normalizedPath))
                continue;

            var isLiteral = !entry.RouteTemplate.Contains('{');

            // Literal match always beats parameterized match
            if (isLiteral && !bestIsLiteral)
            {
                bestMatch = entry;
                bestIsLiteral = true;
            }
            else if (isLiteral == bestIsLiteral)
            {
                // Same category: prefer longer (more specific) template
                if (bestMatch == null || entry.RouteTemplate.Length > bestMatch.RouteTemplate.Length)
                {
                    bestMatch = entry;
                    bestIsLiteral = isLiteral;
                }
            }
        }

        return bestMatch;
    }
}
