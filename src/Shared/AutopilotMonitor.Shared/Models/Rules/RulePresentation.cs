using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// How a finding of an analyze rule is shown: its role, a one-line summary, what it is about and
    /// its key facts. Every string is a <c>{{token}}</c> template; like the explanation it is stored
    /// raw and resolved against the finding's evidence where it is rendered. A fact whose value was
    /// not recorded is left out.
    /// </summary>
    public class RulePresentation
    {
        /// <summary>What the finding means for the session outcome (<see cref="RulePresentationRoles"/>).</summary>
        public string Role { get; set; } = default!;

        /// <summary>One-line template for the finding row; the first sentence of the explanation stands in when a token was not recorded.</summary>
        public string? Summary { get; set; }

        /// <summary>What the finding is about, so findings about the same app or policy group together.</summary>
        public RuleSubject? Subject { get; set; }

        /// <summary>Key facts shown as label/value pairs, in order (at most six).</summary>
        public List<RuleFact>? Facts { get; set; }
    }

    /// <summary>
    /// What a finding is about. Findings with the same kind and resolved key are grouped. Key and name
    /// use qualified tokens of one signal (<c>{{failed_app.appId}}</c>, <c>{{failed_app.appName}}</c>)
    /// so both come from the same event.
    /// </summary>
    public class RuleSubject
    {
        /// <summary>The kind of thing the key identifies (<see cref="RuleSubjectKinds"/>).</summary>
        public string Kind { get; set; } = default!;

        /// <summary>Template of the grouping key.</summary>
        public string Key { get; set; } = default!;

        /// <summary>Template of the display name; the key is shown when it is missing.</summary>
        public string? Name { get; set; }
    }

    /// <summary>One key fact of a finding.</summary>
    public class RuleFact
    {
        /// <summary>Short label, for example "Install starts".</summary>
        public string Label { get; set; } = default!;

        /// <summary>Template of the value, for example <c>{{install_retry_loop.count}}</c>.</summary>
        public string Value { get; set; } = default!;

        /// <summary>How the value is shown (<see cref="RuleFactFormats"/>); text when absent.</summary>
        public string? Format { get; set; }
    }

    /// <summary>Roles of a finding (<see cref="RulePresentation.Role"/>); the schema enum mirrors <see cref="All"/>.</summary>
    public static class RulePresentationRoles
    {
        /// <summary>Restates the session outcome; shown in the verdict, not as a finding row.</summary>
        public const string Outcome = "outcome";

        /// <summary>Can explain why a session failed or is stuck.</summary>
        public const string Cause = "cause";

        /// <summary>A circumstance such as standby, a restart or battery power.</summary>
        public const string Context = "context";

        /// <summary>Hygiene or security without bearing on the outcome.</summary>
        public const string Advisory = "advisory";

        /// <summary>Every role, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Outcome, Cause, Context, Advisory };
    }

    /// <summary>Subject kinds (<see cref="RuleSubject.Kind"/>); the schema enum mirrors <see cref="All"/>.</summary>
    public static class RuleSubjectKinds
    {
        /// <summary>An app or package.</summary>
        public const string App = "app";

        /// <summary>A configuration or MDM policy.</summary>
        public const string Policy = "policy";

        /// <summary>A user or local account.</summary>
        public const string Account = "account";

        /// <summary>A platform, remediation or health script.</summary>
        public const string Script = "script";

        /// <summary>The device itself (power, clock, hardware, firmware).</summary>
        public const string Device = "device";

        /// <summary>Network, proxy or connectivity.</summary>
        public const string Network = "network";

        /// <summary>An Enrollment Status Page phase or step.</summary>
        public const string Esp = "esp";

        /// <summary>The enrollment session as a whole.</summary>
        public const string Session = "session";

        /// <summary>Every subject kind, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { App, Policy, Account, Script, Device, Network, Esp, Session };
    }

    /// <summary>Fact formats (<see cref="RuleFact.Format"/>); the schema enum mirrors <see cref="All"/>.</summary>
    public static class RuleFactFormats
    {
        /// <summary>Plain text (the default).</summary>
        public const string Text = "text";

        /// <summary>Monospaced, for identifiers and paths.</summary>
        public const string Code = "code";

        /// <summary>An error code; renderers add the error-code catalog meaning.</summary>
        public const string ErrorCode = "errorCode";

        /// <summary>A duration in seconds.</summary>
        public const string Duration = "duration";

        /// <summary>A number.</summary>
        public const string Number = "number";

        /// <summary>An ISO 8601 point in time.</summary>
        public const string Timestamp = "timestamp";

        /// <summary>Every format, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Text, Code, ErrorCode, Duration, Number, Timestamp };
    }

    /// <summary>Limits of a presentation block; the schema carries the same numbers.</summary>
    public static class RulePresentationLimits
    {
        /// <summary>Maximum length of the summary template.</summary>
        public const int SummaryMaxLength = 160;

        /// <summary>Maximum number of facts.</summary>
        public const int MaxFacts = 6;

        /// <summary>Maximum length of a fact label.</summary>
        public const int LabelMaxLength = 40;

        /// <summary>Maximum length of a fact value, subject key or subject name template.</summary>
        public const int TemplateMaxLength = 200;
    }
}
