using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Shared.Models
{
    // -------------------------------------------------------------------------------------
    // Typed wire DTOs for the Metrics function folder (anonymous-object → typed migration).
    // Envelope classes implement IApiResponse; nested item classes stay flat and carry no
    // marker interface. Property declaration order IS the JSON key order.
    // DTOs of the two GeographicLocationSessions endpoints live in GeographicApiModels.cs.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Device enrollment-history envelope (GetDeviceHistory). <see cref="History"/> is absent
    /// as a NORMAL outcome (unknown device, junk serial, or every chain ref pruned).
    /// </summary>
    // Declaration order == wire order.
    public class GetDeviceHistoryResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>Absent when the device has no history row — the banner simply stays hidden.</summary>
        public DeviceHistory? History { get; set; }

        /// <summary>
        /// The requesting session's attempt number within its journey; absent without a
        /// <c>?sessionId=</c> parameter, when the session is unknown, or when the position
        /// cannot be computed (fail-soft, never a guessed position).
        /// </summary>
        public int? AttemptNumber { get; set; }
    }

    /// <summary>
    /// Landing-page platform stats envelope (GetPlatformStats, unauthenticated). When no
    /// stats row has been computed yet, the zero shape is served: all counters 0 with
    /// <see cref="TotalSignedUpTenants"/> and <see cref="LastUpdated"/> absent.
    /// </summary>
    // Declaration order == wire order.
    public class GetPlatformStatsResponse : IApiResponse
    {
        public long TotalEnrollments { get; set; }
        public long TotalUsers { get; set; }
        public long TotalTenants { get; set; }

        /// <summary>Absent on the not-yet-computed zero shape.</summary>
        public long? TotalSignedUpTenants { get; set; }

        public long UniqueDeviceModels { get; set; }
        public long TotalEventsProcessed { get; set; }
        public long SuccessfulEnrollments { get; set; }
        public long IssuesDetected { get; set; }

        /// <summary>Always absent today: the computed shape never sets it, the zero shape sets null.</summary>
        public DateTime? LastUpdated { get; set; }
    }

    /// <summary>
    /// Per-session time-attribution envelope (GetSessionTimeAttribution). A missing breakdown
    /// is a NORMAL outcome (pre-feature session, non-terminal, Incomplete — no wall clock).
    /// </summary>
    // Declaration order == wire order.
    public class GetSessionTimeAttributionResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>Absent when no breakdown row exists — the UI simply omits the lane.</summary>
        public SessionTimeBreakdown? Breakdown { get; set; }
    }

    /// <summary>
    /// Self-service MCP/API usage envelope (GetMyMcpUsage): the caller's own usage records
    /// plus resolved plan and quota state. The MCP server paginates over the
    /// <c>records</c> key — its name is wire-critical.
    /// </summary>
    // Declaration order == wire order.
    public class GetMyMcpUsageResponse : IApiResponse
    {
        public string UserId { get; set; } = default!;

        /// <summary>Absent when the token carries no UPN claim.</summary>
        public string? Upn { get; set; }

        /// <summary>Per-user plan override from the caller's own whitelist row; absent without one.</summary>
        public string? UsagePlan { get; set; }

        /// <summary>Resolved effective plan (per-user override → tenant edition).</summary>
        public string EffectivePlan { get; set; } = default!;

        public McpUsageQuotaNode Quota { get; set; } = default!;
        public IReadOnlyList<UserUsageRecord> Records { get; set; } = default!;
    }

    /// <summary>Effective quota state nested in <see cref="GetMyMcpUsageResponse"/>.</summary>
    // Declaration order == wire order.
    public class McpUsageQuotaNode
    {
        public int DailyLimit { get; set; }
        public int MonthlyLimit { get; set; }
        public long DailyUsed { get; set; }
        public long MonthlyUsed { get; set; }
        public DateTime ResetUtc { get; set; }
    }

    /// <summary>
    /// Per-user MCP/API usage envelope (GetMcpUserUsage). Non-global callers only receive
    /// the records attributed to their own tenant; a foreign oid and an unknown oid are
    /// indistinguishable (both 200 with empty <c>records</c>). The MCP server paginates over
    /// the <c>records</c> key — its name is wire-critical.
    /// </summary>
    // Declaration order == wire order.
    public class GetMcpUserUsageResponse : IApiResponse
    {
        public string UserId { get; set; } = default!;
        public IReadOnlyList<UserUsageRecord> Records { get; set; } = default!;
    }

    /// <summary>
    /// Global per-tenant MCP/API usage envelope (GetGlobalMcpUsage). The MCP server paginates
    /// over the <c>records</c> key — its name is wire-critical.
    /// </summary>
    // Declaration order == wire order.
    public class GetGlobalMcpUsageResponse : IApiResponse
    {
        /// <summary>Echo of the request filter; absent when the caller passed no tenantId.</summary>
        public string? TenantId { get; set; }

        public IReadOnlyList<UserUsageRecord> Records { get; set; } = default!;
    }

    /// <summary>
    /// Global daily MCP/API usage summaries envelope (GetGlobalMcpUsageDaily). The MCP server
    /// paginates over the <c>summaries</c> key — its name is wire-critical.
    /// </summary>
    // Declaration order == wire order.
    public class GetGlobalMcpUsageDailyResponse : IApiResponse
    {
        /// <summary>Echo of the request filter; absent when the caller passed no tenantId.</summary>
        public string? TenantId { get; set; }

        public IReadOnlyList<UserUsageDailySummary> Summaries { get; set; } = default!;
    }

    /// <summary>
    /// Per-tenant session-status tally envelope shared by MetricsSummary and
    /// MetricsSummaryGlobal.
    /// </summary>
    // Declaration order == wire order.
    public class MetricsSummaryResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<MetricsSummaryTenantItem> Summary { get; set; } = default!;
        public int WindowDays { get; set; }
    }

    /// <summary>
    /// One per-tenant status tally in <see cref="MetricsSummaryResponse"/>. WindowDays is
    /// repeated per item (envelope carries it too — historical wire shape, kept for parity).
    /// </summary>
    // Declaration order == wire order.
    public class MetricsSummaryTenantItem
    {
        public string TenantId { get; set; } = default!;
        public int TotalSessions { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int InProgress { get; set; }
        public int Pending { get; set; }
        public int Stalled { get; set; }
        public int AwaitingUser { get; set; }
        public int Incomplete { get; set; }
        public int Other { get; set; }

        /// <summary>Failed over the TERMINAL outcomes only (Succeeded + Failed), percent rounded to one decimal; 0 without terminal sessions.</summary>
        public double FailureRate { get; set; }

        public int WindowDays { get; set; }
    }

    /// <summary>
    /// Session ids where a rule produced a result within the window (GetRuleHitSessions) —
    /// powers the dashboard's <c>?ruleId=</c> deep link.
    /// </summary>
    // Declaration order == wire order.
    public class GetRuleHitSessionsResponse : IApiResponse
    {
        public string RuleId { get; set; } = default!;
        public int Days { get; set; }
        public IReadOnlyList<string> SessionIds { get; set; } = default!;

        /// <summary>True when the result hit the hit-set cap (2000) — the list is a lower bound.</summary>
        public bool Truncated { get; set; }
    }
}
