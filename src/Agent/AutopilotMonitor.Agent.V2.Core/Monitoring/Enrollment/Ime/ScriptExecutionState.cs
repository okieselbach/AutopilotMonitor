namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;

/// <summary>
/// Accumulates multi-line script execution data from IME logs before emitting
/// a single consolidated event. Used for both platform scripts and remediation scripts.
/// For health scripts (proactive remediations) one source IME log line
/// (<c>[HS] new result = {…}</c>) can produce up to three of these instances —
/// one per phase (pre-detection, remediation, post-detection).
/// </summary>
public class ScriptExecutionState
{
    /// <summary>Intune policy GUID identifying the script.</summary>
    public string PolicyId { get; set; }

    /// <summary>"platform" or "remediation".</summary>
    public string ScriptType { get; set; }

    /// <summary>
    /// UTC timestamp of the script's first observed start line, taken from the source CMTrace log
    /// timestamp (so it dates correctly even when the agent replays historic IME log content that
    /// predates its own launch). For platform scripts: set once at slot creation in
    /// <see cref="ImeLogTracker.HandleScriptStarted"/>. For remediation (health) scripts: captured
    /// from the HS-SCRIPT-START line into a dedicated per-policy map and stamped on every phase
    /// event when the consolidated HS-NEW-RESULT arrives, so the whole-cycle run duration surfaces
    /// the same way. Null when the start line was never seen (e.g. a completion observed without a
    /// matching start). Drives the per-script run-duration surfaced on
    /// <c>script_completed</c>/<c>script_failed</c> and the platform-only
    /// <c>script_timeout_suspected</c> heuristic.
    /// </summary>
    public System.DateTime? StartedAtUtc { get; set; }

    /// <summary>"detection", "remediation", or "post-detection" (remediation scripts only).</summary>
    public string ScriptPart { get; set; }

    /// <summary>"System" or "User" execution context.</summary>
    public string RunContext { get; set; }

    /// <summary>PowerShell exit code.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Standard output (truncated).</summary>
    public string Stdout { get; set; }

    /// <summary>Standard error output (truncated).</summary>
    public string Stderr { get; set; }

    /// <summary>"Success" or "Failed" (platform scripts).</summary>
    public string Result { get; set; }

    /// <summary>
    /// Provenance of <see cref="Result"/> for platform scripts:
    /// <c>"ime_policy_result"</c> — authoritative <c>PS-SCRIPT-RESULT</c> line from
    /// <c>IntuneManagementExtension.log</c>; <c>"agentexecutor_fallback"</c> — derived from the
    /// AgentExecutor.log exit code because IME never logged its policy-result line before the
    /// deadline (short Autopilot enrollments end inside IME's batch-send gap). Surfaced as
    /// <c>resultSource</c> on the emitted event so the UI/MCP can flag fallback-grounded results.
    /// Null for health scripts and for events emitted before this field existed.
    /// </summary>
    public string ResultSource { get; set; }

    /// <summary>
    /// UTC timestamp at which the AgentExecutor.log exit-code line for this platform script was
    /// observed. Drives the deadline check in
    /// <see cref="ImeLogTracker.FlushStalePlatformScriptResults"/> — set only on the platform path
    /// (PS-AGENT exit code); null until an exit code is seen.
    /// </summary>
    public System.DateTime? ExitObservedAtUtc { get; set; }

    /// <summary>"True" or "False" compliance result (remediation detection / post-detection only).</summary>
    public string ComplianceResult { get; set; }

    /// <summary>
    /// IME RemediationStatus enum from the <c>[HS] new result</c> JSON. Known values:
    /// 0 = Unknown, 1 = Compliant, 2 = Remediated, 3 = RemediationFailed,
    /// 4 = NoRemediation (detection-only policy or no remediation script attached).
    /// Set on every event of a health-script cycle (detection / remediation / post-detection).
    /// </summary>
    public int? RemediationStatus { get; set; }

    /// <summary>
    /// Target type from the <c>[HS] new result</c> JSON: 1 = User-targeted, 2 = Device-targeted.
    /// Used by the UI to differentiate user/device remediations independent of <see cref="RunContext"/>.
    /// </summary>
    public int? TargetType { get; set; }

    /// <summary>Top-level <c>ErrorCode</c> field from the <c>[HS] new result</c> JSON (0 = no error).</summary>
    public int? ErrorCode { get; set; }

    /// <summary>Top-level <c>Info.ErrorDetails</c> string from the <c>[HS] new result</c> JSON.</summary>
    public string ErrorDetails { get; set; }
}

/// <summary>
/// Lightweight live-progress signal emitted when IME logs the start of a script
/// (currently only health scripts via the <c>HS-SCRIPT-START</c> pattern).
/// Gives the UI a "running" indicator before the consolidated final result arrives
/// (which may be 30 s – 3 min later).
/// </summary>
public class ScriptStartedInfo
{
    /// <summary>Intune policy GUID.</summary>
    public string PolicyId { get; set; }

    /// <summary>"platform" or "remediation".</summary>
    public string ScriptType { get; set; }

    /// <summary>Numeric IME PolicyType from the start line (captured as string), e.g. "6" for health scripts.</summary>
    public string PolicyType { get; set; }
}
