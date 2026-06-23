using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Wire-format DTO consumed by <c>AutopilotMonitor.SummaryDialog.exe</c>. Plan §4.x M4.6.β.
    /// <para>
    /// Mirrors <c>AutopilotMonitor.SummaryDialog.Models.FinalStatus</c> — the dialog deserializes
    /// this JSON on launch and renders the summary. We cannot reference the dialog project
    /// directly (WPF net48 WinExe), so the shape is duplicated here. Keep the JSON property names
    /// in lockstep with the dialog — changes here require a corresponding update in the dialog.
    /// </para>
    /// </summary>
    public sealed class FinalStatus
    {
        /// <summary>
        /// V2 wire-format version. The dialog branches on this: a missing or <c>1</c> value
        /// triggers the V1 backward-compat render path (legacy "completed"/"failed" outcome
        /// strings, no failure banner, no per-app duration / error detail). V2 emits
        /// <c>2</c> and the dialog uses the richer renderer.
        /// </summary>
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = 2;

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("completionSource")]
        public string CompletionSource { get; set; }

        [JsonProperty("helloOutcome")]
        public string HelloOutcome { get; set; }

        [JsonProperty("enrollmentType")]
        public string EnrollmentType { get; set; }

        [JsonProperty("agentUptimeSeconds")]
        public double AgentUptimeSeconds { get; set; }

        /// <summary>
        /// V2 schema 2 — seconds between device boot and agent start (post-mortem field; the dialog
        /// does not render it). Omitted when boot time was not available at build time. Read with
        /// <see cref="AgentUptimeSeconds"/>: a large value here paired with a small uptime is the
        /// "agent arrived after the enrollment already decided" signature (see
        /// <see cref="LowObservationCoverage"/>).
        /// </summary>
        [JsonProperty("bootToAgentStartSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public double? BootToAgentStartSeconds { get; set; }

        /// <summary>
        /// V2 schema 2 — true when the agent started late AND lived only briefly before terminating,
        /// i.e. it had near-zero coverage of the actual enrollment/failure window and its diagnosis
        /// is a post-mortem of the end-state. Emitted only when true (NullValueHandling.Ignore keeps
        /// normal sessions clean). Mirrors the <c>agent_late_start</c> event.
        /// </summary>
        [JsonProperty("lowObservationCoverage", NullValueHandling = NullValueHandling.Ignore)]
        public bool? LowObservationCoverage { get; set; }

        [JsonProperty("signalsSeen")]
        public List<string> SignalsSeen { get; set; } = new List<string>();

        /// <summary>
        /// V2 schema 2 — human-readable explanation rendered as a banner under the outcome
        /// header. Set only for non-success outcomes (failed / timed_out). Examples:
        /// "ESP failed: 0x800705B4 (provisioning timeout)", "Enrollment exceeded the 6-hour
        /// time limit", "Windows Hello provisioning timed out".
        /// </summary>
        [JsonProperty("failureReason", NullValueHandling = NullValueHandling.Ignore)]
        public string FailureReason { get; set; }

        /// <summary>
        /// V2 schema 2 — when each milestone signal was observed (ISO-8601). V1 wrote this
        /// too; V2 reinstates it for parity. Used by field engineers reading the JSON
        /// post-mortem; the dialog itself does not render it.
        /// </summary>
        [JsonProperty("signalTimestamps", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> SignalTimestamps { get; set; }

        /// <summary>
        /// V2 schema 2 — IME pattern that drove the engine's terminal classification, if any.
        /// Surfaced as a top-level field (rather than encoded into <see cref="SignalsSeen"/>)
        /// so the on-disk JSON matches the on-the-wire <c>imePatternMatchedPatternId</c> field
        /// in the audit trail. Absent when no IME pattern was matched.
        /// </summary>
        [JsonProperty("imeMatchedPatternId", NullValueHandling = NullValueHandling.Ignore)]
        public string ImeMatchedPatternId { get; set; }

        [JsonProperty("appSummary")]
        public FinalStatusAppSummary AppSummary { get; set; } = new FinalStatusAppSummary();

        [JsonProperty("packageStatesByPhase")]
        public Dictionary<string, List<FinalStatusPackageInfo>> PackageStatesByPhase { get; set; } =
            new Dictionary<string, List<FinalStatusPackageInfo>>();
    }

    public sealed class FinalStatusAppSummary
    {
        [JsonProperty("totalApps")]
        public int TotalApps { get; set; }

        [JsonProperty("completedApps")]
        public int CompletedApps { get; set; }

        [JsonProperty("errorCount")]
        public int ErrorCount { get; set; }

        [JsonProperty("deviceErrors")]
        public int DeviceErrors { get; set; }

        [JsonProperty("userErrors")]
        public int UserErrors { get; set; }

        [JsonProperty("appsByPhase")]
        public Dictionary<string, int> AppsByPhase { get; set; } = new Dictionary<string, int>();
    }

    public sealed class FinalStatusPackageInfo
    {
        [JsonProperty("appName")]
        public string AppName { get; set; }

        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("isError")]
        public bool IsError { get; set; }

        [JsonProperty("isCompleted")]
        public bool IsCompleted { get; set; }

        [JsonProperty("targeted")]
        public string Targeted { get; set; }

        // Plan §5 Fix 4a / 4c — per-app install-lifecycle timestamps. Omitted from the
        // JSON when not yet captured (e.g. agent started mid-install), so the dialog can
        // distinguish "not tracked" from "stamp=epoch".
        [JsonProperty("startedAt", NullValueHandling = NullValueHandling.Ignore)]
        public string StartedAt { get; set; }

        [JsonProperty("completedAt", NullValueHandling = NullValueHandling.Ignore)]
        public string CompletedAt { get; set; }

        [JsonProperty("durationSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public double? DurationSeconds { get; set; }

        // Schema 2 — per-app failure detail. Populated only for apps with
        // InstallationState=Error so the V2 dialog can render the "why" beneath the app row.
        [JsonProperty("errorPatternId", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorPatternId { get; set; }

        [JsonProperty("errorDetail", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorDetail { get; set; }

        [JsonProperty("errorCode", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorCode { get; set; }
    }
}
