using System;

namespace AutopilotMonitor.Shared.Models.Graph
{
    /// <summary>
    /// One cached display-name row. Negative results (<see cref="IsNotFound"/> = true) live
    /// as long as positive ones.
    /// </summary>
    public sealed class ScriptDisplayNameEntry
    {
        public string TenantId { get; set; } = string.Empty;
        public ScriptKind Kind { get; set; }
        public string ScriptId { get; set; } = string.Empty;

        /// <summary>Display name from Graph. Null when <see cref="IsNotFound"/>.</summary>
        public string? DisplayName { get; set; }

        /// <summary>File name (Platform Scripts only). Null for Remediations.</summary>
        public string? FileName { get; set; }

        /// <summary>When the Graph fetch happened. Drives TTL.</summary>
        public DateTimeOffset FetchedAt { get; set; }

        /// <summary>True when Graph returned 404 (script deleted in tenant). Negative-cache row.</summary>
        public bool IsNotFound { get; set; }
    }

    /// <summary>
    /// Per-(tenant, kind) meta row. Tracks the last time we did a list-full-pull of every script
    /// of this kind in the tenant. When the meta row is older than the full-refresh window
    /// (default 7d) we re-pull on the next cache miss instead of doing per-ID fallback fetches.
    /// </summary>
    public sealed class ScriptNameCacheMeta
    {
        public string TenantId { get; set; } = string.Empty;
        public ScriptKind Kind { get; set; }
        public DateTimeOffset LastFullRefreshAt { get; set; }
    }
}
