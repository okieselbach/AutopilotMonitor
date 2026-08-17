using System;
using AutopilotMonitor.Agent.V2;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Pins the two-tier runtime-launch fallback orchestration. A customer device
    /// failed bootstrap 2026-05-11 because WMI Win32_Process.Create returned 2
    /// (Access Denied — Defender ASR rule d1e49aac-...) and the install threw, which
    /// the bootstrap script translated into exit 1, which the next IME run SKIPped
    /// via its pre-flight, leaving the device stuck without a runtime until manual
    /// intervention. These tests fail loudly if a future refactor silently
    /// reintroduces throw-on-WMI-failure or skips the schtasks fallback.
    /// </summary>
    public sealed class InstallModeRuntimeLaunchTests
    {
        [Fact]
        public void Wmi_success_returns_wmi_outcome_and_skips_schtasks_fallback()
        {
            var schtasksInvocations = 0;

            var result = AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                wmiReturnValue: 0u,
                wmiPid: 1234,
                trySchtasks: () => { schtasksInvocations++; return 0; });

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Wmi, result.Method);
            Assert.Equal(1234, result.Pid);
            Assert.Equal(0u, result.WmiReturnValue);
            Assert.Equal(0, result.SchtasksExitCode);
            Assert.Equal(0, schtasksInvocations);
            Assert.Contains("PID=1234", result.Diagnostic);
        }

        [Fact]
        public void Wmi_access_denied_falls_back_to_schtasks_success()
        {
            var result = AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                wmiReturnValue: 2u,
                wmiPid: 0,
                trySchtasks: () => 0);

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Schtasks, result.Method);
            Assert.Equal(0, result.Pid);
            Assert.Equal(2u, result.WmiReturnValue);
            Assert.Equal(0, result.SchtasksExitCode);
            Assert.Contains("schtasks", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Wmi_access_denied_diagnostic_names_the_defender_asr_rule()
        {
            // Customer-facing log line should make the AV/EDR root cause obvious to support
            // without forcing them to look up WMI return codes or ASR rule GUIDs.
            var result = AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                wmiReturnValue: 2u,
                wmiPid: 0,
                trySchtasks: () => 1);

            Assert.Contains("d1e49aac-8f56-4280-b9ba-993a6d77406c", result.Diagnostic);
            Assert.Contains("Access Denied", result.Diagnostic);
        }

        [Fact]
        public void Non_access_denied_wmi_failure_omits_the_asr_specific_hint()
        {
            // returnValue=8 ('Unknown failure') is not ASR-shaped — don't mislead support.
            var result = AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                wmiReturnValue: 8u,
                wmiPid: 0,
                trySchtasks: () => 1);

            Assert.DoesNotContain("d1e49aac", result.Diagnostic);
            Assert.Contains("returnValue=8", result.Diagnostic);
        }

        [Fact]
        public void Both_paths_fail_returns_deferred_with_both_codes_captured()
        {
            var result = AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                wmiReturnValue: 2u,
                wmiPid: 0,
                trySchtasks: () => 1);

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Deferred, result.Method);
            Assert.Equal(0, result.Pid);
            Assert.Equal(2u, result.WmiReturnValue);
            Assert.Equal(1, result.SchtasksExitCode);
            Assert.Contains("BootTrigger", result.Diagnostic);
        }

        [Fact]
        public void Wmi_returnvalue_zero_but_pid_zero_does_not_count_as_success()
        {
            // Defensive: if WBEM lies about success (returnValue=0 but no real ProcessId),
            // treat as failure and exercise the fallback rather than logging a bogus PID.
            var schtasksInvocations = 0;

            var result = AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                wmiReturnValue: 0u,
                wmiPid: 0,
                trySchtasks: () => { schtasksInvocations++; return 0; });

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Schtasks, result.Method);
            Assert.Equal(1, schtasksInvocations);
        }

        [Fact]
        public void Try_schtasks_delegate_is_evaluated_lazily()
        {
            // Pins that the WMI-success path costs zero — important because schtasks
            // /Run on the BootTrigger task in OOBE has its own queue-defer cost.
            var schtasksInvocations = 0;

            AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                wmiReturnValue: 0u,
                wmiPid: 9999,
                trySchtasks: () => { schtasksInvocations++; return 1; });

            Assert.Equal(0, schtasksInvocations);
        }

        [Fact]
        public void Null_schtasks_delegate_is_rejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => AutopilotMonitor.Agent.V2.Program.DecideRuntimeLaunchOutcome(
                    wmiReturnValue: 2u,
                    wmiPid: 0,
                    trySchtasks: null!));
        }
    }

    /// <summary>
    /// Pins the restricted-token launch ordering introduced 2026-08-17: MDM LOB MSI installs
    /// (EnterpriseDesktopAppManagement CSP → msiexec chain) carry a SYSTEM token with
    /// SeTimeZonePrivilege / SeSystemEnvironmentPrivilege REMOVED, and WMI Win32_Process.Create
    /// duplicates the caller token onto the runtime — so on a stripped token the Scheduled Task
    /// (fresh full SYSTEM token) must launch first and WMI is only the degraded fallback. These
    /// tests fail loudly if a refactor silently restores WMI-first for the stripped-token case
    /// or drops the process verification after schtasks /Run.
    /// </summary>
    public sealed class InstallModeRestrictedTokenLaunchTests
    {
        [Fact]
        public void Schtasks_success_with_verified_pid_returns_schtasks_and_never_touches_wmi()
        {
            var wmiInvocations = 0;

            var result = AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(
                trySchtasks: () => 0,
                verifyRuntimePid: () => 4321,
                tryWmi: () => { wmiInvocations++; return new AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt(0u, 9999); });

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Schtasks, result.Method);
            Assert.Equal(4321, result.Pid);
            Assert.Equal(0, result.SchtasksExitCode);
            Assert.Equal(0, wmiInvocations);
            Assert.Contains("full SYSTEM token", result.Diagnostic);
            Assert.Contains("PID=4321", result.Diagnostic);
        }

        [Fact]
        public void Schtasks_queued_but_no_process_appears_falls_back_to_wmi()
        {
            var result = AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(
                trySchtasks: () => 0,
                verifyRuntimePid: () => 0,
                tryWmi: () => new AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt(0u, 777));

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Wmi, result.Method);
            Assert.Equal(777, result.Pid);
            Assert.Contains("no runtime process appeared", result.Diagnostic);
        }

        [Fact]
        public void Schtasks_failure_skips_verification_and_falls_back_to_wmi()
        {
            var verifyInvocations = 0;

            var result = AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(
                trySchtasks: () => 1,
                verifyRuntimePid: () => { verifyInvocations++; return 0; },
                tryWmi: () => new AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt(0u, 555));

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Wmi, result.Method);
            Assert.Equal(0, verifyInvocations);
            Assert.Equal(1, result.SchtasksExitCode);
        }

        [Fact]
        public void Wmi_fallback_diagnostic_warns_that_the_runtime_stays_degraded()
        {
            // Support line: a restricted-token WMI launch means timezone auto-set / firmware
            // reads will fail for this runtime instance — the log must say so explicitly.
            var result = AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(
                trySchtasks: () => 1,
                verifyRuntimePid: () => 0,
                tryWmi: () => new AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt(0u, 555));

            Assert.Contains("RESTRICTED TOKEN", result.Diagnostic);
            Assert.Contains("timezone", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Both_paths_fail_returns_deferred_with_both_codes_captured()
        {
            var result = AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(
                trySchtasks: () => 1,
                verifyRuntimePid: () => 0,
                tryWmi: () => new AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt(2u, 0));

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Deferred, result.Method);
            Assert.Equal(0, result.Pid);
            Assert.Equal(2u, result.WmiReturnValue);
            Assert.Equal(1, result.SchtasksExitCode);
            Assert.Contains("BootTrigger", result.Diagnostic);
        }

        [Fact]
        public void Wmi_returnvalue_zero_but_pid_zero_does_not_count_as_success()
        {
            var result = AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(
                trySchtasks: () => 1,
                verifyRuntimePid: () => 0,
                tryWmi: () => new AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt(0u, 0));

            Assert.Equal(AutopilotMonitor.Agent.V2.Program.RuntimeLaunchMethod.Deferred, result.Method);
        }

        [Fact]
        public void Null_delegates_are_rejected()
        {
            Func<int> zero = () => 0;
            Func<AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt> wmi =
                () => new AutopilotMonitor.Agent.V2.Program.WmiLaunchAttempt(0u, 1);

            Assert.Throws<ArgumentNullException>(
                () => AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(null!, zero, wmi));
            Assert.Throws<ArgumentNullException>(
                () => AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(zero, null!, wmi));
            Assert.Throws<ArgumentNullException>(
                () => AutopilotMonitor.Agent.V2.Program.DecideRestrictedTokenLaunchOutcome(zero, zero, null!));
        }
    }
}
