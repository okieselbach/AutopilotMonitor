using System.Reflection;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Functions.Services.Diagnosis;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Scenario tests for the diagnosis headline: one session row plus synthetic events shaped like the
/// agent's payloads (app events carry strings, ESP and config events typed values) per verdict path
/// and failure class. The outcome is the status; the cause comes from the verdict path, and for an
/// explicit failure from the ESP evidence.
/// </summary>
public class DiagnosisHeadlineBuilderTests
{
    private const string NotesId = "00000000-0000-4000-8000-0000000000a1";
    private const string VpnId = "00000000-0000-4000-8000-0000000000a2";
    private const string AgentId = "00000000-0000-4000-8000-0000000000a3";
    private const string PortalId = "00000000-0000-4000-8000-0000000000a4";
    private static readonly DateTime T0 = new(2026, 10, 13, 13, 55, 0, DateTimeKind.Utc);

    // ── Events ───────────────────────────────────────────────────────────

    private sealed class Timeline
    {
        private readonly List<EnrollmentEvent> _events = new();
        private long _sequence;

        public Timeline Add(string type, Dictionary<string, object>? data = null, double minute = -1)
        {
            _sequence++;
            _events.Add(new EnrollmentEvent
            {
                EventId = $"evt-{_sequence}",
                EventType = type,
                Sequence = _sequence,
                Timestamp = T0.AddMinutes(minute >= 0 ? minute : _sequence),
                Data = data ?? new Dictionary<string, object>(),
            });
            return this;
        }

        public IReadOnlyList<EnrollmentEvent> Events => _events;
    }

    private static Dictionary<string, object> App(string appId, string appName, params (string Key, object Value)[] extra)
    {
        var data = new Dictionary<string, object>
        {
            ["appId"] = appId, ["appName"] = appName, ["intent"] = "Install", ["targeted"] = "User", ["appType"] = "WinGet",
        };
        foreach (var (key, value) in extra) data[key] = value;
        return data;
    }

    private static Dictionary<string, object> EspConfig(bool continueAnyway = false, params string[] blockingIds) => new()
    {
        ["allowContinueAnyway"] = continueAnyway,
        ["allowTryAgain"] = true,
        ["allowReset"] = false,
        ["syncFailureTimeoutMinutes"] = 40L,
        ["skipUserStatusPage"] = false,
        ["skipDeviceStatusPage"] = false,
        ["espTrackedWin32AppIds"] = blockingIds.Cast<object>().ToList(),
        ["espTrackedWin32Count"] = (long)blockingIds.Length,
    };

    private static Dictionary<string, object> Failed(string category, string subcategory, string? errorCode = null, string? culprits = null)
    {
        var data = new Dictionary<string, object>
        {
            ["reason"] = "esp_terminal_failure",
            ["failureType"] = $"Provisioning_{category}_{subcategory}_Failed",
            ["category"] = category,
            ["failedSubcategory"] = subcategory,
        };
        if (errorCode != null) data["errorCode"] = errorCode;
        if (culprits != null) data["likelyCulpritApps"] = culprits;
        return data;
    }

    private static SessionSummary Session(SessionStatus status, string? verdictPath, string failureReason = "", string reconcileReason = "")
    {
        var terminal = status is SessionStatus.Succeeded or SessionStatus.Failed or SessionStatus.Incomplete;
        return new SessionSummary
        {
            SessionId = "00000000-0000-4000-8000-000000000001",
            TenantId = "00000000-0000-4000-8000-000000000002",
            Status = status,
            VerdictPath = verdictPath,
            FailureReason = failureReason,
            ReconcileReason = reconcileReason,
            StartedAt = T0,
            CompletedAt = terminal ? T0.AddMinutes(50) : null,
            LastEventAt = T0.AddMinutes(49),
        };
    }

    // ── Explicit ESP failures ────────────────────────────────────────────

    [Fact]
    public void An_app_that_failed_with_the_esp_error_is_the_cause_with_its_facts()
    {
        var events = new Timeline()
            .Add("esp_config_detected", EspConfig(false, NotesId))
            .Add("app_install_started", App(PortalId, "Contoso Portal", ("targeted", "Device")))
            .Add("app_install_completed", App(PortalId, "Contoso Portal", ("state", "Installed"), ("targeted", "Device")))
            .Add("app_install_started", App(NotesId, "Contoso Notes", ("state", "Installing")))
            .Add("app_install_started", App(NotesId, "Contoso Notes", ("state", "Installing")))
            .Add("app_install_started", App(NotesId, "Contoso Notes", ("state", "Installing")))
            .Add("app_install_started", App(NotesId, "Contoso Notes", ("state", "Installing")))
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("state", "Error"), ("errorCode", "0x80d03805"), ("detectionResult", "NotDetected")))
            .Add("esp_provisioning_status", new()
            {
                ["category"] = "AccountSetup",
                ["categorySucceeded"] = "False",
                ["subcategories"] = new Dictionary<string, object>
                {
                    ["Certificates"] = new Dictionary<string, object> { ["state"] = "succeeded", ["statusText"] = "Complete" },
                    ["Apps"] = new Dictionary<string, object> { ["state"] = "failed", ["statusText"] = "Failed: 0x80d03805" },
                },
                ["failedSubcategories"] = "Apps",
                ["failedSubcategoryErrorCode"] = "0x80d03805",
            })
            .Add("esp_provisioning_status", new()
            {
                ["category"] = "DeviceSetup",
                ["categorySucceeded"] = "True",
                ["subcategories"] = new Dictionary<string, object> { ["Apps"] = new Dictionary<string, object> { ["state"] = "succeeded" } },
            })
            .Add("esp_failure_settle_started", new() { ["category"] = "AccountSetup", ["failedSubcategory"] = "Apps", ["errorCode"] = "0x80d03805" })
            .Add("enrollment_failed", Failed("AccountSetup", "Apps", "0x80d03805", "Contoso Notes"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed, "enrollment_failed: esp_terminal_failure (0x80d03805)"), events.Events);

        Assert.Equal(DiagnosisOutcomes.Failed, h.Outcome);
        Assert.Equal(DiagnosisCauses.EspAppFailure, h.Cause);
        Assert.Equal(VerdictPaths.AgentFailed, h.VerdictPath);
        Assert.Equal("enrollment_failed: esp_terminal_failure (0x80d03805)", h.ReasonText);
        Assert.Equal(T0.AddMinutes(50), h.At);

        Assert.NotNull(h.FailedStep);
        Assert.Equal("AccountSetup", h.FailedStep!.Category);
        Assert.Equal("Apps", h.FailedStep.Subcategory);
        Assert.Equal("0x80d03805", h.FailedStep.ErrorCode);
        Assert.Equal("esp_terminal_failure", h.FailedStep.Reason);

        // The ESP's own culprit name is the same app — one row, the stronger evidence.
        var culprit = Assert.Single(h.Culprits!);
        Assert.Equal(DiagnosisCulpritKinds.App, culprit.Kind);
        Assert.Equal(DiagnosisCulpritEvidence.InstallFailed, culprit.Evidence);
        Assert.Equal(NotesId, culprit.Key);
        Assert.Equal("Contoso Notes", culprit.Name);
        Assert.Equal(4, culprit.InstallStarts);
        Assert.Equal("Install", culprit.Intent);
        Assert.Equal("User", culprit.Targeted);
        Assert.Equal("WinGet", culprit.AppType);
        Assert.Equal("0x80d03805", culprit.ErrorCode);
        Assert.True(culprit.EspBlocking);

        // Phases in ESP order; the failed step carries the code.
        Assert.Equal(new[] { "DeviceSetup", "AccountSetup" }, h.Esp!.Select(p => p.Category));
        var account = h.Esp![1];
        Assert.Equal("failed", account.State);
        Assert.Equal("0x80d03805", account.Subcategories.Single(s => s.Name == "Apps").ErrorCode);
        Assert.Null(account.Subcategories.Single(s => s.Name == "Certificates").ErrorCode);
        Assert.Equal("succeeded", h.Esp![0].State);

        Assert.False(h.EspConfig!.ContinueAnyway);
        Assert.True(h.EspConfig.TryAgain);
        Assert.Equal(40, h.EspConfig.TimeoutMinutes);
        Assert.Equal(1, h.EspConfig.BlockingAppCount);

        Assert.Null(h.Waiting);
        Assert.Null(h.Caveats);
    }

    [Fact]
    public void Without_a_failed_app_the_esp_timed_out_on_the_unfinished_ones()
    {
        var events = new Timeline()
            .Add("app_install_started", App(VpnId, "Contoso VPN", ("targeted", "Device")))
            .Add("app_install_starved", App(AgentId, "Contoso Agent", ("state", "NotInstalled"), ("targeted", "Device")))
            .Add("app_install_failed", App("00000000-0000-4000-8000-0000000000a9", "Contoso Catalog", ("intent", "Available"), ("errorCode", "1603")))
            .Add("enrollment_failed", Failed("DeviceSetup", "Apps"))
            .Add("esp_apps_failure_correlation", new()
            {
                ["failedSubcategory"] = "Apps",
                ["category"] = "DeviceSetup",
                ["errorCode"] = "",
                ["likelyCauseApps"] = new List<object>
                {
                    new Dictionary<string, object> { ["appId"] = VpnId, ["appName"] = "Contoso VPN", ["state"] = "Installing", ["progressPercent"] = 40L },
                },
            })
            .Add("app_install_failed", App(VpnId, "Contoso VPN", ("targeted", "Device"), ("confidence", "presumed"), ("failureType", "esp_apps_timeout")));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Equal(DiagnosisCauses.EspAppTimeout, h.Cause);
        Assert.Collection(h.Culprits!,
            c =>
            {
                // Promoted at the failure; the in-flight row for the same app folds into it.
                Assert.Equal(VpnId, c.Key);
                Assert.Equal(DiagnosisCulpritEvidence.PresumedStuck, c.Evidence);
            },
            c =>
            {
                Assert.Equal(AgentId, c.Key);
                Assert.Equal(DiagnosisCulpritEvidence.NeverStarted, c.Evidence);
                Assert.Equal("NotInstalled", c.State);
            });
        // An Available app's failure never blocks the ESP.
        Assert.DoesNotContain(h.Culprits!, c => c.Name == "Contoso Catalog");
    }

    [Fact]
    public void Failed_apps_rank_the_esp_error_first_and_an_app_that_installed_later_is_no_culprit()
    {
        var events = new Timeline()
            .Add("app_install_failed", App(VpnId, "Contoso VPN", ("exitCode", "1603"), ("errorCode", "1603")))
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("errorCode", "-2147023278")))
            .Add("app_install_failed", App(PortalId, "Contoso Portal", ("errorCode", "1618")))
            .Add("app_install_started", App(PortalId, "Contoso Portal"))
            .Add("app_install_completed", App(PortalId, "Contoso Portal", ("state", "Installed")))
            .Add("enrollment_failed", Failed("AccountSetup", "Apps", "0x80070652"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Equal(DiagnosisCauses.EspAppFailure, h.Cause);
        // -2147023278 is 0x80070652 in decimal: the same error as the ESP's.
        Assert.Equal(new[] { NotesId, VpnId }, h.Culprits!.Select(c => c.Key));
        Assert.Equal("1603", h.Culprits![1].ExitCode);
    }

    [Fact]
    public void IMEs_own_detection_failure_report_matches_the_esp_not_detected_error()
    {
        // IME's report delta carries EnforcementErrorCode as the signed decimal of 0x87D1041C;
        // hresultFromWin32 and exitCode stay "0".
        var events = new Timeline()
            .Add("app_install_failed", App(VpnId, "Contoso VPN", ("errorCode", "1603")))
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("state", "Error"), ("errorCode", "-2016345060"), ("detectionResult", "NotDetected"), ("exitCode", "0"), ("hresultFromWin32", "0")))
            .Add("app_install_failed", App(PortalId, "Contoso Portal", ("errorCode", "1618")))
            .Add("enrollment_failed", Failed("AccountSetup", "Apps", "0x87d1041c"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Equal(DiagnosisCauses.EspAppFailure, h.Cause);
        var first = h.Culprits![0];
        Assert.Equal(NotesId, first.Key);
        Assert.Equal("-2016345060", first.ErrorCode);
        Assert.Equal("0", first.ExitCode);
    }

    [Fact]
    public void The_check_before_the_install_does_not_match_the_esp_not_detected_error()
    {
        // detectionResult NotDetected is IME's check BEFORE the install: every failed app carries
        // it. A download error must not be ranked as the ESP's detection failure (D-392).
        var events = new Timeline()
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("state", "Error"), ("errorCode", "-2145091577"), ("detectionResult", "NotDetected")))
            .Add("app_install_failed", App(PortalId, "Contoso Portal", ("errorCode", "1618")))
            .Add("enrollment_failed", Failed("AccountSetup", "Apps", "0x87d1041c"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        // No app carries the ESP's code, so the latest failure leads.
        Assert.Equal(new[] { PortalId, NotesId }, h.Culprits!.Select(c => c.Key));
    }

    [Fact]
    public void A_step_other_than_apps_fails_without_app_culprits()
    {
        var events = new Timeline()
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("errorCode", "1603")))
            .Add("enrollment_failed", Failed("DeviceSetup", "Certificates", "0x80070005"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Equal(DiagnosisCauses.EspStepFailure, h.Cause);
        Assert.Equal("Certificates", h.FailedStep!.Subcategory);
        Assert.Equal("0x80070005", h.FailedStep.ErrorCode);
        Assert.Null(h.Culprits);
    }

    [Fact]
    public void The_expired_observation_failure_takes_its_step_from_the_advisory()
    {
        var events = new Timeline()
            .Add("esp_failure_advisory", Failed("AccountSetup", "Apps", "0x87D1041C", "Contoso Notes"))
            .Add("enrollment_failed", new() { ["reason"] = "esp_terminal_failure", ["advisoryReason"] = "advisory_completion_window_expired_without_completion_evidence" });

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Equal("AccountSetup", h.FailedStep!.Category);
        Assert.Equal("Apps", h.FailedStep.Subcategory);
        Assert.Equal("0x87d1041c", h.FailedStep.ErrorCode);
        // Only the ESP's own list names the app: unfinished, not failed.
        Assert.Equal(DiagnosisCauses.EspAppTimeout, h.Cause);
        var culprit = Assert.Single(h.Culprits!);
        Assert.Equal(DiagnosisCulpritEvidence.NamedByEsp, culprit.Evidence);
        Assert.Null(culprit.Key);
    }

    [Fact]
    public void A_shell_core_failure_without_enrollment_failed_is_an_esp_step_failure()
    {
        var events = new Timeline()
            .Add("esp_failure", new() { ["failureType"] = "ESPProgress_Timeout" });

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentEspFailureFallback, "ESP (Enrollment Status Page) reported a failure: ESPProgress_Timeout"), events.Events);

        Assert.Equal(DiagnosisCauses.EspStepFailure, h.Cause);
        Assert.Equal("ESPProgress_Timeout", h.FailedStep!.FailureType);
    }

    [Fact]
    public void A_failure_without_an_esp_step_is_the_agents_own()
    {
        var events = new Timeline()
            .Add("enrollment_failed", new() { ["reason"] = "oobe_update_not_finished", ["advisoryReason"] = "oobe_update_cap_expired_without_restart" });

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed, "enrollment_failed: oobe_update_not_finished"), events.Events);

        Assert.Equal(DiagnosisCauses.AgentFailure, h.Cause);
        Assert.Equal("oobe_update_not_finished", h.FailedStep!.Reason);
        Assert.Null(h.Culprits);
    }

    [Fact]
    public void A_pre_provisioning_failure_is_a_whiteglove_failure_with_its_culprits()
    {
        var failed = Failed("DeviceSetup", "Apps", "0x80070652");
        failed["failureType"] = "WhiteGlove_Failed";
        var events = new Timeline()
            .Add("app_install_failed", App(VpnId, "Contoso VPN", ("targeted", "Device"), ("errorCode", "0x80070652")))
            .Add("enrollment_failed", failed);

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Equal(DiagnosisCauses.WhiteGloveFailure, h.Cause);
        Assert.Equal(VpnId, Assert.Single(h.Culprits!).Key);
    }

    [Fact]
    public void The_sweep_r1_explicit_failure_is_read_from_the_evidence_like_the_agents()
    {
        var events = new Timeline()
            .Add("enrollment_failed", Failed("DeviceSetup", "SecurityPolicies", "0x80070002"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, "maxlife:r1"), events.Events);

        Assert.Equal(DiagnosisCauses.EspStepFailure, h.Cause);
        Assert.Equal("SecurityPolicies", h.FailedStep!.Subcategory);
    }

    [Fact]
    public void An_app_policy_provider_that_never_finished_is_a_culprit()
    {
        var events = new Timeline()
            .Add("esp_policy_provider_stalled", new()
            {
                ["reason"] = "provider_incomplete",
                ["stalledProviders"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = "Sidecar", ["scope"] = "device", ["kind"] = "setupApps", ["stalledForMinutes"] = 15.5 },
                },
            })
            .Add("enrollment_failed", Failed("DeviceSetup", "Apps"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        var provider = Assert.Single(h.Culprits!);
        Assert.Equal(DiagnosisCulpritKinds.PolicyProvider, provider.Kind);
        Assert.Equal("Sidecar", provider.Name);
        Assert.Equal(15.5, provider.StalledForMinutes);
        // A provider is no app: the step failed without an app to blame.
        Assert.Equal(DiagnosisCauses.EspStepFailure, h.Cause);
    }

    [Fact]
    public void A_device_preparation_provider_is_no_culprit_for_a_later_apps_failure()
    {
        // A ConfigMgr client installed as a Win32 app registers its device-preparation provider
        // after device preparation ended; its stall explains no device-setup failure.
        var events = new Timeline()
            .Add("esp_policy_provider_stalled", new()
            {
                ["reason"] = "provider_incomplete",
                ["stalledProviders"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = "ConfigMgr", ["scope"] = "device", ["kind"] = "devicePreparation", ["stalledForMinutes"] = 15.0 },
                },
            })
            .Add("enrollment_failed", Failed("DeviceSetup", "Apps"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Null(h.Culprits);
    }

    [Fact]
    public void Only_device_preparation_providers_explain_a_device_preparation_failure()
    {
        // Field shape: the ESP never left device preparation (0x800705b4).
        var events = new Timeline()
            .Add("esp_policy_provider_stalled", new()
            {
                ["reason"] = "sidecar_provider_missing",
                ["stalledProviders"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = "ConfigMgr", ["scope"] = "device", ["kind"] = "devicePreparation", ["stalledForMinutes"] = 40.0 },
                    new Dictionary<string, object> { ["name"] = "Contoso", ["scope"] = "device", ["kind"] = "setupApps", ["stalledForMinutes"] = 40.0 },
                },
            })
            .Add("enrollment_failed", Failed("DevicePreparation", "EspProviderInstallation", "0x800705b4"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        var provider = Assert.Single(h.Culprits!);
        Assert.Equal(DiagnosisCulpritKinds.PolicyProvider, provider.Kind);
        Assert.Equal("ConfigMgr", provider.Name);
        Assert.Equal(40.0, provider.StalledForMinutes);
    }

    [Fact]
    public void The_appx_log_candidate_joins_the_culprits()
    {
        var events = new Timeline()
            .Add("enrollment_failed", Failed("AccountSetup", "Apps", "0x80073cf3"))
            .Add("esp_appx_failure_analysis", new()
            {
                ["verdict"] = "appx_candidate_identified",
                ["confidence"] = "high",
                ["topCandidatePackage"] = "Contoso.Notes_1.0.0.0_x64__abcdefghijklm",
                ["topCandidatePackageName"] = "Contoso.Notes",
                ["topCandidateHresult"] = "0x80073cf3",
            });

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        var package = Assert.Single(h.Culprits!);
        Assert.Equal(DiagnosisCulpritKinds.AppxPackage, package.Kind);
        Assert.Equal("Contoso.Notes", package.Name);
        Assert.Equal("high", package.Confidence);
    }

    [Fact]
    public void An_administrator_preempting_through_the_agent_is_admin_marked()
    {
        var events = new Timeline()
            .Add("enrollment_failed", new() { ["reason"] = "Session failed by administrator (detected on register-session)." });

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.Equal(DiagnosisCauses.AdminMarked, h.Cause);
        Assert.Null(h.FailedStep);
    }

    // ── Verdict paths that state the cause ───────────────────────────────

    [Theory]
    [InlineData(SessionStatus.Failed, "rule:ANALYZE-APP-013", DiagnosisCauses.RuleKo)]
    [InlineData(SessionStatus.Failed, VerdictPaths.ManualFailed, DiagnosisCauses.AdminMarked)]
    [InlineData(SessionStatus.Succeeded, VerdictPaths.ManualSucceeded, DiagnosisCauses.AdminMarked)]
    [InlineData(SessionStatus.Succeeded, VerdictPaths.AgentComplete, DiagnosisCauses.Completed)]
    [InlineData(SessionStatus.Succeeded, VerdictPaths.AgentGatherComplete, DiagnosisCauses.Completed)]
    [InlineData(SessionStatus.Succeeded, "sweep:r4", DiagnosisCauses.Reconciled)]
    [InlineData(SessionStatus.Succeeded, "late:r2", DiagnosisCauses.Reconciled)]
    [InlineData(SessionStatus.Succeeded, "sweep:r5_assumed", DiagnosisCauses.Reconciled)]
    [InlineData(SessionStatus.Succeeded, "sweep:r1b_succeeded", DiagnosisCauses.Reconciled)]
    [InlineData(SessionStatus.Succeeded, "sweep:r1c", DiagnosisCauses.Reconciled)]
    [InlineData(SessionStatus.Succeeded, VerdictPaths.SweepSelfDeployingReconcile, DiagnosisCauses.Reconciled)]
    [InlineData(SessionStatus.Succeeded, VerdictPaths.RetroSelfDeployingReconcile, DiagnosisCauses.Reconciled)]
    [InlineData(SessionStatus.Incomplete, "sweep:r6", DiagnosisCauses.SweepTimeout)]
    [InlineData(SessionStatus.Incomplete, "retro:r5_incomplete", DiagnosisCauses.SweepTimeout)]
    [InlineData(SessionStatus.Incomplete, "legacy:r6", DiagnosisCauses.SweepTimeout)]
    [InlineData(SessionStatus.Incomplete, "maxlife:r5_incomplete", DiagnosisCauses.AgentGaveUp)]
    [InlineData(SessionStatus.Incomplete, "stopwait:r6", DiagnosisCauses.AgentGaveUp)]
    [InlineData(SessionStatus.Incomplete, "sweep:r3", DiagnosisCauses.AgentGaveUp)]
    [InlineData(SessionStatus.Incomplete, VerdictPaths.RegisterSuperseded, DiagnosisCauses.Superseded)]
    [InlineData(SessionStatus.Incomplete, VerdictPaths.RetroSuperseded, DiagnosisCauses.Superseded)]
    [InlineData(SessionStatus.Stalled, VerdictPaths.SweepStalled, DiagnosisCauses.AgentSilent)]
    [InlineData(SessionStatus.Stalled, VerdictPaths.AgentStallProbe, DiagnosisCauses.NoProgress)]
    [InlineData(SessionStatus.AwaitingUser, VerdictPaths.IngestWhiteGloveAwaiting, DiagnosisCauses.AwaitingUser)]
    [InlineData(SessionStatus.AwaitingUser, VerdictPaths.SweepWhiteGloveAwaiting, DiagnosisCauses.AwaitingUser)]
    [InlineData(SessionStatus.AwaitingUser, "sweep:r5_awaiting", DiagnosisCauses.AwaitingUser)]
    [InlineData(SessionStatus.AwaitingUser, "sweep:r1b_awaiting", DiagnosisCauses.AwaitingUser)]
    [InlineData(SessionStatus.Pending, VerdictPaths.AgentWhiteGlovePending, DiagnosisCauses.WhiteGloveSealed)]
    [InlineData(SessionStatus.InProgress, VerdictPaths.RegisterNew, DiagnosisCauses.None)]
    [InlineData(SessionStatus.InProgress, VerdictPaths.AgentStallHeal, DiagnosisCauses.None)]
    [InlineData(SessionStatus.InProgress, VerdictPaths.AgentWhiteGloveResumed, DiagnosisCauses.None)]
    public void The_verdict_path_states_the_cause(SessionStatus status, string path, string cause)
    {
        var h = DiagnosisHeadlineBuilder.Build(Session(status, path), Array.Empty<EnrollmentEvent>());

        Assert.Equal(cause, h.Cause);
        Assert.Equal(path, h.VerdictPath);
        Assert.Null(h.FailedStep);
    }

    [Fact]
    public void Every_cause_and_outcome_is_in_its_vocabulary()
    {
        Assert.Equal(DiagnosisOutcomes.All.Count, DiagnosisOutcomes.All.Distinct().Count());
        foreach (SessionStatus status in Enum.GetValues(typeof(SessionStatus)))
            Assert.Contains(DiagnosisHeadlineBuilder.OutcomeOf(status), DiagnosisOutcomes.All);
        Assert.Equal(DiagnosisCauses.All.Count, DiagnosisCauses.All.Distinct().Count());
    }

    [Fact]
    public void A_rule_ko_names_the_rule()
    {
        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, "rule:ANALYZE-APP-013", "Rule: App Detection Failure During ESP"), Array.Empty<EnrollmentEvent>());

        Assert.Equal("ANALYZE-APP-013", h.RuleId);
        Assert.Equal("Rule: App Detection Failure During ESP", h.ReasonText);
    }

    [Fact]
    public void A_superseded_session_names_its_successor()
    {
        var h = DiagnosisHeadlineBuilder.Build(
            Session(SessionStatus.Incomplete, VerdictPaths.RegisterSuperseded,
                "Superseded by session 00000000-0000-4000-8000-0000000000FF: the device registered a new enrollment."),
            Array.Empty<EnrollmentEvent>());

        Assert.Equal(DiagnosisCauses.Superseded, h.Cause);
        Assert.Equal("00000000-0000-4000-8000-0000000000ff", h.SupersededBySessionId);
    }

    [Fact]
    public void The_agent_timeout_is_the_agent_giving_up_and_lists_what_it_waited_for()
    {
        var events = new Timeline()
            .Add("completion_waiting", new() { ["missingPrerequisites"] = "desktop_arrival,hello_resolution" })
            .Add("app_tracking_summary", new()
            {
                ["installingNames"] = new List<object> { "Contoso Notes" },
                ["pendingNames"] = new List<object> { "Contoso Agent", "Contoso Notes" },
            })
            .Add("enrollment_failed", new() { ["failureType"] = "agent_timeout", ["failureSource"] = "max_lifetime_timer" });

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Incomplete, "maxlife:r5_incomplete", "No completion signal within 48h grace"), events.Events);

        Assert.Equal(DiagnosisOutcomes.Incomplete, h.Outcome);
        Assert.Equal(DiagnosisCauses.AgentGaveUp, h.Cause);
        Assert.Null(h.FailedStep);
        Assert.Collection(h.Waiting!,
            w => { Assert.Equal(DiagnosisWaitKinds.Prerequisite, w.Kind); Assert.Equal(DiagnosisPrerequisites.DesktopArrival, w.Name); },
            w => Assert.Equal(DiagnosisPrerequisites.HelloResolution, w.Name),
            w => { Assert.Equal(DiagnosisWaitKinds.App, w.Kind); Assert.Equal("Contoso Notes", w.Name); Assert.Equal("installing", w.State); },
            w => { Assert.Equal("Contoso Agent", w.Name); Assert.Equal("pending", w.State); });
    }

    [Fact]
    public void The_prerequisite_names_are_the_decision_engines_literals()
    {
        // The engine's literal class is internal to DecisionCore; the portal's wait labels key on this list.
        var literals = typeof(DecisionEngine).GetNestedType("CompletionPrerequisites", BindingFlags.NonPublic)!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(s => s, StringComparer.Ordinal);

        Assert.Equal(literals, DiagnosisPrerequisites.All.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void A_stalled_session_is_dated_by_its_stall_and_lists_stuck_providers()
    {
        var session = Session(SessionStatus.Stalled, VerdictPaths.SweepStalled, "Agent silent for 130min (detected by maintenance sweep)");
        session.StalledAt = T0.AddMinutes(130);
        var events = new Timeline()
            .Add("esp_policy_provider_stalled", new()
            {
                ["stalledProviders"] = new List<object> { new Dictionary<string, object> { ["name"] = "Sidecar", ["scope"] = "device" } },
            });

        var h = DiagnosisHeadlineBuilder.Build(session, events.Events);

        Assert.Equal(DiagnosisCauses.AgentSilent, h.Cause);
        Assert.Equal(T0.AddMinutes(130), h.At);
        var item = Assert.Single(h.Waiting!);
        Assert.Equal(DiagnosisWaitKinds.PolicyProvider, item.Kind);
        Assert.Equal("Sidecar", item.Name);
    }

    [Fact]
    public void A_reconciled_success_keeps_the_reconcile_reason()
    {
        var h = DiagnosisHeadlineBuilder.Build(
            Session(SessionStatus.Succeeded, "sweep:r4", reconcileReason: "Reconciled at timeout: user completed setup (desktop + Windows Hello)"),
            Array.Empty<EnrollmentEvent>());

        Assert.Equal(DiagnosisCauses.Reconciled, h.Cause);
        Assert.Equal("Reconciled at timeout: user completed setup (desktop + Windows Hello)", h.ReasonText);
        Assert.Null(h.Waiting);
    }

    [Fact]
    public void A_soft_failure_shows_the_step_the_user_continued_past()
    {
        var session = Session(SessionStatus.Succeeded, VerdictPaths.AgentCompleteSoft);
        session.EspSoftFailure = true;
        var events = new Timeline()
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("errorCode", "0x80070652")))
            .Add("esp_failure_advisory", Failed("AccountSetup", "Apps", "0x80070652", "Contoso Notes"))
            .Add("enrollment_complete");

        var h = DiagnosisHeadlineBuilder.Build(session, events.Events);

        Assert.Equal(DiagnosisCauses.SoftFailure, h.Cause);
        Assert.Equal("Apps", h.FailedStep!.Subcategory);
        Assert.Equal(DiagnosisCulpritEvidence.InstallFailed, Assert.Single(h.Culprits!).Evidence);
    }

    [Fact]
    public void A_legacy_row_derives_its_path_and_says_so()
    {
        var events = new Timeline()
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("errorCode", "0x87d1041c")))
            .Add("enrollment_failed", Failed("AccountSetup", "Apps", "0x87d1041c"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, null, "enrollment_failed: esp_terminal_failure (0x87d1041c)"), events.Events);

        Assert.Equal(VerdictPaths.AgentFailed, h.VerdictPath);
        Assert.Equal(DiagnosisCauses.EspAppFailure, h.Cause);
        Assert.Equal(new[] { DiagnosisCaveats.VerdictDerived }, h.Caveats);
    }

    [Fact]
    public void An_unattributable_legacy_verdict_is_unknown_and_keeps_its_reason()
    {
        var h = DiagnosisHeadlineBuilder.Build(
            Session(SessionStatus.Failed, null, "Session timed out after 5 hours (started at 2026-01-01 08:00 UTC)"),
            Array.Empty<EnrollmentEvent>());

        Assert.Equal(DiagnosisCauses.Unknown, h.Cause);
        Assert.Equal("Session timed out after 5 hours (started at 2026-01-01 08:00 UTC)", h.ReasonText);
    }

    [Fact]
    public void Observation_limits_become_caveats()
    {
        var config = EspConfig(false, NotesId);
        config["espTrackedWin32Count"] = 60L;
        var events = new Timeline()
            .Add("agent_started", new() { ["previousExitType"] = "exception_crash" })
            .Add("agent_late_start", new() { ["bootToAgentStartSeconds"] = 900.0 })
            .Add("esp_config_detected", config);

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Succeeded, VerdictPaths.AgentComplete), events.Events);

        Assert.Equal(new[] { DiagnosisCaveats.AgentStartedLate, DiagnosisCaveats.AgentCrashed, DiagnosisCaveats.BlockingListTruncated }, h.Caveats);
    }

    [Fact]
    public void A_later_config_without_lists_keeps_the_earlier_blocking_evidence()
    {
        var events = new Timeline()
            .Add("esp_config_detected", EspConfig(false, NotesId))
            .Add("esp_config_detected", new() { ["allowContinueAnyway"] = "true", ["syncFailureTimeoutMinutes"] = "60" })
            .Add("app_install_failed", App(NotesId, "Contoso Notes", ("errorCode", "1603")))
            .Add("enrollment_failed", Failed("AccountSetup", "Apps"));

        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed), events.Events);

        Assert.True(h.EspConfig!.ContinueAnyway);
        Assert.Equal(60, h.EspConfig.TimeoutMinutes);
        Assert.Equal(1, h.EspConfig.BlockingAppCount);
        Assert.True(Assert.Single(h.Culprits!).EspBlocking);
    }

    [Fact]
    public void A_session_without_events_still_has_a_verdict()
    {
        var h = DiagnosisHeadlineBuilder.Build(Session(SessionStatus.Failed, VerdictPaths.AgentFailed, "enrollment_failed: esp_terminal_failure"), Array.Empty<EnrollmentEvent>());

        Assert.Equal(DiagnosisOutcomes.Failed, h.Outcome);
        Assert.Equal(DiagnosisCauses.Unknown, h.Cause);
        Assert.Null(h.Esp);
        Assert.Null(h.EspConfig);
    }
}
