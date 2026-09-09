using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One custom rule a tenant admin submitted for the community pool. Stored in the
    /// <c>RuleSubmissions</c> table: PK = <c>"submissions"</c> (one partition for every tenant,
    /// the SessionReports layout), RK = <c>{invertedTicks(SubmittedAt)}_{SubmissionId}</c>.
    /// <see cref="RuleJson"/> is the server-frozen snapshot of the rule at submit time — the
    /// tenant's later edits or deletes never reach it, and the payload never supplies it.
    /// The stored <see cref="Status"/> is never <see cref="RuleSubmissionStatuses.Published"/>:
    /// that value is derived at read time from the global rule catalog.
    /// </summary>
    public class RuleSubmission
    {
        /// <summary>Twelve hex characters, the same shape as a session report id.</summary>
        public string SubmissionId { get; set; } = default!;

        /// <summary>Groups the rules of one submit action; each rule keeps its own id and status.</summary>
        public string BatchId { get; set; } = default!;
        public string TenantId { get; set; } = default!;

        /// <summary>One of <see cref="RuleSubmissionKinds.All"/>.</summary>
        public string RuleKind { get; set; } = default!;

        /// <summary>The rule id in the tenant's partition at submit time.</summary>
        public string SourceRuleId { get; set; } = default!;
        public string Title { get; set; } = default!;
        public string Category { get; set; } = default!;

        /// <summary>Frozen rule document (wire JSON of the rule minus server and tenant fields).</summary>
        public string RuleJson { get; set; } = default!;
        public string? Comment { get; set; }
        public string SubmittedBy { get; set; } = default!;
        public string SubmittedByName { get; set; } = default!;

        /// <summary>One of <see cref="RuleAttributionModes.All"/>.</summary>
        public string AttributionMode { get; set; } = RuleAttributionModes.Anonymous;

        /// <summary>The exact text that becomes the published rule's <c>author</c>. Frozen at submit time.</summary>
        public string AttributionName { get; set; } = RuleAttributionModes.AnonymousAuthor;
        public DateTime SubmittedAt { get; set; }

        /// <summary>One of <see cref="RuleSubmissionStatuses.Stored"/>.</summary>
        public string Status { get; set; } = RuleSubmissionStatuses.Pending;
        public List<RuleSubmissionFinding> ValidationFindings { get; set; } = new List<RuleSubmissionFinding>();
        public RuleSubmissionFireStats? SourceFireStats { get; set; }
        public string? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewComment { get; set; }

        /// <summary>The reviewer announced that the published rule will differ from the submitted one.</summary>
        public bool WillBeAdapted { get; set; }

        /// <summary>The reserved-namespace id assigned on approval; the published rule's id.</summary>
        public string? PublishedRuleId { get; set; }
        public string? DerivedFromTemplateRuleId { get; set; }
    }

    /// <summary>A pre-flight finding recorded at submit time. Errors block the submission and are never stored.</summary>
    [WireContract]
    public class RuleSubmissionFinding
    {
        /// <summary>"error" | "warning" | "info".</summary>
        public string Level { get; set; } = default!;
        public string Message { get; set; } = default!;
    }

    /// <summary>Fire telemetry of the submitted rule in the submitting tenant over the trailing window.</summary>
    [WireContract]
    public class RuleSubmissionFireStats
    {
        public int Days { get; set; }
        public int FireCount { get; set; }
        public int SessionsEvaluated { get; set; }
        public int EvaluationCount { get; set; }
    }

    public static class RuleSubmissionKinds
    {
        public const string Gather = "gather";
        public const string Analyze = "analyze";
        public static readonly string[] All = { Gather, Analyze };

        public static bool IsKnown(string? kind)
            => kind == Gather || kind == Analyze;
    }

    public static class RuleSubmissionStatuses
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Declined = "declined";
        public const string Withdrawn = "withdrawn";

        /// <summary>Derived at read time (approved + the published id exists in the global catalog); never stored.</summary>
        public const string Published = "published";

        /// <summary>Every wire value, including the derived one.</summary>
        public static readonly string[] All = { Pending, Approved, Declined, Withdrawn, Published };

        /// <summary>The values a row may carry.</summary>
        public static readonly string[] Stored = { Pending, Approved, Declined, Withdrawn };
    }

    public static class RuleAttributionModes
    {
        public const string Anonymous = "anonymous";
        public const string Organization = "organization";
        public const string Person = "person";
        public static readonly string[] All = { Anonymous, Organization, Person };

        /// <summary>The <c>author</c> text of an anonymously contributed rule.</summary>
        public const string AnonymousAuthor = "Community contribution";

        public static bool IsKnown(string? mode)
            => mode == Anonymous || mode == Organization || mode == Person;
    }

    public static class RuleSubmissionDecisions
    {
        public const string Approve = "approve";
        public const string Decline = "decline";
        public static readonly string[] All = { Approve, Decline };
    }
}
