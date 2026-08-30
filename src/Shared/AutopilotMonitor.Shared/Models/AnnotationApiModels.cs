using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Wire shape of one session annotation as returned by the session-scoped annotation
    /// endpoints (lane implied by context, tenant/session implied by the route).
    /// Built by <c>AnnotationWire.ToWire</c>.
    /// </summary>
    // Declaration order == wire order.
    public class SessionAnnotationItem
    {
        public string Lane { get; set; } = default!;

        /// <summary>One of the annotation verdict vocabulary values, or null (note-only annotation) — the key is omitted when null.</summary>
        public string? Verdict { get; set; }

        /// <summary>Free-text note, or null (verdict-only annotation) — the key is omitted when null.</summary>
        public string? Note { get; set; }

        public string AuthorUpn { get; set; } = default!;
        public string AuthorDisplayName { get; set; } = default!;
        public string CreatedByUpn { get; set; } = default!;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>Snapshot of the rule ids that had fired for the session at write time.</summary>
        public IReadOnlyList<string> RuleIds { get; set; } = default!;
    }

    /// <summary>
    /// Wire shape of one session annotation in the cross-session list endpoints, where each
    /// row carries its own tenant/session scope. Built by <c>AnnotationWire.ToWireWithScope</c>.
    /// </summary>
    // Declaration order == wire order.
    public class SessionAnnotationScopedItem
    {
        public string TenantId { get; set; } = default!;
        public string SessionId { get; set; } = default!;
        public string Lane { get; set; } = default!;

        /// <summary>One of the annotation verdict vocabulary values, or null (note-only annotation) — the key is omitted when null.</summary>
        public string? Verdict { get; set; }

        /// <summary>Free-text note, or null (verdict-only annotation) — the key is omitted when null.</summary>
        public string? Note { get; set; }

        public string AuthorUpn { get; set; } = default!;
        public string AuthorDisplayName { get; set; } = default!;
        public string CreatedByUpn { get; set; } = default!;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>Snapshot of the rule ids that had fired for the session at write time.</summary>
        public IReadOnlyList<string> RuleIds { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET sessions/{sessionId}/annotations: the annotation lanes visible to the
    /// caller plus the server-computed list of lanes the caller may write.
    /// </summary>
    // Declaration order == wire order.
    public class GetSessionAnnotationsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string SessionId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public IReadOnlyList<SessionAnnotationItem> Annotations { get; set; } = default!;

        /// <summary>Lanes the caller is allowed to write — the web renders lanes writable exactly when this list says so.</summary>
        public IReadOnlyList<string> WritableLanes { get; set; } = default!;
    }

    /// <summary>
    /// Shared response of the annotation list endpoints (global/session-annotations and
    /// sessions/annotations/list): one page of scoped annotations with an optional
    /// continuation link.
    /// </summary>
    // Declaration order == wire order.
    public class SessionAnnotationListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<SessionAnnotationScopedItem> Annotations { get; set; } = default!;

        /// <summary>Absolute-path link to the next page, or null on the last page — the key is omitted when null.</summary>
        public string? NextLink { get; set; }
    }

    /// <summary>
    /// Response of PUT sessions/{sessionId}/annotations/{lane} when both verdict and note
    /// were empty and the lane was cleared.
    /// </summary>
    // Declaration order == wire order.
    public class UpsertSessionAnnotationDeletedResponse : IApiResponse
    {
        public bool Success { get; set; }
        public bool Deleted { get; set; }
    }

    /// <summary>
    /// Response of PUT sessions/{sessionId}/annotations/{lane} on a successful upsert:
    /// the stored annotation as the session-scoped endpoints would return it.
    /// </summary>
    // Declaration order == wire order.
    public class UpsertSessionAnnotationResponse : IApiResponse
    {
        public bool Success { get; set; }
        public SessionAnnotationItem Annotation { get; set; } = default!;
    }
}
