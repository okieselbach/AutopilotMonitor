using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Defines how to analyze collected events to detect issues
    /// Analyze rules run server-side during event ingestion
    /// </summary>
    public class AnalyzeRule
    {
        /// <summary>
        /// Unique rule identifier (e.g., "ANALYZE-NET-001")
        /// </summary>
        public string RuleId { get; set; } = default!;

        /// <summary>
        /// Human-readable rule title (e.g., "Proxy Authentication Required")
        /// </summary>
        public string Title { get; set; } = default!;

        /// <summary>
        /// Detailed description of what this rule detects
        /// </summary>
        public string Description { get; set; } = default!;

        /// <summary>
        /// Severity level: "info", "warning", "high", "critical"
        /// </summary>
        public string Severity { get; set; } = default!;

        /// <summary>
        /// Rule category: network, identity, enrollment, apps, esp, device
        /// </summary>
        public string Category { get; set; } = default!;

        /// <summary>
        /// Semantic version of this rule (e.g., "1.0.0")
        /// </summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Author of this rule
        /// </summary>
        public string Author { get; set; } = "Autopilot Monitor";

        /// <summary>
        /// Whether this rule is currently enabled for the tenant
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether this is a built-in rule (shipped with the system)
        /// </summary>
        public bool IsBuiltIn { get; set; } = true;

        /// <summary>
        /// Whether this is a community-contributed rule
        /// Community rules behave like built-in rules (read-only, state stored separately)
        /// but are displayed with a distinct "Community" badge in the portal
        /// </summary>
        public bool IsCommunity { get; set; } = false;

        /// <summary>
        /// Rule trigger type: "single" (matches individual events) or "correlation" (combines multiple event types)
        /// Both types run at the same time during analysis - this field is organizational/descriptive
        /// </summary>
        public string Trigger { get; set; } = "single";

        // ===== MATCHING CONDITIONS =====

        /// <summary>
        /// Optional device-fact gates evaluated BEFORE conditions. ALL preconditions must pass;
        /// if any fails the rule is silently skipped — no result, no UI card. Used to filter out
        /// hardware/OS profiles where a rule does not apply (e.g. "skip on virtual machines").
        /// </summary>
        public List<RulePrecondition> Preconditions { get; set; } = new List<RulePrecondition>();

        /// <summary>
        /// Conditions that must be evaluated against the event stream
        /// All required conditions must match for the rule to fire
        /// </summary>
        public List<RuleCondition> Conditions { get; set; } = new List<RuleCondition>();

        // ===== CONFIDENCE SCORING =====

        /// <summary>
        /// Base confidence score (0-100) when the rule's required conditions match
        /// Additional confidence is added from ConfidenceFactors
        /// </summary>
        public int BaseConfidence { get; set; } = 50;

        /// <summary>
        /// Additional factors that increase confidence when matched
        /// </summary>
        public List<ConfidenceFactor> ConfidenceFactors { get; set; } = new List<ConfidenceFactor>();

        /// <summary>
        /// Minimum confidence score (0-100) to create a RuleResult
        /// Default: 40
        /// </summary>
        public int ConfidenceThreshold { get; set; } = 40;

        // ===== RESULTS =====

        /// <summary>
        /// Detailed explanation of the detected issue
        /// Supports markdown formatting
        /// </summary>
        public string Explanation { get; set; } = default!;

        /// <summary>
        /// Steps to remediate the detected issue
        /// </summary>
        public List<RemediationStep> Remediation { get; set; } = new List<RemediationStep>();

        /// <summary>
        /// Links to relevant documentation
        /// </summary>
        public List<RelatedDoc> RelatedDocs { get; set; } = new List<RelatedDoc>();

        // ===== TEMPLATE =====

        /// <summary>
        /// Template variables that must be customized per-tenant before the rule can be used.
        /// If non-empty, the rule is a template: enabling it creates a tenant custom copy
        /// with the user's values substituted into the conditions.
        /// </summary>
        public List<TemplateVariable> TemplateVariables { get; set; } = new List<TemplateVariable>();

        /// <summary>
        /// If this custom rule was created from a template, stores the original template rule's ID.
        /// Used to track lineage and prevent duplicate copies.
        /// </summary>
        public string? DerivedFromTemplateRuleId { get; set; }

        // ===== METADATA =====

        // ===== SESSION FAILURE POLICY =====

        /// <summary>
        /// Rule-definition default for whether firing this rule should mark the entire session as failed.
        /// Shipped in the rule JSON. A tenant can override this via <see cref="MarkSessionAsFailed"/>
        /// in their RuleState — a firing rule is considered a "KO criterion" for the enrollment when the
        /// effective value (override ?? default) is true.
        /// </summary>
        public bool MarkSessionAsFailedDefault { get; set; } = false;

        /// <summary>
        /// Tenant-scoped override for <see cref="MarkSessionAsFailedDefault"/>. Not persisted in the rule
        /// JSON — populated at load time from the RuleStates table. Null means the tenant has not expressed
        /// a preference (fall back to the default).
        /// </summary>
        public bool? MarkSessionAsFailed { get; set; }

        /// <summary>
        /// Tags for filtering and categorization
        /// </summary>
        public string[] Tags { get; set; } = new string[0];

        /// <summary>
        /// When this rule was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this rule was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A condition that is evaluated against the event stream
    /// </summary>
    public class RuleCondition
    {
        /// <summary>
        /// Descriptive name for this signal (e.g., "proxy_407_error")
        /// </summary>
        public string Signal { get; set; } = default!;

        /// <summary>
        /// Source of the signal: "event_type", "event_data", "phase_duration", "event_count", "app_install_duration", "event_correlation"
        /// </summary>
        public string Source { get; set; } = default!;

        /// <summary>
        /// Event type to match on.
        /// For "event_type"/"event_data": the event type to match.
        /// For "event_correlation": the FIRST event type (Event A).
        /// </summary>
        public string EventType { get; set; } = default!;

        /// <summary>
        /// Data field to match on.
        /// For "event_data": field to check with Operator/Value.
        /// For "event_data_array": the ARRAY field to iterate (e.g. "artifacts").
        /// For "event_correlation": optional filter field on Event B (the second event).
        /// Uses dot notation for nested fields (e.g., "data.errorCode").
        /// </summary>
        public string DataField { get; set; } = default!;

        /// <summary>
        /// For "event_data_array" only: the sub-field on each array element to test with
        /// Operator/Value (e.g. "identity"). When empty, each element is treated as a scalar.
        /// The condition matches when ANY element satisfies the operator (e.g. one artifact whose
        /// identity does not match an allow-list regex).
        /// </summary>
        public string ItemField { get; set; } = default!;

        /// <summary>
        /// Comparison operator: "equals", "not_equals", "contains", "not_contains", "regex", "not_regex", "gt", "lt", "gte", "lte", "exists", "not_exists", "count_gte"
        /// For "event_correlation": operator for the Event B filter (applied to DataField).
        /// </summary>
        public string Operator { get; set; } = default!;

        /// <summary>
        /// Value to compare against.
        /// For "event_correlation": value for the Event B filter.
        /// </summary>
        public string Value { get; set; } = default!;

        /// <summary>
        /// Whether this condition must match for the rule to fire
        /// If false, it only contributes to confidence scoring
        /// </summary>
        public bool Required { get; set; } = false;

        // ===== Event Correlation Properties =====
        // Used only when Source = "event_correlation"

        /// <summary>
        /// The second event type to correlate with (Event B).
        /// Example: "app_install_failed"
        /// </summary>
        public string CorrelateEventType { get; set; } = default!;

        /// <summary>
        /// The data field to join on — must have the same value in both Event A and Event B.
        /// Example: "appId" means both events must share the same appId value.
        /// </summary>
        public string JoinField { get; set; } = default!;

        /// <summary>
        /// Maximum time in seconds between Event A and Event B. Null or 0 means no time limit.
        /// </summary>
        public int? TimeWindowSeconds { get; set; }

        /// <summary>
        /// Optional suppression: if an event of SuppressByEvent.EventType exists
        /// with the same SuppressByEvent.JoinField value as the matched event,
        /// the match is skipped (the "resolving" event suppresses the finding).
        /// Used to prevent rules from firing when a subsequent event resolved the issue.
        /// </summary>
        public SuppressByEventConfig? SuppressByEvent { get; set; }

        /// <summary>
        /// Optional filter field on Event A (the first event).
        /// Combined with EventAFilterOperator and EventAFilterValue.
        /// </summary>
        public string EventAFilterField { get; set; } = default!;

        /// <summary>
        /// Operator for the Event A filter. Uses same operators as the main Operator field.
        /// </summary>
        public string EventAFilterOperator { get; set; } = default!;

        /// <summary>
        /// Value for the Event A filter.
        /// </summary>
        public string EventAFilterValue { get; set; } = default!;
    }

    /// <summary>
    /// A device-fact gate evaluated before a rule's conditions. Pure boolean filter —
    /// does not contribute to evidence or confidence. When any precondition on a rule
    /// fails, the rule is silently skipped (no result emitted). Currently only
    /// <c>event_data</c> source is supported.
    /// </summary>
    public class RulePrecondition
    {
        /// <summary>
        /// Source of the fact. Currently only <c>event_data</c>.
        /// </summary>
        public string Source { get; set; } = "event_data";

        /// <summary>
        /// Event type carrying the field to test (e.g., <c>hardware_spec</c>, <c>os_info</c>).
        /// </summary>
        public string EventType { get; set; } = default!;

        /// <summary>
        /// Data field to test (dot notation supported for nested fields).
        /// </summary>
        public string DataField { get; set; } = default!;

        /// <summary>
        /// Comparison operator. Same vocabulary as <see cref="RuleCondition.Operator"/>
        /// minus the count_/correlation-specific operators.
        /// </summary>
        public string Operator { get; set; } = default!;

        /// <summary>
        /// Value to compare against. Boolean values are stringified (<c>"true"</c>/<c>"false"</c>).
        /// </summary>
        public string Value { get; set; } = default!;

        /// <summary>
        /// Optional human-readable note explaining the intent
        /// (e.g., <c>"skip on virtual machines"</c>). Not evaluated.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// A factor that increases confidence when matched
    /// </summary>
    public class ConfidenceFactor
    {
        /// <summary>
        /// Descriptive name for this factor
        /// </summary>
        public string Signal { get; set; } = default!;

        /// <summary>
        /// Condition expression (e.g., "count >= 5", "exists", "duration > 300")
        /// </summary>
        public string Condition { get; set; } = default!;

        /// <summary>
        /// Confidence weight to add when this factor matches (0-100)
        /// Total confidence = BaseConfidence + sum of matched factor weights, capped at 100
        /// </summary>
        public int Weight { get; set; }
    }

    /// <summary>
    /// A remediation step with title and sub-steps
    /// </summary>
    public class RemediationStep
    {
        /// <summary>
        /// Title of the remediation approach
        /// </summary>
        public string Title { get; set; } = default!;

        /// <summary>
        /// Ordered steps to execute
        /// </summary>
        public List<string> Steps { get; set; } = new List<string>();
    }

    /// <summary>
    /// A link to related documentation
    /// </summary>
    public class RelatedDoc
    {
        /// <summary>
        /// Display title for the link
        /// </summary>
        public string Title { get; set; } = default!;

        /// <summary>
        /// URL to the documentation
        /// </summary>
        public string Url { get; set; } = default!;
    }

    /// <summary>
    /// Configuration for suppressing a condition match when a "resolving" event exists.
    /// Example: suppress an app_install_failed match when app_install_completed exists for the same appId.
    /// </summary>
    public class SuppressByEventConfig
    {
        /// <summary>
        /// The event type that resolves/suppresses the matched event (e.g., "app_install_completed").
        /// </summary>
        public string EventType { get; set; } = default!;

        /// <summary>
        /// The data field to join on — must have the same value in both the matched event
        /// and the suppressing event (e.g., "appId").
        /// </summary>
        public string JoinField { get; set; } = default!;
    }

    /// <summary>
    /// Defines a template variable in a rule condition that must be customized per-tenant.
    /// Points at a specific condition field (by index) and describes what value the user needs to provide.
    /// </summary>
    public class TemplateVariable
    {
        /// <summary>
        /// Machine name for this variable (e.g., "cert_subject")
        /// </summary>
        public string Name { get; set; } = default!;

        /// <summary>
        /// Human-readable label shown in the configuration UI (e.g., "Certificate Subject")
        /// </summary>
        public string Label { get; set; } = default!;

        /// <summary>
        /// Help text explaining what value the user should provide
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Zero-based index into the rule's Conditions array where this variable lives
        /// </summary>
        public int ConditionIndex { get; set; }

        /// <summary>
        /// Which field on the condition to customize: "value", "eventType", "dataField", "eventAFilterValue"
        /// </summary>
        public string Field { get; set; } = "value";

        /// <summary>
        /// The placeholder value that ships with the template (e.g., "CN=YOUR-CERTIFICATE-SUBJECT")
        /// </summary>
        public string Placeholder { get; set; } = default!;

        /// <summary>
        /// Optional regex pattern to validate user input
        /// </summary>
        public string? Validation { get; set; }
    }
}
