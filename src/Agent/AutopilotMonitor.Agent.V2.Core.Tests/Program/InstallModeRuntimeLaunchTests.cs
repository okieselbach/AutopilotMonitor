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
}
