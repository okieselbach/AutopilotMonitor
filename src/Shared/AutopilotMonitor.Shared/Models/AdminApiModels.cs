using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;

namespace AutopilotMonitor.Shared.Models
{
    // ---------------------------------------------------------------------------
    // Wire DTOs for the Functions/Admin folder (anonymous-object → typed migration).
    // Every envelope implements IApiResponse; nested item classes stay flat and
    // interface-free. Declaration order == wire order on every class.
    // ---------------------------------------------------------------------------

    /// <summary>One currently active web user as listed by GET global/presence.</summary>
    // Declaration order == wire order.
    public class ActiveUserItem
    {
        public string TenantId { get; set; } = default!;
        public string Upn { get; set; } = default!;
        public string UserRole { get; set; } = default!;
        public DateTime LastSeen { get; set; }

        /// <summary>Whole seconds since <see cref="LastSeen"/>, floored at 0.</summary>
        public int SecondsAgo { get; set; }
    }

    /// <summary>Response of GET global/presence: users active within the requested window.</summary>
    // Declaration order == wire order.
    public class GetActiveUsersResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int WindowMinutes { get; set; }
        public int ActiveCount { get; set; }
        public IReadOnlyList<ActiveUserItem> Users { get; set; } = default!;
    }

    /// <summary>
    /// Shared response of the audit-log listing endpoints (GET audit/logs and
    /// GET global/audit/logs), paged and non-paged: the non-paged variant leaves
    /// <see cref="NextLink"/> null so the key is absent, exactly like the old literal.
    /// </summary>
    // Declaration order == wire order.
    public class AuditLogListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<AuditLogEntry> Logs { get; set; } = default!;

        /// <summary>Absolute-path link to the next page, or null on the last page / non-paged variant — the key is omitted when null.</summary>
        public string? NextLink { get; set; }
    }

    /// <summary>
    /// Response of GET global/ops-events, paged and non-paged: the non-paged variant leaves
    /// <see cref="NextLink"/> null so the key is absent, exactly like the old literal.
    /// </summary>
    // Declaration order == wire order.
    public class OpsEventListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<OpsEventEntry> Events { get; set; } = default!;

        /// <summary>Absolute-path link to the next page, or null on the last page / non-paged variant — the key is omitted when null.</summary>
        public string? NextLink { get; set; }
    }

    /// <summary>Response of GET config/{tenantId}/autopilot-device-validation/consent-url.</summary>
    // Declaration order == wire order.
    public class AutopilotConsentUrlResponse : IApiResponse
    {
        public string ConsentUrl { get; set; } = default!;

        /// <summary>True when the self-service app-homing funnel targeted the primary app registration.</summary>
        public bool WillAutoFlipHoming { get; set; }
    }

    /// <summary>Response of GET config/{tenantId}/autopilot-device-validation/consent-status.</summary>
    // Declaration order == wire order.
    public class AutopilotConsentStatusResponse : IApiResponse
    {
        public bool IsConsented { get; set; }

        /// <summary>Human-readable detail from the consent probe, or null — the key is omitted when null.</summary>
        public string? Message { get; set; }

        public bool HomingFlipped { get; set; }
    }

    /// <summary>Response of GET config/{tenantId}/autopilot-device-validation/access-check.</summary>
    // Declaration order == wire order.
    public class AutopilotAccessCheckResponse : IApiResponse
    {
        public bool AccessPresent { get; set; }

        /// <summary>True when the probe was inconclusive (timeout / Graph 5xx) — treat as unknown, not "absent".</summary>
        public bool IsTransient { get; set; }

        public string RequiredPermission { get; set; } = default!;
        public bool HomingFlipped { get; set; }
    }

    /// <summary>
    /// Shared response of the one-shot maintenance job triggers (POST maintenance/backfill-occurred-utc,
    /// POST maintenance/reclassify-legacy): the service's run report plus trigger attribution.
    /// </summary>
    // Declaration order == wire order.
    public class MaintenanceJobRunResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>
        /// The service-specific run report (BackfillResult / ReclassificationResult — types owned by the
        /// Functions project, so this stays <c>object</c>; serialized by runtime type, wire-identical).
        /// </summary>
        public object Result { get; set; } = default!;

        public string TriggeredBy { get; set; } = default!;
        public DateTime TriggeredAt { get; set; }
    }

    /// <summary>Response of POST maintenance/trigger.</summary>
    // Declaration order == wire order.
    public class TriggerMaintenanceResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;

        /// <summary>
        /// The MaintenanceResult run report (type owned by the Functions project, so this stays
        /// <c>object</c>; serialized by runtime type, wire-identical).
        /// </summary>
        public object Result { get; set; } = default!;

        public string TriggeredBy { get; set; } = default!;
        public DateTime TriggeredAt { get; set; }
    }

    /// <summary>
    /// One customs-archive run (one (tenantId, historyRowKey) partition) with per-source-table
    /// row counts, as listed by GET global/customs-archive.
    /// </summary>
    // Declaration order == wire order.
    public class CustomsArchiveRunSummary
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string HistoryRowKey { get; set; } = string.Empty;

        /// <summary>Earliest ArchivedAt across the run's rows.</summary>
        public DateTime ArchivedAt { get; set; }

        public int GatherRulesCount { get; set; }
        public int AnalyzeRulesCount { get; set; }
        public int ImeLogPatternsCount { get; set; }
    }

    /// <summary>
    /// One archived entry of a customs-archive run with a truncated EntityJson preview,
    /// as listed by GET global/customs-archive/{tenantId}/{historyRowKey}.
    /// </summary>
    // Declaration order == wire order.
    public class CustomsArchiveEntrySummary
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;
        public string OriginalTable { get; set; } = string.Empty;
        public string OriginalRowKey { get; set; } = string.Empty;
        public DateTime ArchivedAt { get; set; }

        /// <summary>First 200 characters of the archived EntityJson.</summary>
        public string EntityJsonPreview { get; set; } = string.Empty;
    }

    /// <summary>Response of GET global/customs-archive: every archive run, newest first.</summary>
    // Declaration order == wire order.
    public class CustomsArchiveRunListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<CustomsArchiveRunSummary> Runs { get; set; } = default!;
    }

    /// <summary>Response of GET global/customs-archive/{tenantId}/{historyRowKey}: the run's entries.</summary>
    // Declaration order == wire order.
    public class CustomsArchiveEntryListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<CustomsArchiveEntrySummary> Entries { get; set; } = default!;
    }

    /// <summary>Response of GET global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey}: one full entry.</summary>
    // Declaration order == wire order.
    public class CustomsArchiveEntryResponse : IApiResponse
    {
        public bool Success { get; set; }
        public TenantOffboardingCustomsArchiveEntry Entry { get; set; } = default!;
    }

    /// <summary>Response of DELETE global/customs-archive/{tenantId}/{historyRowKey}: bulk-delete acknowledgement.</summary>
    // Declaration order == wire order.
    public class CustomsArchiveDeleteRunResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>Number of archive rows removed.</summary>
        public int Deleted { get; set; }
    }

    /// <summary>Response of GET global/delegated-admins: every delegated assignment.</summary>
    // Declaration order == wire order.
    public class DelegatedAdminListResponse : IApiResponse
    {
        public IReadOnlyList<DelegatedAdminEntry> Assignments { get; set; } = default!;
    }

    /// <summary>Response of POST global/delegated-admins: the granted (created/replaced) assignment.</summary>
    // Declaration order == wire order.
    public class DelegatedAdminGrantResponse : IApiResponse
    {
        public DelegatedAdminEntry Assignment { get; set; } = default!;
    }

    /// <summary>
    /// Shared response of the blocked-device listings (GET devices/blocked and
    /// GET global/devices/blocked): the active blocks in scope.
    /// </summary>
    // Declaration order == wire order.
    public class BlockedDeviceListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<BlockedDeviceEntry> Blocked { get; set; } = default!;
    }

    /// <summary>Response of POST devices/block: block/kill acknowledgement.</summary>
    // Declaration order == wire order.
    public class BlockDeviceResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public DateTime UnblockAt { get; set; }

        /// <summary>"Block" or "Kill" (normalized casing).</summary>
        public string Action { get; set; } = default!;
    }

    /// <summary>Response of GET global/email-templates/{kind}: the effective template.</summary>
    // Declaration order == wire order.
    public class EmailTemplateResponse : IApiResponse
    {
        /// <summary>"welcome" or "farewell".</summary>
        public string Kind { get; set; } = default!;

        public string Subject { get; set; } = default!;
        public bool IsOverridden { get; set; }

        /// <summary>The effective template HTML: the override when stored, otherwise the built-in raw template.</summary>
        public string Html { get; set; } = default!;

        public string BuiltInHtml { get; set; } = default!;

        /// <summary>Who stored the override, or null when no override exists — the key is omitted when null.</summary>
        public string? UpdatedBy { get; set; }

        /// <summary>When the override was stored, or null when no override exists — the key is omitted when null.</summary>
        public DateTime? UpdatedUtc { get; set; }

        /// <summary>The domain placeholder token used in the raw template.</summary>
        public string Placeholder { get; set; } = default!;

        public int MaxLength { get; set; }
    }

    /// <summary>Response of PUT global/email-templates/{kind}: override stored.</summary>
    // Declaration order == wire order.
    public class EmailTemplateSaveResponse : IApiResponse
    {
        /// <summary>"welcome" or "farewell".</summary>
        public string Kind { get; set; } = default!;

        public bool IsOverridden { get; set; }
        public string UpdatedBy { get; set; } = default!;
        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>Response of DELETE global/email-templates/{kind}: reset to the built-in template.</summary>
    // Declaration order == wire order.
    public class EmailTemplateResetResponse : IApiResponse
    {
        /// <summary>"welcome" or "farewell".</summary>
        public string Kind { get; set; } = default!;

        public bool IsOverridden { get; set; }
    }

    /// <summary>Response of POST global/email-templates/{kind}/test: the test mail was accepted by the provider.</summary>
    // Declaration order == wire order.
    public class EmailTemplateTestSendResponse : IApiResponse
    {
        public string SentTo { get; set; } = default!;
        public string DomainName { get; set; } = default!;

        /// <summary>True when an unsaved draft body was sent instead of the effective template.</summary>
        public bool Draft { get; set; }
    }

    /// <summary>Response of GET global/identity-bindings: every admin identity binding.</summary>
    // Declaration order == wire order.
    public class IdentityBindingListResponse : IApiResponse
    {
        public IReadOnlyList<AdminIdentityBinding> Bindings { get; set; } = default!;
    }

    /// <summary>Response of PUT global/identity-bindings/{upn}: the created/replaced binding.</summary>
    // Declaration order == wire order.
    public class IdentityBindingResponse : IApiResponse
    {
        public AdminIdentityBinding Binding { get; set; } = default!;
    }

    /// <summary>Reseed counters for a rule table with sunset handling (gather / analyze).</summary>
    // Declaration order == wire order.
    public class ReseedRuleCountsNode
    {
        public int Deleted { get; set; }
        public int Written { get; set; }

        /// <summary>Orphan per-tenant RuleState rows cleaned while sunsetting rules missing from the new catalog.</summary>
        public int OrphanStatesGcd { get; set; }

        /// <summary>Sunset rules skipped on failure (retried on the next reseed).</summary>
        public int SunsetSkipped { get; set; }
    }

    /// <summary>Reseed counters for a plain delete-and-rewrite table (IME patterns, CPE mappings).</summary>
    // Declaration order == wire order.
    public class ReseedTableCountsNode
    {
        public int Deleted { get; set; }
        public int Written { get; set; }
    }

    /// <summary>Response of POST rules/reseed-from-github: per-catalog reseed counters.</summary>
    // Declaration order == wire order.
    public class ReseedFromGitHubResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public ReseedRuleCountsNode Gather { get; set; } = default!;
        public ReseedRuleCountsNode Analyze { get; set; } = default!;
        public ReseedTableCountsNode Ime { get; set; } = default!;
        public ReseedTableCountsNode CpeCommunityMappings { get; set; } = default!;
        public ReseedTableCountsNode CpeSeedMappings { get; set; } = default!;
    }

    /// <summary>Response of POST tenants/{tenantId}/admins: the created tenant member.</summary>
    // Declaration order == wire order.
    public class TenantAdminCreatedResponse : IApiResponse
    {
        /// <summary>
        /// The stored TenantAdminEntity row (type owned by the Functions project, so this stays
        /// <c>object</c>; serialized by runtime type, wire-identical).
        /// </summary>
        public object Admin { get; set; } = default!;
    }

    /// <summary>Response of GET global/tenant-groups: every group with tenants + assignees.</summary>
    // Declaration order == wire order.
    public class TenantGroupListResponse : IApiResponse
    {
        public IReadOnlyList<TenantGroup> Groups { get; set; } = default!;
    }

    /// <summary>Response of POST global/tenant-groups: the created group's id and (trimmed) name.</summary>
    // Declaration order == wire order.
    public class CreateTenantGroupResponse : IApiResponse
    {
        public string GroupId { get; set; } = default!;
        public string Name { get; set; } = default!;
    }

    /// <summary>Response of GET versions/blocked: every active version block rule.</summary>
    // Declaration order == wire order.
    public class BlockedVersionListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<BlockedVersionEntry> Rules { get; set; } = default!;
    }

    /// <summary>Response of POST versions/block: block/kill rule acknowledgement.</summary>
    // Declaration order == wire order.
    public class BlockVersionResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public string VersionPattern { get; set; } = default!;

        /// <summary>"Block" or "Kill" (normalized casing).</summary>
        public string Action { get; set; } = default!;
    }
}
