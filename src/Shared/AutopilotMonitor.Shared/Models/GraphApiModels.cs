using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response of POST tenants/{tenantId}/scripts/display-names: resolved Intune script
    /// display names keyed by the canonical ref string. Always 200, possibly partial —
    /// unresolved refs stay in the dictionary with a null VALUE (dictionary values are NOT
    /// subject to WhenWritingNull, so the key is emitted with an explicit JSON null).
    /// </summary>
    // Declaration order == wire order.
    public class GetScriptDisplayNamesResponse : IApiResponse
    {
        /// <summary>
        /// Display name per canonical script ref ("Platform:{id}" / "Remediation:{id}").
        /// IMPORTANT: dictionary KEYS do not run through the camelCase PropertyNamingPolicy
        /// (ApiJsonOptions sets no DictionaryKeyPolicy) — the ref strings are serialized
        /// verbatim, including the PascalCase type prefix. Pinned by GraphWireParityTests.
        /// </summary>
        public Dictionary<string, string?> Refs { get; set; } = default!;

        /// <summary>
        /// Ref tokens from the request that failed to parse, or null when there were none —
        /// the key is omitted when null (the empty-body and empty-refs early-exit sites never
        /// wrote this key at all; they leave it null).
        /// </summary>
        public IReadOnlyList<string>? Malformed { get; set; }
    }

    /// <summary>
    /// One feature row of the graph-permissions status matrix: the feature identifier, the
    /// granted verdict (null while the snapshot is transient) and the Graph application
    /// permissions the feature requires.
    /// </summary>
    // Declaration order == wire order.
    public class GraphFeatureStatusItem
    {
        public string Name { get; set; } = default!;

        /// <summary>Granted verdict, or null when the snapshot is transient (verdict unknown) — the key is omitted when null.</summary>
        public bool? Granted { get; set; }

        public IReadOnlyList<string> RequiredPermissions { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET tenants/{tenantId}/graph-permissions/status: the client id of the app
    /// homed for the tenant, the transient flag, the granted Graph app roles and the
    /// per-feature verdict matrix.
    /// </summary>
    // Declaration order == wire order.
    public class GetGraphPermissionsStatusResponse : IApiResponse
    {
        /// <summary>ClientId of the app registration that acts for this tenant (empty string when unresolved, never null).</summary>
        public string ClientId { get; set; } = default!;

        /// <summary>True when the snapshot is not authoritative (token-acquire timeout / transient failure) — the UI renders "try again".</summary>
        public bool IsTransient { get; set; }

        public IReadOnlyList<string> GrantedRoles { get; set; } = default!;
        public IReadOnlyList<GraphFeatureStatusItem> Features { get; set; } = default!;
    }
}
