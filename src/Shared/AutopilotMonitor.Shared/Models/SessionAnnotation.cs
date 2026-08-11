using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Human-entered per-session verdict + free-text note, role-separated into lanes
    /// (one row per session + lane, editable in place). Stored in the
    /// <c>SessionAnnotations</c> table: PK = tenantId, RK = <c>{sessionId}_{lane}</c>.
    /// The <c>globaladmin</c> lane is platform-internal and never returned to tenant
    /// callers. Author fields are stamped server-side from the JWT, never from the body.
    /// </summary>
    public class SessionAnnotation
    {
        /// <summary>Maximum length of <see cref="Note"/> (matches the feedback comment cap).</summary>
        public const int MaxNoteLength = 4096;

        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>One of <see cref="AnnotationLanes.All"/>.</summary>
        public string Lane { get; set; } = string.Empty;

        /// <summary>One of <see cref="AnnotationVerdicts.All"/>, or null (note-only annotation).</summary>
        public string? Verdict { get; set; }

        /// <summary>Free text, ≤ <see cref="MaxNoteLength"/>, or null (verdict-only annotation).</summary>
        public string? Note { get; set; }

        /// <summary>UPN of the last editor. Server-stamped.</summary>
        public string AuthorUpn { get; set; } = string.Empty;

        /// <summary>Display name of the last editor. Server-stamped.</summary>
        public string AuthorDisplayName { get; set; } = string.Empty;

        /// <summary>UPN of the first writer. Immutable across edits.</summary>
        public string CreatedByUpn { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>
        /// Snapshot of the rule ids that had fired for the session at write time
        /// (denormalized so rule-quality evaluation needs no join against RuleResults).
        /// </summary>
        public List<string> RuleIds { get; set; } = new();
    }

    /// <summary>Lane discriminators — the RowKey suffix and the write-permission axis.</summary>
    public static class AnnotationLanes
    {
        public const string Operator = "operator";
        public const string TenantAdmin = "tenantadmin";
        public const string GlobalAdmin = "globaladmin";

        public static readonly string[] All = { Operator, TenantAdmin, GlobalAdmin };
    }

    /// <summary>Structured verdict vocabulary. A verdict is always optional.</summary>
    public static class AnnotationVerdicts
    {
        public const string RootCauseConfirmed = "root_cause_confirmed";
        public const string AnalysisWrong = "analysis_wrong";
        public const string DifferentProblem = "different_problem";
        public const string Inconclusive = "inconclusive";

        public static readonly string[] All =
        {
            RootCauseConfirmed, AnalysisWrong, DifferentProblem, Inconclusive,
        };
    }
}
