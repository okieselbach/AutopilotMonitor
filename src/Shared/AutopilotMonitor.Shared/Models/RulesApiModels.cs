using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Analyze-rule listing envelope shared by GetAnalyzeRules (tenant-scoped) and
    /// GetGlobalAnalyzeRules (Global Admin, ?tenantId= scoped).
    /// </summary>
    // Declaration order == wire order.
    public class AnalyzeRuleListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<AnalyzeRule> Rules { get; set; } = default!;
    }

    /// <summary>
    /// Gather-rule listing envelope shared by GetGatherRules (tenant-scoped) and
    /// GetGlobalGatherRules (Global Admin, ?tenantId= scoped).
    /// </summary>
    // Declaration order == wire order.
    public class GatherRuleListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<GatherRule> Rules { get; set; } = default!;
    }

    /// <summary>IME log pattern listing envelope (GetImeLogPatterns).</summary>
    // Declaration order == wire order.
    public class ImeLogPatternListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<ImeLogPattern> Patterns { get; set; } = default!;
    }

    /// <summary>
    /// Response of POST rules/analyze/{ruleId}/create-from-template: the newly created
    /// custom rule instantiated from the template.
    /// </summary>
    // Declaration order == wire order.
    public class CreateAnalyzeRuleFromTemplateResponse : IApiResponse
    {
        public bool Success { get; set; }
        public AnalyzeRule Rule { get; set; } = default!;
        public string Message { get; set; } = default!;
    }

    /// <summary>
    /// Response of POST rules/analyze/dryrun: the full diagnostic trace of one draft-rule
    /// evaluation against a session.
    /// </summary>
    // Declaration order == wire order.
    public class DryRunAnalyzeRuleResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string SessionId { get; set; } = default!;

        /// <summary>
        /// The <c>RuleDryRun</c> trace (AutopilotMonitor.Functions.Services) — typed
        /// <c>object</c> here only because that class lives in the Functions assembly;
        /// System.Text.Json serializes the runtime type, so the wire shape is unchanged.
        /// </summary>
        public object Result { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET sessions/{sessionId}/analysis: persisted rule results plus severity
    /// counts over the still-open (unresolved) findings.
    /// </summary>
    // Declaration order == wire order.
    public class GetRuleResultsResponse : IApiResponse
    {
        /// <summary>False when any rule result failed to persist during an on-demand reanalyze.</summary>
        public bool Success { get; set; }

        public string SessionId { get; set; } = default!;

        /// <summary>All persisted results, including resolved findings (kept for audit).</summary>
        public IReadOnlyList<RuleResult> Results { get; set; } = default!;

        /// <summary>Count of open (unresolved) findings only.</summary>
        public int TotalIssues { get; set; }

        public int CriticalCount { get; set; }
        public int HighCount { get; set; }
        public int WarningCount { get; set; }
        public int PersistFailureCount { get; set; }

        /// <summary>Rule ids that failed to persist during the reanalyze — the key is omitted
        /// entirely when there were no failures (the happy-path contract stays unchanged).</summary>
        public IReadOnlyList<string>? PersistFailureRuleIds { get; set; }
    }

    /// <summary>Response of POST rules/ime-log-patterns/reseed (Global Admin only).</summary>
    // Declaration order == wire order.
    public class ReseedImeLogPatternsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public int Deleted { get; set; }
        public int Written { get; set; }
    }

    /// <summary>
    /// Response of POST rules/gather/test-pattern: the per-line evaluation of a logparser
    /// regex with the agent's exact matching semantics.
    /// </summary>
    // Declaration order == wire order.
    public class TestLogPatternResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>"cmtrace" or "text" — the effective mode the lines were evaluated in.</summary>
        public string Format { get; set; } = default!;

        public LogPatternTestResult Result { get; set; } = default!;
    }

    /// <summary>Aggregate outcome of one pattern test. Serialized camelCase to clients.</summary>
    // Declaration order == wire order.
    public sealed class LogPatternTestResult
    {
        public int MatchCount { get; set; }
        public int ParseFailureCount { get; set; }
        public int TimeoutCount { get; set; }
        public List<LogPatternLineResult> Lines { get; } = new();
        public List<string> Notes { get; } = new();
    }

    /// <summary>Per-line outcome row of one pattern test.</summary>
    // Declaration order == wire order.
    public sealed class LogPatternLineResult
    {
        public int LineNumber { get; set; }

        /// <summary>matched | no_match | parse_failed | regex_timeout</summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>The named/numbered capture groups exactly as they would land in the
        /// emitted event's data (group "0" excluded, unsuccessful groups omitted).</summary>
        public Dictionary<string, string>? Groups { get; set; }

        public string? MatchedText { get; set; }

        /// <summary>cmtrace mode only: the parsed component/type/message the regex ran against.</summary>
        public string? Component { get; set; }
        public int? CmTraceType { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>Response of GET preview/whitelist: every approved tenant (Global Admin only).</summary>
    // Declaration order == wire order.
    public class GetPreviewWhitelistResponse : IApiResponse
    {
        /// <summary>
        /// <c>PreviewWhitelistEntity</c> rows (AutopilotMonitor.Functions.DataAccess.TableStorage)
        /// — element-typed <c>object</c> here only because that entity lives in the Functions
        /// assembly; System.Text.Json serializes the runtime type, so the wire shape is unchanged.
        /// </summary>
        public IReadOnlyList<object> Tenants { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET preview/notification-email/{tenantId}. Empty string when no
    /// address is stored (never null — the site coalesces).
    /// </summary>
    // Declaration order == wire order.
    public class GetPreviewNotificationEmailResponse : IApiResponse
    {
        public string Email { get; set; } = default!;
    }

    /// <summary>
    /// Response of GET preview/notification-emails: every stored notification address,
    /// keyed by lowercased tenant id.
    /// </summary>
    // Declaration order == wire order.
    public class GetAllPreviewNotificationEmailsResponse : IApiResponse
    {
        public int Count { get; set; }
        public Dictionary<string, string> Emails { get; set; } = default!;
    }
}
