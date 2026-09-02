using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models.Config;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Paginated/projected response of GET config/all (delegated one-shot page and the
    /// GA ?pageSize= mode): keep-list projections of the tenant configurations
    /// (TenantConfigProjection dictionaries — secrets can never be selected).
    /// </summary>
    // Declaration order == wire order.
    public class GetAllTenantConfigurationsResponse : IApiResponse
    {
        public int Count { get; set; }

        /// <summary>Keep-list field projections (TenantConfigProjection.ProjectAll dictionaries).</summary>
        public IReadOnlyList<Dictionary<string, object?>> Tenants { get; set; } = default!;

        /// <summary>Absolute-path link to the next page, or null on the last page — the key is omitted when null.</summary>
        public string? NextLink { get; set; }
    }

    /// <summary>
    /// Consent-probe verdict embedded in app-homing responses (success and deny alike).
    /// Built by <c>AppHomingFunction.ProbePayload</c>.
    /// </summary>
    // Declaration order == wire order.
    public class AppHomingProbeWire
    {
        /// <summary>False when the decision needed no probe (e.g. GA force flip).</summary>
        public bool Attempted { get; set; }

        public bool Succeeded { get; set; }
        public bool IsTransient { get; set; }
    }

    /// <summary>
    /// Response of POST config/{tenantId}/app-homing on an allowed flip (or allowed no-op):
    /// the resulting homing state plus the consent-probe verdict.
    /// </summary>
    // Declaration order == wire order.
    public class UpdateTenantAppHomingResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>False for an allowed no-op (already homed at the target).</summary>
        public bool Changed { get; set; }

        /// <summary>"primary" or "legacy" — the app the tenant resolves to after the call.</summary>
        public string HomedApp { get; set; } = default!;

        /// <summary>Explicit homing pin, or null (legacy default) — the key is omitted when null.</summary>
        public string? HomedAppClientId { get; set; }

        /// <summary>Client id observed on the tenant's last agent auth, or null — the key is omitted when null.</summary>
        public string? LastAuthClientId { get; set; }

        public DateTime? LastAuthClientIdSince { get; set; }
        public AppHomingProbeWire Probe { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET config/latest-versions: the latest published agent + bootstrap
    /// script versions (null slots when the version blob could not be fetched).
    /// </summary>
    // Declaration order == wire order.
    public class GetLatestVersionsResponse : IApiResponse
    {
        public string? LatestAgentVersion { get; set; }
        public string? LatestBootstrapScriptVersion { get; set; }
        public string? LatestAgentSha256 { get; set; }
        public DateTimeOffset? FetchedAtUtc { get; set; }

        /// <summary>"cache" or "blob".</summary>
        public string Source { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET config/fields-schema: the machine-readable tenant-config field schema
    /// for the MCP write surface.
    /// </summary>
    // Declaration order == wire order.
    public class GetTenantConfigFieldsSchemaResponse : IApiResponse
    {
        public int Count { get; set; }
        public int WritableCount { get; set; }

        public IReadOnlyList<TenantConfigFieldSchema> Fields { get; set; } = default!;
    }

    /// <summary>
    /// One entry of the tenant-config field schema (built by TenantConfigPatchService.BuildFieldsSchema).
    /// Serialized camelCase; null Format/Reason keys are omitted (WhenWritingNull) — the null and
    /// set cases are pinned by ConfigWireParityTests.
    /// </summary>
    // Declaration order == wire order (matches the former positional-record constructor order).
    public sealed class TenantConfigFieldSchema
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string? Format { get; set; }
        public bool Nullable { get; set; }
        public bool Writable { get; set; }
        public string? Reason { get; set; }
        public bool GaOnly { get; set; }
        public bool RevertProtected { get; set; }

        public TenantConfigFieldSchema(
            string name,
            string type,
            string? format,
            bool nullable,
            bool writable,
            string? reason,
            bool gaOnly,
            bool revertProtected)
        {
            Name = name;
            Type = type;
            Format = format;
            Nullable = nullable;
            Writable = writable;
            Reason = reason;
            GaOnly = gaOnly;
            RevertProtected = revertProtected;
        }
    }

    /// <summary>
    /// One pre-write config snapshot in GET config/{tenantId}/backups — metadata only,
    /// the raw EntityJson (clear-text secrets) is never returned.
    /// </summary>
    // Declaration order == wire order.
    public class TenantConfigBackupItem
    {
        /// <summary>The snapshot's RowKey (reverse-ticks + short guid) — the public backup id.</summary>
        public string BackupId { get; set; } = default!;

        public DateTime BackupTakenAt { get; set; }
        public string ChangedBy { get; set; } = default!;

        /// <summary>Write path that triggered the snapshot (portal-put | plan | mcp-patch | ...).</summary>
        public string Source { get; set; } = default!;

        /// <summary>Caller-provided intent, or null — the key is omitted when null.</summary>
        public string? Reason { get; set; }

        /// <summary>Masked "old → new" change summary, or null when unparseable — the key is omitted when null.</summary>
        public Dictionary<string, string>? Diff { get; set; }
    }

    /// <summary>
    /// Response of GET config/{tenantId}/backups: the tenant's pre-write config snapshots,
    /// newest first.
    /// </summary>
    // Declaration order == wire order.
    public class ListTenantConfigBackupsResponse : IApiResponse
    {
        public string TenantId { get; set; } = default!;
        public IEnumerable<TenantConfigBackupItem> Backups { get; set; } = default!;
    }

    /// <summary>
    /// Success response of PATCH config/{tenantId}/fields and POST config/{tenantId}/revert
    /// (both flow through the same outcome writer): which fields changed, the masked diff,
    /// and the pre-write backup id.
    /// </summary>
    // Declaration order == wire order.
    public class TenantConfigPatchOutcomeResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyCollection<string> AppliedFields { get; set; } = default!;

        /// <summary>Masked "old → new" summary, or null — the key is omitted when null.</summary>
        public Dictionary<string, string>? Diff { get; set; }

        /// <summary>Pre-write snapshot id, or null (no-op writes take no backup) — the key is omitted when null.</summary>
        public string? BackupId { get; set; }

        /// <summary>True when the patch changed nothing (zero applied fields).</summary>
        public bool NoOp { get; set; }
    }

    /// <summary>
    /// Response of PATCH config/{tenantId}/plan: the resulting plan/trial state.
    /// </summary>
    // Declaration order == wire order.
    public class SetTenantPlanTierResponse : IApiResponse
    {
        public string TenantId { get; set; } = default!;
        public string PlanTier { get; set; } = default!;
        public DateTime? TrialExpiresUtc { get; set; }
        public bool TrialConsumed { get; set; }

        /// <summary>Effective edition after the change, lowercase ("community" | "pro").</summary>
        public string EffectiveEdition { get; set; } = default!;

        /// <summary>End of the retention downgrade grace window, or null — the key is omitted when null.</summary>
        public DateTime? RetentionGraceEndsUtc { get; set; }

        /// <summary>Effective delegated (MSP) tenant slot limit after the change (override or plan entitlement).</summary>
        public int MaxDelegatedTenants { get; set; }

        /// <summary>The Global Admin override; omitted when the plan entitlement applies.</summary>
        public int? MaxDelegatedTenantsOverride { get; set; }

        /// <summary>Effective MCP usage plan name of the tenant after the change (override or edition default).</summary>
        public string McpUsagePlan { get; set; } = default!;

        /// <summary>The Global Admin MCP usage-plan override (a SectionUsagePlans plan name); omitted when the edition default applies.</summary>
        public string? McpUsagePlanOverride { get; set; }
    }

    /// <summary>
    /// Response of POST config/{tenantId}/trial: the started self-service trial.
    /// </summary>
    // Declaration order == wire order.
    public class StartTenantTrialResponse : IApiResponse
    {
        public string TenantId { get; set; } = default!;
        public DateTime? TrialStartedUtc { get; set; }
        public DateTime? TrialExpiresUtc { get; set; }

        /// <summary>Always the Pro tier name — starting a trial makes the tenant effectively Pro.</summary>
        public string EffectiveEdition { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET and PUT global/config/plan-tiers: the global usage-plan tier definitions.
    /// </summary>
    // Declaration order == wire order.
    public class PlanTierDefinitionsResponse : IApiResponse
    {
        public IReadOnlyList<PlanTierDefinition> Tiers { get; set; } = default!;
    }

    /// <summary>
    /// Response of POST config/{tenantId}/test-notification AND POST global/config/test-ops-channel:
    /// the delivery verdict of a test send (HTTP 200 for both verdicts — Success carries the
    /// outcome). One shape for both because the semantics are identical; the platform endpoint
    /// only differs in which channel list it resolves the id against.
    /// </summary>
    // Declaration order == wire order.
    public class TestWebhookNotificationResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>HTTP status returned by the webhook endpoint, or null when the send never got a response — the key is omitted when null.</summary>
        public int? StatusCode { get; set; }

        public string Message { get; set; } = default!;
    }

    /// <summary>
    /// Response of PUT global/config: acknowledgement plus the stored admin configuration.
    /// </summary>
    // Declaration order == wire order.
    public class UpdateAdminConfigurationResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public AdminConfiguration Config { get; set; } = default!;
    }

    /// <summary>
    /// Response of PUT config/{tenantId}: acknowledgement plus the stored tenant configuration.
    /// </summary>
    // Declaration order == wire order.
    public class UpdateTenantConfigurationResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public TenantConfiguration Config { get; set; } = default!;
    }
}
