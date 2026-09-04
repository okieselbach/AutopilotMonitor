using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Success body of GET auth/me: the caller's resolved identity, roles and effective
    /// entitlement flags. Blocked outcomes (TenantSuspended / PendingActivation) are error
    /// shapes and stay anonymous by design.
    /// </summary>
    // Declaration order == wire order.
    public class AuthMeResponse : IApiResponse
    {
        public string TenantId { get; set; } = default!;
        public string Upn { get; set; } = default!;
        public string DisplayName { get; set; } = default!;
        public string ObjectId { get; set; } = default!;
        public bool IsGlobalAdmin { get; set; }
        public bool IsGlobalReader { get; set; }
        public bool IsTenantAdmin { get; set; }

        /// <summary>True when the caller holds delegated ("MSP") assignments to other tenants.</summary>
        public bool IsDelegated { get; set; }

        /// <summary>The OTHER tenants this caller may manage; empty for non-delegated callers.</summary>
        public IReadOnlyCollection<string> DelegatedTenantIds { get; set; } = default!;

        /// <summary>Tenant role (Admin / Operator / Viewer); the key is omitted for a roleless caller.</summary>
        public string? Role { get; set; }

        public bool CanManageBootstrapTokens { get; set; }
        public bool HasMcpAccess { get; set; }

        /// <summary>"primary" or "legacy" — which app registration this tenant is homed on.</summary>
        public string HomedApp { get; set; } = default!;

        public bool BootstrapTokenEnabled { get; set; }
        public bool UnrestrictedModeEnabled { get; set; }
    }

    /// <summary>
    /// Response of GET auth/is-global-admin: whether the caller holds the Global Admin
    /// platform role, echoing the caller's UPN.
    /// </summary>
    // Declaration order == wire order.
    public class IsGlobalAdminResponse : IApiResponse
    {
        public bool IsGlobalAdmin { get; set; }

        /// <summary>Caller's UPN from the token, or null when the token carries no UPN claim — the key is omitted when null.</summary>
        public string? Upn { get; set; }
    }

    /// <summary>
    /// Response of GET auth/global-admins: every Global Admin/Reader row.
    /// </summary>
    // Declaration order == wire order.
    public class GetGlobalAdminsResponse : IApiResponse
    {
        public IReadOnlyList<GlobalAdminRow> Admins { get; set; } = default!;
    }

    /// <summary>
    /// Response of POST auth/global-admins (201): the created row.
    /// </summary>
    // Declaration order == wire order.
    public class AddGlobalAdminResponse : IApiResponse
    {
        public GlobalAdminRow Admin { get; set; } = default!;
    }

    /// <summary>
    /// One Global Admin/Reader row on the wire. Deliberately NOT the storage entity: the
    /// ITableEntity keys (partitionKey/rowKey/eTag/timestamp) that the pre-2026-08-31 wire
    /// carried are storage internals and were dropped from the contract (no consumer read them).
    /// </summary>
    // Declaration order == wire order.
    public class GlobalAdminRow
    {
        /// <summary>User Principal Name (lowercase).</summary>
        public string Upn { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public DateTime AddedDate { get; set; }
        public string AddedBy { get; set; } = string.Empty;
        /// <summary>"GlobalAdmin" or "GlobalReader" (legacy empty rows are normalized to GlobalAdmin).</summary>
        public string Role { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response of GET health: liveness probe plus the backend build identity.
    /// </summary>
    // Declaration order == wire order.
    public class HealthCheckResponse : IApiResponse
    {
        public string Status { get; set; } = default!;
        public string Service { get; set; } = default!;
        public DateTime Timestamp { get; set; }
        public string Version { get; set; } = default!;
        public string CommitHash { get; set; } = default!;
        public DateTime BuildUtc { get; set; }
    }

    /// <summary>
    /// Response of GET health/detailed: the full system health report (always 200; per-check
    /// status is in the body). Non-GA callers get a filtered check list.
    /// </summary>
    // Declaration order == wire order.
    public class DetailedHealthCheckResponse : IApiResponse
    {
        public string Service { get; set; } = default!;
        public DateTime Timestamp { get; set; }
        public string OverallStatus { get; set; } = default!;
        public IReadOnlyList<HealthCheck> Checks { get; set; } = default!;
        public string Version { get; set; } = default!;
        public string CommitHash { get; set; } = default!;
        public DateTime BuildUtc { get; set; }
    }

    /// <summary>
    /// Response of GET health/mcp: the standalone MCP-server reachability probe.
    /// </summary>
    // Declaration order == wire order.
    public class McpHealthCheckResponse : IApiResponse
    {
        public DateTime Timestamp { get; set; }
        public HealthCheck Check { get; set; } = default!;
    }

    /// <summary>
    /// One health check result inside GET health/detailed / health/mcp. Details is a
    /// heterogeneous per-check bag by design (endpoint URLs only for Global Admins);
    /// a null Details key is omitted (WhenWritingNull).
    /// </summary>
    // Declaration order == wire order.
    public class HealthCheck
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Status { get; set; } = "unknown";
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object>? Details { get; set; }
    }

    /// <summary>
    /// Body of GET auth/mcp (200 when allowed, 403 when denied — same shape on both).
    /// The four platform/delegated keys are emitted ONLY when the caller actually holds the
    /// tier (null ⇒ key omitted): ordinary tenant users must not learn a platform tier exists,
    /// and <see cref="IsGlobalAdmin"/> is only ever emitted as literal <c>true</c>.
    /// </summary>
    // Declaration order == wire order (matches the former Dictionary insertion order).
    public class CheckMcpAccessResponse : IApiResponse
    {
        public bool Allowed { get; set; }
        public string Upn { get; set; } = default!;
        public string AccessGrant { get; set; } = default!;
        public string Reason { get; set; } = default!;

        /// <summary>Back-compat / write-tier hint for the MCP access-guard; true or omitted, never false.</summary>
        public bool? IsGlobalAdmin { get; set; }

        /// <summary>"GlobalAdmin" | "GlobalReader"; omitted without a platform role.</summary>
        public string? GlobalRole { get; set; }

        /// <summary>Managed tenant ids (lowercase) of a delegated (MSP) caller; omitted otherwise.</summary>
        public IReadOnlyCollection<string>? DelegatedTenantIds { get; set; }

        /// <summary>"DelegatedAdmin" | "DelegatedReader"; omitted without delegated scope.</summary>
        public string? DelegatedRole { get; set; }
    }

    /// <summary>
    /// Response of GET global/mcp-users: the effective MCP access policy name plus every
    /// whitelist row.
    /// </summary>
    // Declaration order == wire order.
    public class GetMcpUsersResponse : IApiResponse
    {
        public string Policy { get; set; } = default!;
        public IReadOnlyList<McpUserEntry> Users { get; set; } = default!;
    }

    /// <summary>
    /// Response of POST global/mcp-users (201): the created whitelist row.
    /// </summary>
    // Declaration order == wire order.
    public class AddMcpUserResponse : IApiResponse
    {
        public McpUserEntry User { get; set; } = default!;
    }

    /// <summary>
    /// Response of PATCH global/mcp-users/{upn}/usage-plan: the UPN and the plan now in
    /// effect ("(inherit)" when cleared to the tenant default).
    /// </summary>
    // Declaration order == wire order.
    public class SetMcpUserUsagePlanResponse : IApiResponse
    {
        public string Upn { get; set; } = default!;
        public string UsagePlan { get; set; } = default!;
    }

    /// <summary>
    /// Response of POST realtime/negotiate: exactly the shape the @microsoft/signalr client's
    /// negotiate protocol expects.
    /// </summary>
    // Declaration order == wire order.
    public class SignalRNegotiateResponse : IApiResponse
    {
        public string Url { get; set; } = default!;
        public string AccessToken { get; set; } = default!;
    }

    /// <summary>
    /// 429 body written by McpQuotaEnforcementMiddleware when the per-user MCP daily/monthly
    /// quota is exhausted. Carries the error-envelope prefix (error, code=QuotaExceeded,
    /// correlationId); <c>quotaExceeded</c> is the discriminator the MCP error handler keys on.
    /// </summary>
    // Declaration order == wire order.
    public class McpQuotaExceededResponse : IApiErrorResponse
    {
        /// <summary>The full quota message (whose window, which plan, when it resets).</summary>
        public string Error { get; set; } = default!;
        public string Code { get; set; } = Constants.ApiErrorCodes.QuotaExceeded;
        public string CorrelationId { get; set; } = string.Empty;
        public bool QuotaExceeded { get; set; }
        public string Plan { get; set; } = default!;

        /// <summary>Which window was exceeded ("daily"/"monthly") — always set on the blocked path.</summary>
        public string? Scope { get; set; }

        /// <summary>Whose budget was exceeded: "user" (the caller's own plan) or "tenant" (the organization-wide windows).</summary>
        public string Level { get; set; } = default!;

        /// <summary>Limit of the exceeded window.</summary>
        public int Limit { get; set; }

        /// <summary>Used count of the exceeded window.</summary>
        public long Used { get; set; }

        /// <summary>Reset time of the exceeded window, pre-formatted "yyyy-MM-ddTHH:mm:ssZ".</summary>
        public string ResetUtc { get; set; } = default!;

        /// <summary>
        /// The MANAGED tenant whose organization windows blocked a delegated (MSP) read — its plan governs
        /// the budget, not the caller's. Absent when the caller's own tenant/plan was exceeded and on the
        /// all-managed-tenants-exhausted aggregate block.
        /// </summary>
        public string? TargetTenantId { get; set; }
    }
}
