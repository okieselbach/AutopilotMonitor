using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// GET /api/sessions/{sessionId}/diagnosis — the session's verdict in one object: what happened
    /// (<see cref="DiagnosisHeadline.Outcome"/>), why (<see cref="DiagnosisHeadline.Cause"/>) and the
    /// evidence behind it. Built on demand from the session row and its events, never from rule
    /// confidence; analyze findings stay a separate list.
    /// </summary>
    public class GetSessionDiagnosisResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string SessionId { get; set; } = default!;
        public DiagnosisHeadline Headline { get; set; } = default!;
    }

    /// <summary>The verdict of one session and the facts it rests on.</summary>
    public class DiagnosisHeadline
    {
        /// <summary>The session status in the diagnosis vocabulary (<see cref="DiagnosisOutcomes"/>).</summary>
        public string Outcome { get; set; } = default!;

        /// <summary>Why the session ended up there (<see cref="DiagnosisCauses"/>).</summary>
        public string Cause { get; set; } = default!;

        /// <summary>The code path that set the status (<see cref="VerdictPaths"/>); derived for rows written before it was stamped.</summary>
        public string VerdictPath { get; set; } = default!;

        /// <summary>
        /// The session's own reason text: the failure, stall or classification reason, or the reconcile
        /// reason of a success. The honest fallback whenever the cause is <c>unknown</c>.
        /// </summary>
        public string? ReasonText { get; set; }

        /// <summary>When the verdict was reached (completion, stall detection or the last event); null while in progress.</summary>
        public DateTime? At { get; set; }

        /// <summary>The analyze rule that marked the session failed (cause <c>rule_ko</c>).</summary>
        public string? RuleId { get; set; }

        /// <summary>The newer session of the same device that replaced this one (cause <c>superseded</c>).</summary>
        public string? SupersededBySessionId { get; set; }

        /// <summary>The ESP step that failed, for an explicit failure.</summary>
        public DiagnosisFailedStep? FailedStep { get; set; }

        /// <summary>What the evidence names as the cause, strongest first; at most five.</summary>
        public List<DiagnosisCulprit>? Culprits { get; set; }

        /// <summary>The last reported state of each ESP phase (Device preparation, Device setup, Account setup).</summary>
        public List<DiagnosisEspCategory>? Esp { get; set; }

        /// <summary>The ESP profile the device reported.</summary>
        public DiagnosisEspConfig? EspConfig { get; set; }

        /// <summary>What the enrollment still waited for, for a session that did not finish.</summary>
        public List<DiagnosisWaitItem>? Waiting { get; set; }

        /// <summary>Limits of the observation that weaken the verdict (<see cref="DiagnosisCaveats"/>).</summary>
        public List<string>? Caveats { get; set; }
    }

    /// <summary>The failed ESP step of an explicit failure.</summary>
    public class DiagnosisFailedStep
    {
        /// <summary>ESP phase: DevicePreparation, DeviceSetup or AccountSetup; null when the failure named none.</summary>
        public string? Category { get; set; }

        /// <summary>ESP subcategory such as Apps, Certificates, SecurityPolicies or NetworkConnections.</summary>
        public string? Subcategory { get; set; }

        /// <summary>The ESP error as lowercase hex HRESULT (0x87d1041c).</summary>
        public string? ErrorCode { get; set; }

        /// <summary>The agent's failure type (Provisioning_AccountSetup_Apps_Failed, ESPProgress_Timeout, WhiteGlove_Failed).</summary>
        public string? FailureType { get; set; }

        /// <summary>The agent's failure reason (esp_terminal_failure, oobe_update_not_finished).</summary>
        public string? Reason { get; set; }

        /// <summary>When the agent reported the failure.</summary>
        public DateTime At { get; set; }

        /// <summary>The event that carries the failure.</summary>
        public string? EventId { get; set; }
    }

    /// <summary>One thing the evidence names as the cause: an app, a Store package or an ESP policy provider.</summary>
    public class DiagnosisCulprit
    {
        /// <summary>What it is (<see cref="DiagnosisCulpritKinds"/>).</summary>
        public string Kind { get; set; } = default!;

        /// <summary>How it was identified (<see cref="DiagnosisCulpritEvidence"/>).</summary>
        public string Evidence { get; set; } = default!;

        /// <summary>Stable identity: the Intune app id, the package full name or the provider name; null when only a name is known.</summary>
        public string? Key { get; set; }

        /// <summary>Display name.</summary>
        public string Name { get; set; } = default!;

        /// <summary>App type as the agent reported it (Win32, MSI, WinGet).</summary>
        public string? AppType { get; set; }

        /// <summary>Assignment intent as the agent reported it (Install, Available).</summary>
        public string? Intent { get; set; }

        /// <summary>Who the app is assigned to as the agent reported it (User, Device, Dependency).</summary>
        public string? Targeted { get; set; }

        /// <summary>The last install state the agent reported (Installing, Error, Downloading, NotInstalled).</summary>
        public string? State { get; set; }

        /// <summary>
        /// The error the app or package reported, as sent: a decimal exit code, a hex HRESULT, or an
        /// HRESULT in the signed decimal form the IME log writes (0x87D1041C arrives as -2016345060).
        /// </summary>
        public string? ErrorCode { get; set; }

        /// <summary>The installer exit code (signed decimal).</summary>
        public string? ExitCode { get; set; }

        /// <summary>How often the install started in this session.</summary>
        public int? InstallStarts { get; set; }

        /// <summary>The last install start.</summary>
        public DateTime? LastStartedAt { get; set; }

        /// <summary>When the failure was reported.</summary>
        public DateTime? FailedAt { get; set; }

        /// <summary>True when the ESP lists the app as blocking; null when that is unknown (never false).</summary>
        public bool? EspBlocking { get; set; }

        /// <summary>Download progress of an app still in flight.</summary>
        public int? ProgressPercent { get; set; }

        /// <summary>The analyzer's confidence for a Store package (high, medium, low).</summary>
        public string? Confidence { get; set; }

        /// <summary>Scope of a policy provider: device, or the user's SID.</summary>
        public string? Scope { get; set; }

        /// <summary>How long a policy provider has been stuck.</summary>
        public double? StalledForMinutes { get; set; }

        /// <summary>The event the evidence comes from.</summary>
        public string? EventId { get; set; }
    }

    /// <summary>The last reported state of one ESP phase.</summary>
    public class DiagnosisEspCategory
    {
        /// <summary>DevicePreparation, DeviceSetup or AccountSetup.</summary>
        public string Category { get; set; } = default!;

        /// <summary>succeeded, failed or in_progress.</summary>
        public string State { get; set; } = default!;

        /// <summary>The steps of the phase in reported order.</summary>
        public List<DiagnosisEspSubcategory> Subcategories { get; set; } = new List<DiagnosisEspSubcategory>();
    }

    /// <summary>One step of an ESP phase.</summary>
    public class DiagnosisEspSubcategory
    {
        /// <summary>Apps, Certificates, SecurityPolicies, NetworkConnections, WaitingForAadRegistration and so on.</summary>
        public string Name { get; set; } = default!;

        /// <summary>The reported state: notStarted, inProgress, succeeded, failed or notRequired.</summary>
        public string State { get; set; } = default!;

        /// <summary>The ESP's own status text.</summary>
        public string? StatusText { get; set; }

        /// <summary>The HRESULT of a failed step (lowercase hex).</summary>
        public string? ErrorCode { get; set; }
    }

    /// <summary>The ESP profile settings the device reported.</summary>
    public class DiagnosisEspConfig
    {
        /// <summary>Users may continue past an error ("Continue anyway").</summary>
        public bool? ContinueAnyway { get; set; }

        /// <summary>Users may retry after an error.</summary>
        public bool? TryAgain { get; set; }

        /// <summary>Users may reset the device after an error.</summary>
        public bool? Reset { get; set; }

        /// <summary>The ESP time limit in minutes.</summary>
        public int? TimeoutMinutes { get; set; }

        /// <summary>The user part of the ESP (Account setup) is skipped.</summary>
        public bool? SkipUserEsp { get; set; }

        /// <summary>The device part of the ESP (Device setup) is skipped.</summary>
        public bool? SkipDeviceEsp { get; set; }

        /// <summary>Distinct apps the ESP lists as blocking; the lists are capped, see the <c>blocking_list_truncated</c> caveat.</summary>
        public int? BlockingAppCount { get; set; }
    }

    /// <summary>Something an unfinished enrollment still waited for.</summary>
    public class DiagnosisWaitItem
    {
        /// <summary>What it is (<see cref="DiagnosisWaitKinds"/>).</summary>
        public string Kind { get; set; } = default!;

        /// <summary>The prerequisite, app or provider name as the agent reported it.</summary>
        public string Name { get; set; } = default!;

        /// <summary>For an app: installing, downloading, pending or likely_stuck.</summary>
        public string? State { get; set; }
    }

    /// <summary>Values of <see cref="DiagnosisHeadline.Outcome"/>: the session status, one to one.</summary>
    public static class DiagnosisOutcomes
    {
        public const string Failed = "failed";
        public const string Stalled = "stalled";
        public const string AwaitingUser = "awaiting_user";
        public const string Incomplete = "incomplete";
        public const string Succeeded = "succeeded";
        public const string InProgress = "in_progress";
        public const string Pending = "pending";
        public const string Unknown = "unknown";

        /// <summary>Every outcome, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Failed, Stalled, AwaitingUser, Incomplete, Succeeded, InProgress, Pending, Unknown };
    }

    /// <summary>Values of <see cref="DiagnosisHeadline.Cause"/>, read from the verdict path and the session's events.</summary>
    public static class DiagnosisCauses
    {
        /// <summary>The agent reported completion.</summary>
        public const string Completed = "completed";

        /// <summary>The ESP failed, the user continued anyway and setup completed.</summary>
        public const string SoftFailure = "soft_failure";

        /// <summary>The platform declared the session complete without a completion report.</summary>
        public const string Reconciled = "reconciled";

        /// <summary>A required app failed during the ESP and the ESP stopped.</summary>
        public const string EspAppFailure = "esp_app_failure";

        /// <summary>The ESP stopped while required apps had not finished (timed out, still installing or never started).</summary>
        public const string EspAppTimeout = "esp_app_timeout";

        /// <summary>An ESP step failed and no app was identified.</summary>
        public const string EspStepFailure = "esp_step_failure";

        /// <summary>Pre-provisioning (the technician phase) failed.</summary>
        public const string WhiteGloveFailure = "whiteglove_failure";

        /// <summary>The agent reported a failure that names no ESP step.</summary>
        public const string AgentFailure = "agent_failure";

        /// <summary>An analyze rule marked the session failed.</summary>
        public const string RuleKo = "rule_ko";

        /// <summary>An administrator set the result.</summary>
        public const string AdminMarked = "admin_marked";

        /// <summary>The agent stopped (maximum lifetime, stopped waiting, emergency break) without a completion signal.</summary>
        public const string AgentGaveUp = "agent_gave_up";

        /// <summary>No completion arrived within the timeout and the server closed the session.</summary>
        public const string SweepTimeout = "sweep_timeout";

        /// <summary>A newer enrollment of the same device replaced this session.</summary>
        public const string Superseded = "superseded";

        /// <summary>Stalled: the agent stopped sending telemetry.</summary>
        public const string AgentSilent = "agent_silent";

        /// <summary>Stalled: the agent reported no progress.</summary>
        public const string NoProgress = "no_progress";

        /// <summary>Device setup or pre-provisioning finished and the enrollment waits for the user.</summary>
        public const string AwaitingUser = "awaiting_user";

        /// <summary>Pre-provisioning finished and the device was sealed for the user.</summary>
        public const string WhiteGloveSealed = "whiteglove_sealed";

        /// <summary>Still running; nothing to explain yet.</summary>
        public const string None = "none";

        /// <summary>The verdict could not be attributed; see the reason text.</summary>
        public const string Unknown = "unknown";

        /// <summary>Every cause, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Completed, SoftFailure, Reconciled, EspAppFailure, EspAppTimeout, EspStepFailure, WhiteGloveFailure,
            AgentFailure, RuleKo, AdminMarked, AgentGaveUp, SweepTimeout, Superseded, AgentSilent, NoProgress,
            AwaitingUser, WhiteGloveSealed, None, Unknown,
        };
    }

    /// <summary>Values of <see cref="DiagnosisCulprit.Kind"/>.</summary>
    public static class DiagnosisCulpritKinds
    {
        public const string App = "app";
        public const string AppxPackage = "appx_package";
        public const string PolicyProvider = "policy_provider";

        /// <summary>Every kind, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { App, AppxPackage, PolicyProvider };
    }

    /// <summary>Values of <see cref="DiagnosisCulprit.Evidence"/>, strongest first.</summary>
    public static class DiagnosisCulpritEvidence
    {
        /// <summary>The app reported an install error itself.</summary>
        public const string InstallFailed = "install_failed";

        /// <summary>The app was installing when the ESP failed with an error code; the agent assigned that error to it.</summary>
        public const string PresumedFailed = "presumed_failed";

        /// <summary>The app was installing when the ESP gave up without a per-app error.</summary>
        public const string PresumedStuck = "presumed_stuck";

        /// <summary>The app was still downloading or installing when the agent stopped.</summary>
        public const string InFlight = "in_flight";

        /// <summary>The app never started installing.</summary>
        public const string NeverStarted = "never_started";

        /// <summary>The ESP tracked the app and it had not completed when the step failed.</summary>
        public const string NotCompleted = "not_completed";

        /// <summary>Only the ESP's own list of likely culprits names the app.</summary>
        public const string NamedByEsp = "named_by_esp";

        /// <summary>The AppX deployment log points at the package.</summary>
        public const string AppxLog = "appx_log";

        /// <summary>An ESP policy provider did not finish.</summary>
        public const string ProviderStalled = "provider_stalled";

        /// <summary>Every kind of evidence, strongest first.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            InstallFailed, PresumedFailed, PresumedStuck, InFlight, NeverStarted, NotCompleted, NamedByEsp, AppxLog, ProviderStalled,
        };
    }

    /// <summary>Values of <see cref="DiagnosisWaitItem.Kind"/>.</summary>
    public static class DiagnosisWaitKinds
    {
        /// <summary>A completion prerequisite the agent still waited for.</summary>
        public const string Prerequisite = "prerequisite";

        /// <summary>An app not yet installed.</summary>
        public const string App = "app";

        /// <summary>An ESP policy provider that did not finish.</summary>
        public const string PolicyProvider = "policy_provider";

        /// <summary>Every kind, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Prerequisite, App, PolicyProvider };
    }

    /// <summary>
    /// Names of a <see cref="DiagnosisWaitKinds.Prerequisite"/> wait item: the completion prerequisites
    /// the decision engine lists in <c>completion_waiting.missingPrerequisites</c>.
    /// </summary>
    public static class DiagnosisPrerequisites
    {
        /// <summary>The account setup phase of the ESP has not reported its end.</summary>
        public const string AccountSetupProvisioningComplete = "account_setup_provisioning_complete";

        /// <summary>Windows Hello has not been set up or skipped.</summary>
        public const string HelloResolution = "hello_resolution";

        /// <summary>The user has not reached the desktop.</summary>
        public const string DesktopArrival = "desktop_arrival";

        /// <summary>RealmJoin has not finished.</summary>
        public const string RealmJoinResolution = "realmjoin_resolution";

        /// <summary>Every prerequisite, the engine's order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { AccountSetupProvisioningComplete, HelloResolution, DesktopArrival, RealmJoinResolution };
    }

    /// <summary>Values of <see cref="DiagnosisHeadline.Caveats"/>.</summary>
    public static class DiagnosisCaveats
    {
        /// <summary>The agent started late; the start of the enrollment was not observed.</summary>
        public const string AgentStartedLate = "agent_started_late";

        /// <summary>An agent run ended in a crash; events of that run may be missing.</summary>
        public const string AgentCrashed = "agent_crashed";

        /// <summary>The verdict path was derived from a row written before it was recorded.</summary>
        public const string VerdictDerived = "verdict_derived";

        /// <summary>The ESP's blocking app lists were capped, so an app missing from them may still be blocking.</summary>
        public const string BlockingListTruncated = "blocking_list_truncated";

        /// <summary>Every caveat, declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { AgentStartedLate, AgentCrashed, VerdictDerived, BlockingListTruncated };
    }
}
