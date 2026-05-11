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
