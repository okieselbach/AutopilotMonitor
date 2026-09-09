using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>One rule reference in a submit request; the server freezes the rule itself.</summary>
    [WireContract]
    public class RuleSubmissionItemRef
    {
        /// <summary>One of <see cref="RuleSubmissionKinds.All"/>.</summary>
        public string Kind { get; set; } = default!;
        public string RuleId { get; set; } = default!;
    }

    /// <summary>Body of POST rules/submissions: up to <see cref="MaxItems"/> of the caller's own custom rules.</summary>
    public class SubmitRuleSubmissionsRequest : IApiRequest
    {
        public const int MaxItems = 10;
        public const int MaxCommentLength = 4000;
        public const int MaxAttributionNameLength = 64;
        public const int MaxEmailLength = 254;

        public string TenantId { get; set; } = default!;
        public List<RuleSubmissionItemRef> Items { get; set; } = new List<RuleSubmissionItemRef>();
        public string? Comment { get; set; }

        /// <summary>Optional reply address; stored for the reviewer, never published.</summary>
        public string? Email { get; set; }

        /// <summary>One of <see cref="RuleAttributionModes.All"/>.</summary>
        public string AttributionMode { get; set; } = RuleAttributionModes.Anonymous;

        /// <summary>Credit text for the <c>person</c> mode; ignored for the other modes.</summary>
        public string? AttributionName { get; set; }
    }

    /// <summary>
    /// Wire shape of one submission (list rows, submit response, detail head). The frozen rule
    /// document is not part of it — the detail response carries it typed.
    /// </summary>
    // Declaration order == wire order.
    [WireContract]
    public class RuleSubmissionItem
    {
        public string SubmissionId { get; set; } = default!;
        public string BatchId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public string RuleKind { get; set; } = default!;
        public string SourceRuleId { get; set; } = default!;
        public string Title { get; set; } = default!;
        public string Category { get; set; } = default!;
        public string? Comment { get; set; }
        public string SubmittedBy { get; set; } = default!;
        public string SubmittedByName { get; set; } = default!;
        public string? ContactEmail { get; set; }
        public string AttributionMode { get; set; } = default!;
        public string AttributionName { get; set; } = default!;
        public DateTime SubmittedAt { get; set; }

        /// <summary>One of <see cref="RuleSubmissionStatuses.All"/> — the effective status, <c>published</c> included.</summary>
        public string Status { get; set; } = default!;
        public IReadOnlyList<RuleSubmissionFinding> ValidationFindings { get; set; } = default!;
        public RuleSubmissionFireStats? SourceFireStats { get; set; }
        public string? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewComment { get; set; }
        public bool WillBeAdapted { get; set; }
        public string? PublishedRuleId { get; set; }
        public string? DerivedFromTemplateRuleId { get; set; }
    }

    /// <summary>Success body of POST rules/submissions.</summary>
    // Declaration order == wire order.
    public class SubmitRuleSubmissionsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public string BatchId { get; set; } = default!;
        public IReadOnlyList<RuleSubmissionItem> Submissions { get; set; } = default!;
    }

    /// <summary>Success body of GET rules/submissions and GET global/rule-submissions.</summary>
    // Declaration order == wire order.
    public class RuleSubmissionListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<RuleSubmissionItem> Submissions { get; set; } = default!;

        /// <summary>Absolute-path link to the next page; null/absent on the last page and in non-paged responses.</summary>
        public string? NextLink { get; set; }
    }

    /// <summary>The repo-ready file for a submission: where it goes and what it contains.</summary>
    // Declaration order == wire order.
    [WireContract]
    public class RuleSubmissionRepoFile
    {
        /// <summary>Repository-relative path, e.g. <c>rules/analyze/ANALYZE-NET-002.json</c>.</summary>
        public string Path { get; set; } = default!;
        public string Content { get; set; } = default!;
    }

    /// <summary>
    /// Success body of GET global/rule-submissions/{id}: the submission, the frozen rule typed by
    /// kind (exactly one of the two rule properties is set), live fire stats, the suggested
    /// reserved id and the repo file built from the assigned (or suggested) id.
    /// </summary>
    // Declaration order == wire order.
    public class RuleSubmissionDetailResponse : IApiResponse
    {
        public bool Success { get; set; }
        public RuleSubmissionItem Submission { get; set; } = default!;
        public AnalyzeRule? AnalyzeRule { get; set; }
        public GatherRule? GatherRule { get; set; }
        public RuleSubmissionFireStats? LiveFireStats { get; set; }
        public string? SuggestedPublishedRuleId { get; set; }
        public RuleSubmissionRepoFile? RepoFile { get; set; }
    }

    /// <summary>Body of PATCH global/rule-submissions/{id}.</summary>
    public class ReviewRuleSubmissionRequest : IApiRequest
    {
        public const int MaxReviewCommentLength = 4000;

        /// <summary>One of <see cref="RuleSubmissionDecisions.All"/>.</summary>
        public string Decision { get; set; } = default!;

        /// <summary>Shown to the submitter. Required for <c>decline</c>.</summary>
        public string? ReviewComment { get; set; }
        public bool? WillBeAdapted { get; set; }

        /// <summary>Reserved-namespace id the rule ships under. Required for <c>approve</c>.</summary>
        public string? PublishedRuleId { get; set; }
    }

    /// <summary>Success body of PATCH global/rule-submissions/{id}.</summary>
    // Declaration order == wire order.
    public class ReviewRuleSubmissionResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public RuleSubmissionItem Submission { get; set; } = default!;
    }
}
