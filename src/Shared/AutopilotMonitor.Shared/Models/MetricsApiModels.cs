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

    /// <summary>
    /// Effective quota state nested in <see cref="GetMyMcpUsageResponse"/>: the caller's own windows and
    /// the organization-wide windows of their HOME tenant (shared by every member; 0 = unlimited). Every
    /// MCP request the caller makes — reads into managed tenants included — draws on these windows. The
    /// limits already contain the growth from purchased delegation slots; the slot fields let a client show
    /// the breakdown (base = limit − slots × slot value) and are absent when the tenant has no purchased slot.
    /// </summary>
    // Declaration order == wire order.
    public class McpUsageQuotaNode
    {
        public int DailyLimit { get; set; }
        public int MonthlyLimit { get; set; }
        public long DailyUsed { get; set; }
        public long MonthlyUsed { get; set; }
        public DateTime ResetUtc { get; set; }
        public string TenantPlan { get; set; } = default!;
        public int TenantDailyLimit { get; set; }
        public int TenantMonthlyLimit { get; set; }
        public long TenantDailyUsed { get; set; }
        public long TenantMonthlyUsed { get; set; }
        /// <summary>Delegation slots bought beyond the edition's included ones; absent when zero.</summary>
        public int? PurchasedDelegatedSlots { get; set; }
        /// <summary>Per-user daily growth per purchased slot; absent when no slot was purchased.</summary>
        public int? SlotDailyLimit { get; set; }
        public int? SlotMonthlyLimit { get; set; }
        /// <summary>Organization daily growth per purchased slot; absent when no slot was purchased.</summary>
        public int? SlotTenantDailyLimit { get; set; }
        public int? SlotTenantMonthlyLimit { get; set; }
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
    /// Organization-wide MCP usage by user for ONE tenant — the caller's own (GetMcpOrganizationUsage) or the
    /// tenant a Global Admin / Global Reader names (GetGlobalMcpOrganizationUsage): every account of the
    /// tenant whose requests were charged to its organization budget (a member's reads into managed tenants
    /// included), plus the tenant's organization windows. Built from the tenant's organization counters.
    /// </summary>
    // Declaration order == wire order.
    public class GetMcpOrganizationUsageResponse : IApiResponse
    {
        public string TenantId { get; set; } = default!;
        /// <summary>Effective range start (yyyyMMdd).</summary>
        public string DateFrom { get; set; } = default!;
        /// <summary>Effective range end (yyyyMMdd, inclusive).</summary>
        public string DateTo { get; set; } = default!;
        public IReadOnlyList<McpOrganizationUsageItem> Users { get; set; } = default!;
        /// <summary>The tenant's organization windows (plan, limits, today / this month) from the same counters.</summary>
        public McpOrganizationQuotaNode Quota { get; set; } = default!;
        /// <summary>Requests charged to the tenant per day inside the range, oldest first; days without requests are absent.</summary>
        public IReadOnlyList<McpOrganizationDailyItem> Daily { get; set; } = default!;
    }

    /// <summary>One day of the tenant's organization budget, nested in <see cref="GetMcpOrganizationUsageResponse"/>.</summary>
    // Declaration order == wire order.
    public class McpOrganizationDailyItem
    {
        /// <summary>yyyyMMdd.</summary>
        public string Date { get; set; } = default!;
        public long Requests { get; set; }
    }

    /// <summary>
    /// The organization-wide windows of the tenant in <see cref="GetMcpOrganizationUsageResponse"/>: its tenant
    /// plan (tenant-wide override, else edition) with the limits (0 = unlimited; purchased-slot growth included)
    /// and the counters over every account charged to the tenant. The slot fields carry the breakdown and are
    /// absent when the tenant has no purchased delegation slot.
    /// </summary>
    // Declaration order == wire order.
    public class McpOrganizationQuotaNode
    {
        public string TenantPlan { get; set; } = default!;
        public int DailyLimit { get; set; }
        public int MonthlyLimit { get; set; }
        public long DailyUsed { get; set; }
        public long MonthlyUsed { get; set; }
        /// <summary>Delegation slots bought beyond the edition's included ones; absent when zero.</summary>
        public int? PurchasedDelegatedSlots { get; set; }
        /// <summary>Organization daily growth per purchased slot; absent when no slot was purchased.</summary>
        public int? SlotDailyLimit { get; set; }
        public int? SlotMonthlyLimit { get; set; }
    }

    /// <summary>One account's share of the organization budget, nested in <see cref="GetMcpOrganizationUsageResponse"/>.</summary>
    // Declaration order == wire order.
    public class McpOrganizationUsageItem
    {
        public string UserId { get; set; } = default!;
        /// <summary>Absent for rows written before the UPN was recorded.</summary>
        public string? UserPrincipalName { get; set; }
        public long RequestsToday { get; set; }
        public long RequestsThisMonth { get; set; }
        public long RequestsInRange { get; set; }
        /// <summary>Absent for rows written before the timestamp was recorded.</summary>
        public DateTime? LastRequestAt { get; set; }
    }

    /// <summary>
    /// The all-tenant MCP usage overview of the Global Admin metrics page (GetGlobalMcpUsageOverview) over whole UTC
    /// days: tenants with their accounts and quota state, the tool ranking, the quota refusals, the per-minute peaks
    /// and the daily trend. "Calls" are tool calls (request telemetry, one MCP correlation id per call); "requests"
    /// are backend requests charged to the quota (the quota counters). What a telemetry store did not answer is
    /// empty and named in <see cref="Telemetry"/>.
    /// </summary>
    // Declaration order == wire order.
    public class GetGlobalMcpUsageOverviewResponse : IApiResponse
    {
        /// <summary>First day of the window (yyyyMMdd, UTC).</summary>
        public string DateFrom { get; set; } = default!;
        /// <summary>Last day of the window (yyyyMMdd, UTC) — today.</summary>
        public string DateTo { get; set; } = default!;
        /// <summary>False when Global Admins and Global Readers are left out of every number.</summary>
        public bool IncludesPlatformAdmins { get; set; }
        public McpUsageTelemetryStatus Telemetry { get; set; } = default!;
        /// <summary>The window's totals over the lists below, computed once for every consumer.</summary>
        public McpUsageTotalsNode Totals { get; set; } = default!;
        /// <summary>One item per day of the window, oldest first.</summary>
        public IReadOnlyList<McpUsageDayItem> Daily { get; set; } = default!;
        /// <summary>Every tenant with MCP use in the window, most calls first.</summary>
        public IReadOnlyList<McpUsageTenantItem> Tenants { get; set; } = default!;
        /// <summary>Every tool called in the window, most calls first.</summary>
        public IReadOnlyList<McpUsageToolItem> Tools { get; set; } = default!;
        /// <summary>Refusals by the daily or monthly quota, one item per day, account and window, newest first.</summary>
        public IReadOnlyList<McpUsageQuotaHitItem> QuotaHits { get; set; } = default!;
        public McpUsageMinuteLimits MinuteLimits { get; set; } = default!;
    }

    /// <summary>Which telemetry store answered, nested in <see cref="GetGlobalMcpUsageOverviewResponse"/>.</summary>
    // Declaration order == wire order.
    public class McpUsageTelemetryStatus
    {
        /// <summary>The backend's request telemetry answered: calls, reach, routes, refusals and per-minute peaks are filled.</summary>
        public bool RequestsAvailable { get; set; }
        /// <summary>The MCP server's log answered: tool quality, MCP-only tools, the MCP limit and its refusals are filled.</summary>
        public bool McpLogAvailable { get; set; }
        /// <summary>First day (yyyyMMdd) the MCP log covers when that lies after the window start; absent when it covers the window.</summary>
        public string? McpLogFrom { get; set; }
        /// <summary>True when a store cut a result (size cap, shard timeout), so its numbers are a lower bound; absent otherwise.</summary>
        public bool? Partial { get; set; }
    }

    /// <summary>
    /// The overview's totals, nested in <see cref="GetGlobalMcpUsageOverviewResponse"/>. "Calls" are tool calls,
    /// "requests" are backend requests charged to the quota; an account is a person or a service principal of one
    /// tenant.
    /// </summary>
    // Declaration order == wire order.
    public class McpUsageTotalsNode
    {
        /// <summary>Tool calls in the window, MCP-only tools included; absent when the request telemetry did not answer.</summary>
        public long? Calls { get; set; }
        /// <summary>Calls of tools that run in the MCP server alone (part of <see cref="Calls"/>, attributed to no account); absent without the MCP log.</summary>
        public long? McpOnlyCalls { get; set; }
        /// <summary>Tools called in the window.</summary>
        public int Tools { get; set; }
        public long Requests { get; set; }
        public int Tenants { get; set; }
        public int Accounts { get; set; }
        /// <summary>Accounts that are service principals (<c>app:&lt;client-id&gt;</c>).</summary>
        public int ServicePrincipals { get; set; }
        /// <summary>Accounts with at least one quota refusal in the window (daily or monthly, own or organization window).</summary>
        public int QuotaHitAccounts { get; set; }
        /// <summary>Accounts with at least one per-minute refusal in the window (MCP server or backend).</summary>
        public int MinuteRefusedAccounts { get; set; }
        /// <summary>Accounts that met any limit in the window — the union of the two above.</summary>
        public int LimitAccounts { get; set; }
    }

    /// <summary>One day of the overview trend.</summary>
    // Declaration order == wire order.
    public class McpUsageDayItem
    {
        /// <summary>yyyyMMdd.</summary>
        public string Date { get; set; } = default!;
        public long Calls { get; set; }
        public long Requests { get; set; }
        /// <summary>Accounts with a call or a charged request that day.</summary>
        public int Accounts { get; set; }
        /// <summary>The day's calls by the usage plan of the caller's tenant, most first.</summary>
        public IReadOnlyList<McpUsagePlanCallsItem> CallsByPlan { get; set; } = default!;
    }

    /// <summary>Calls of one usage plan on one day.</summary>
    // Declaration order == wire order.
    public class McpUsagePlanCallsItem
    {
        public string Plan { get; set; } = default!;
        public long Calls { get; set; }
    }

    /// <summary>One tenant of the overview with its organization windows and its accounts.</summary>
    // Declaration order == wire order.
    public class McpUsageTenantItem
    {
        public string TenantId { get; set; } = default!;
        /// <summary>The tenant's domain from its configuration; absent when it has none or the configuration was unreadable.</summary>
        public string? DomainName { get; set; }
        /// <summary>The organization windows (tenant plan, limits, today / this month) from the tenant's quota counters.</summary>
        public McpOrganizationQuotaNode Quota { get; set; } = default!;
        public long Calls { get; set; }
        public long Requests { get; set; }
        /// <summary>Quota refusals of the tenant's accounts and of the organization window in the window.</summary>
        public int QuotaHits { get; set; }
        /// <summary>Absent when no counter row carries a timestamp.</summary>
        public DateTime? LastRequestAt { get; set; }
        /// <summary>Accounts with MCP use in the window, most calls first.</summary>
        public IReadOnlyList<McpUsageAccountItem> Accounts { get; set; } = default!;
    }

    /// <summary>One account (person or service principal) of a tenant in the overview.</summary>
    // Declaration order == wire order.
    public class McpUsageAccountItem
    {
        /// <summary>Entra object id; absent for an account known only from request telemetry (every request refused).</summary>
        public string? UserId { get; set; }
        /// <summary>UPN, or <c>app:&lt;client-id&gt;</c> for a service principal.</summary>
        public string Principal { get; set; } = default!;
        /// <summary>Role on its latest request (Admin, Operator, Viewer, DelegatedReader, GlobalReader, GlobalAdmin); absent without request telemetry.</summary>
        public string? Role { get; set; }
        /// <summary>A Global Admin or Global Reader; listed only when platform admins are included.</summary>
        public bool IsPlatformAdmin { get; set; }
        /// <summary>The account's plan and windows as its next request meets them; absent without an object id.</summary>
        public McpUsageAccountQuotaNode? Quota { get; set; }
        public long Calls { get; set; }
        public long Requests { get; set; }
        /// <summary>The day (yyyyMMdd) with the most charged requests in the window; absent without requests.</summary>
        public string? PeakDay { get; set; }
        /// <summary>Charged requests on <see cref="PeakDay"/>; absent without requests.</summary>
        public long? PeakDayRequests { get; set; }
        /// <summary>Days on which the account met a quota refusal of its own window.</summary>
        public int QuotaHits { get; set; }
        /// <summary>The account's most called tools, at most three, most first.</summary>
        public IReadOnlyList<McpUsageToolCallsItem> TopTools { get; set; } = default!;
        /// <summary>Absent when neither the counters nor the telemetry carry a timestamp.</summary>
        public DateTime? LastRequestAt { get; set; }
    }

    /// <summary>The account's own windows (0 = unlimited; purchased-slot growth included).</summary>
    // Declaration order == wire order.
    public class McpUsageAccountQuotaNode
    {
        /// <summary>The account's usage plan: its personal override, else the tenant plan.</summary>
        public string Plan { get; set; } = default!;
        public int DailyLimit { get; set; }
        public int MonthlyLimit { get; set; }
        public long DailyUsed { get; set; }
        public long MonthlyUsed { get; set; }
    }

    /// <summary>Calls of one tool by one account.</summary>
    // Declaration order == wire order.
    public class McpUsageToolCallsItem
    {
        public string Tool { get; set; } = default!;
        public long Calls { get; set; }
    }

    /// <summary>One MCP tool in the overview ranking.</summary>
    // Declaration order == wire order.
    public class McpUsageToolItem
    {
        public string Tool { get; set; } = default!;
        public long Calls { get; set; }
        /// <summary>Accounts that called it; absent for a tool that never reaches the backend.</summary>
        public int? Accounts { get; set; }
        /// <summary>Tenants whose accounts called it; absent for a tool that never reaches the backend.</summary>
        public int? Tenants { get; set; }
        /// <summary>Backend requests one served call costs the quota on average; 0 for a tool that runs in the MCP server alone.</summary>
        public double RequestsPerCall { get; set; }
        /// <summary>Share of calls (0–1) that ended as an error, soft errors included; absent without the MCP log.</summary>
        public double? ErrorRate { get; set; }
        /// <summary>95th-percentile duration of a call; absent without the MCP log.</summary>
        public long? P95DurationMs { get; set; }
        /// <summary>Mean size of the answer in characters; absent without the MCP log.</summary>
        public long? AvgResultChars { get; set; }
        /// <summary>Calls whose page exceeded the tool's size cap; absent without the MCP log.</summary>
        public long? OverCapCalls { get; set; }
        /// <summary>The backend routes its calls requested, most requests first.</summary>
        public IReadOnlyList<McpUsageRouteItem> Routes { get; set; } = default!;
    }

    /// <summary>One backend route of a tool: method plus path with ids as <c>{id}</c>.</summary>
    // Declaration order == wire order.
    public class McpUsageRouteItem
    {
        public string Route { get; set; } = default!;
        public long Requests { get; set; }
    }

    /// <summary>Quota refusals of one account (level user) or organization (level tenant) on one day and window.</summary>
    // Declaration order == wire order.
    public class McpUsageQuotaHitItem
    {
        /// <summary>yyyyMMdd.</summary>
        public string Date { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        /// <summary>The refused account; absent when the request row carried no principal.</summary>
        public string? Principal { get; set; }
        /// <summary><c>user</c> (the account's window) or <c>tenant</c> (the organization's).</summary>
        public string Level { get; set; } = default!;
        /// <summary><c>daily</c> or <c>monthly</c>.</summary>
        public string Scope { get; set; } = default!;
        public string Plan { get; set; } = default!;
        /// <summary>The window's limit; absent when the refusal line carried none.</summary>
        public long? Limit { get; set; }
        /// <summary>The window's count at the first refusal of the day; absent when the refusal line carried none.</summary>
        public long? Used { get; set; }
        public long Refused { get; set; }
        public DateTime FirstAt { get; set; }
        public DateTime LastAt { get; set; }
    }

    /// <summary>The two per-minute limits an MCP account meets and how close the accounts came to them.</summary>
    // Declaration order == wire order.
    public class McpUsageMinuteLimits
    {
        /// <summary>MCP messages per minute the MCP server admits per account, from its newest start line; absent until a server reported it.</summary>
        public int? McpServerLimit { get; set; }
        /// <summary>Every account with request telemetry in the window, closest to a limit first.</summary>
        public IReadOnlyList<McpUsageMinuteItem> Accounts { get; set; } = default!;
    }

    /// <summary>One account's busiest 60 seconds against both per-minute limits, and its refusals.</summary>
    // Declaration order == wire order.
    public class McpUsageMinuteItem
    {
        public string TenantId { get; set; } = default!;
        public string Principal { get; set; } = default!;
        /// <summary>
        /// How close the account came to a per-minute limit, in whole percent: the higher of its call peak against the MCP
        /// server limit and its request peak against its backend limit (the list is ordered by it); 0 when neither limit is known.
        /// </summary>
        public int SharePercent { get; set; }
        /// <summary>Most tool calls the account started inside 60 seconds (measured in 10-second steps).</summary>
        public long PeakCalls { get; set; }
        public DateTime? PeakCallsAt { get; set; }
        /// <summary>Messages the MCP server refused because of its per-minute limit.</summary>
        public long McpRefused { get; set; }
        public DateTime? McpLastRefusedAt { get; set; }
        /// <summary>Most backend requests the account sent inside 60 seconds (measured in 10-second steps).</summary>
        public long PeakRequests { get; set; }
        public DateTime? PeakRequestsAt { get; set; }
        /// <summary>Backend requests per minute the account may send in its home tenant (override, else the default raised to the edition floor).</summary>
        public int BackendLimit { get; set; }
        /// <summary>Requests the backend refused because of its per-minute limit.</summary>
        public long BackendRefused { get; set; }
        public DateTime? BackendLastRefusedAt { get; set; }
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
