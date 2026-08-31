using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Shared.Models
{
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
    /// Response of GET auth/global-admins: every Global Admin/Reader row. Items are
    /// <c>GlobalAdminEntity</c> table entities (backend-project type, serialized by runtime
    /// type — includes the ITableEntity keys, exactly as the anonymous site did).
    /// </summary>
    // Declaration order == wire order.
    public class GetGlobalAdminsResponse : IApiResponse
    {
        public IReadOnlyList<object> Admins { get; set; } = default!;
    }

    /// <summary>
    /// Response of POST auth/global-admins (201): the created <c>GlobalAdminEntity</c>
    /// (backend-project type, serialized by runtime type).
    /// </summary>
    // Declaration order == wire order.
    public class AddGlobalAdminResponse : IApiResponse
    {
        public object Admin { get; set; } = default!;
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
    /// status is in the body). Items are <c>HealthCheck</c> objects (backend-project type,
    /// serialized by runtime type); non-GA callers get a filtered list.
    /// </summary>
    // Declaration order == wire order.
    public class DetailedHealthCheckResponse : IApiResponse
    {
        public string Service { get; set; } = default!;
        public DateTime Timestamp { get; set; }
        public string OverallStatus { get; set; } = default!;
        public IReadOnlyList<object> Checks { get; set; } = default!;
        public string Version { get; set; } = default!;
        public string CommitHash { get; set; } = default!;
        public DateTime BuildUtc { get; set; }
    }

    /// <summary>
    /// Response of GET health/mcp: the standalone MCP-server reachability probe. The check is
    /// a <c>HealthCheck</c> object (backend-project type, serialized by runtime type).
    /// </summary>
    // Declaration order == wire order.
    public class McpHealthCheckResponse : IApiResponse
    {
        public DateTime Timestamp { get; set; }
        public object Check { get; set; } = default!;
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
    /// quota is exhausted (structurally a success shape: first key is quotaExceeded).
    /// </summary>
    // Declaration order == wire order.
    public class McpQuotaExceededResponse : IApiResponse
    {
        public bool QuotaExceeded { get; set; }
        public string Plan { get; set; } = default!;

        /// <summary>Which window was exceeded ("daily"/"monthly") — always set on the blocked path.</summary>
        public string? Scope { get; set; }

        /// <summary>Limit of the exceeded window.</summary>
        public int Limit { get; set; }

        /// <summary>Used count of the exceeded window.</summary>
        public long Used { get; set; }

        /// <summary>Reset time of the exceeded window, pre-formatted "yyyy-MM-ddTHH:mm:ssZ".</summary>
        public string ResetUtc { get; set; } = default!;

        public string Message { get; set; } = default!;
    }
}
