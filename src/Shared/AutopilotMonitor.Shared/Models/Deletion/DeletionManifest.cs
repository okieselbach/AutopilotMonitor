using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Models.Deletion
{
    /// <summary>
    /// Snapshot manifest for cascade-delete of a single session. The full set of rows targeted
    /// by the cascade is captured here at preflight time, before any delete fires. This blob
    /// is both the contract (cascade only deletes what's listed) and the backup (restore replays
    /// the dumps back into the tables). See plan §3 for the canonical schema.
    /// </summary>
    public class DeletionManifest
    {
        /// <summary>ULID-shaped identifier of this manifest. Bound to a single producer attempt.</summary>
        public string ManifestId { get; set; } = string.Empty;

        /// <summary>Schema version for forward-compatibility. v1 = 1.</summary>
        public int ManifestVersion { get; set; } = 1;

        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>UTC timestamp at which the manifest was built.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>Who or what triggered the cascade.</summary>
        public DeletionActor CreatedBy { get; set; } = new DeletionActor();

        /// <summary>Free-form reason string (e.g. "admin_delete", "retention_cutoff").</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Per-tenant retention context at the moment the cascade was triggered.</summary>
        public DeletionRetentionContext RetentionContext { get; set; } = new DeletionRetentionContext();

        /// <summary>Row counts per logical bucket; convenient for audit + monitoring without parsing every step.</summary>
        public Dictionary<string, int> PreflightCounts { get; set; } = new Dictionary<string, int>();

        /// <summary>
        /// Customer-storage diagnostics blob name (informational only — cascade does NOT delete
        /// the blob; customer owns the storage account). See plan §3 + §11.
        /// </summary>
        public string? DiagnosticsBlobName { get; set; }

        /// <summary>Ordered cascade steps. The worker executes them in <see cref="DeletionStep.Order"/> order.</summary>
        public List<DeletionStep> Steps { get; set; } = new List<DeletionStep>();

        /// <summary>SHA-256 hash of the manifest contents (excluding this field). Used to detect tampering.</summary>
        public string SchemaHash { get; set; } = string.Empty;
    }

    /// <summary>
    /// Identifies the trigger source of a cascade. <see cref="Type"/> is one of
    /// <c>"admin"</c> or <c>"maintenance"</c>; <see cref="Actor"/> is the user identity for
    /// admin paths or <c>"system"</c> for maintenance.
    /// </summary>
    public class DeletionActor
    {
        public string Type { get; set; } = string.Empty;
        public string Actor { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-tenant retention parameters captured at cascade-build time. Carried in the manifest
    /// so the audit log shows *which* retention policy applied to a given retention-driven delete.
    /// </summary>
    public class DeletionRetentionContext
    {
        public int? TenantRetentionDays { get; set; }
        public DateTime? CutoffUtc { get; set; }
    }

    /// <summary>
    /// One ordered step in the cascade. Either targets a real table (<see cref="Table"/> set,
    /// <see cref="Step"/> null) or a synthetic operation (<see cref="Step"/> set,
    /// <see cref="Table"/> null — only the SoftwareInventoryDecrement and Tombstone steps).
    /// <see cref="Class"/> is one of the values defined in <see cref="DeletionStepClass"/>.
    /// </summary>
    public class DeletionStep
    {
        public int Order { get; set; }

        /// <summary>Table name; null for synthetic steps (Tombstone, SoftwareInventoryDecrement).</summary>
        public string? Table { get; set; }

        /// <summary>Synthetic step name; null for table-targeted steps. Values from <see cref="DeletionStepNames"/>.</summary>
        public string? Step { get; set; }

        /// <summary>Key-class taxonomy. Values from <see cref="DeletionStepClass"/>.</summary>
        public string Class { get; set; } = string.Empty;

        public int RowCount { get; set; }

        /// <summary>Full row dumps. Empty for AGGREGATE steps; the Tombstone step always has 2 entries.</summary>
        public List<DeletionRowDump> Rows { get; set; } = new List<DeletionRowDump>();

        /// <summary>Decrement keys for the AGGREGATE step (SoftwareInventoryDecrement); null for all other classes.</summary>
        public List<DeletionDecrementKey>? Decrements { get; set; }
    }

    /// <summary>
    /// Captured snapshot of a single Azure Table row. <see cref="Props"/> preserves the original
    /// Azure Tables EDM type alongside the JSON value, so a restore can re-Insert the row with
    /// the same column types — a bare <see cref="JsonElement"/> alone would collapse
    /// DateTime / Guid / byte[] / long into ambiguous strings, breaking the
    /// manifest-as-backup guarantee (plan §3 "types preserved").
    /// </summary>
    public class DeletionRowDump
    {
        public string Pk { get; set; } = string.Empty;
        public string Rk { get; set; } = string.Empty;

        /// <summary>ETag as observed at preflight; informational only (not used for restore).</summary>
        public string? Etag { get; set; }

        public Dictionary<string, DeletionPropValue> Props { get; set; } = new Dictionary<string, DeletionPropValue>();
    }

    /// <summary>
    /// Single Azure Table column value with its EDM type tag. Restore looks at <see cref="EdmType"/>
    /// to decide which <c>Get*</c> / strongly-typed entity setter to use; without the tag, a string
    /// timestamp coming out of JSON would be re-inserted as a literal string and the column would
    /// silently change shape.
    /// </summary>
    public class DeletionPropValue
    {
        /// <summary>
        /// One of: <c>String</c>, <c>Int32</c>, <c>Int64</c>, <c>Double</c>, <c>Boolean</c>,
        /// <c>DateTime</c>, <c>Guid</c>, <c>Binary</c>. Matches the Azure Tables OData EDM
        /// type names (minus the <c>Edm.</c> prefix).
        /// </summary>
        public string EdmType { get; set; } = DeletionPropEdmType.String;

        /// <summary>JSON-encoded value. For DateTime / Guid / Binary this is the ISO-8601 /
        /// GUID-string / Base64 form respectively.</summary>
        public JsonElement Value { get; set; }
    }

    /// <summary>EDM type constants used in <see cref="DeletionPropValue.EdmType"/>.</summary>
    public static class DeletionPropEdmType
    {
        public const string String   = "String";
        public const string Int32    = "Int32";
        public const string Int64    = "Int64";
        public const string Double   = "Double";
        public const string Boolean  = "Boolean";
        public const string DateTime = "DateTime";
        public const string Guid     = "Guid";
        public const string Binary   = "Binary";
    }

    /// <summary>
    /// One key targeted by the SoftwareInventoryDecrement AGGREGATE step. The cascade applies
    /// <c>SessionCount -= 1</c> with clamp-≥-0 on the matching <c>SoftwareInventory</c> row.
    /// Shape mirrors the increment side in <c>VulnerabilityCorrelationService</c>.
    /// </summary>
    public class DeletionDecrementKey
    {
        public string Vendor { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-step key-class taxonomy. Encodes how preflight enumerates the table and how the worker
    /// deletes (always by exact (PK,RK) at cascade time — see plan §1 P6 for why).
    /// </summary>
    public static class DeletionStepClass
    {
        public const string PkBySession              = "PK_BY_SESSION";
        public const string PkRkExact                = "PK_RK_EXACT";
        public const string PropTenantPk             = "PROP_TENANT_PK";
        public const string DiscriminatorPkRkSuffix  = "DISCRIMINATOR_PK_RK_SUFFIX";
        public const string DiscriminatorPkRkExact   = "DISCRIMINATOR_PK_RK_EXACT";
        public const string DiscriminatorPkProp      = "DISCRIMINATOR_PK_PROP";
        public const string Aggregate                = "AGGREGATE";
        public const string Final                    = "FINAL";
    }

    /// <summary>Synthetic step names for non-table cascade operations.</summary>
    public static class DeletionStepNames
    {
        public const string SoftwareInventoryDecrement = "SoftwareInventoryDecrement";
        public const string Tombstone                  = "Tombstone";
    }

    /// <summary>
    /// Centralized JSON options for manifest (de)serialization. CamelCase casing matches the
    /// schema documented in plan §3.
    /// </summary>
    public static class DeletionManifestJson
    {
        public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // DictionaryKeyPolicy is intentionally NOT set: <see cref="DeletionRowDump.Props"/> uses
            // the original Azure Table column names (e.g. "EventType", "MaxSeverity") as dictionary
            // keys — those MUST round-trip byte-faithfully so a restore can re-insert the row with
            // the correct column names. <see cref="DeletionManifest.PreflightCounts"/> keys are
            // already camelCase by construction.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
    }
}
