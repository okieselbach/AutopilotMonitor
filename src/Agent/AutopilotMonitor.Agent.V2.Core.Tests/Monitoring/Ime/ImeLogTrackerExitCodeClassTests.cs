#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Return-code class of the installer exit code (IME "[Win32App] lpExitCode is defined as
    /// {AppReturnCodeType}"): captured by the shipped IME-EXITCODE-CLASS pattern into
    /// <see cref="AppPackageState.ExitCodeClass"/>, last value wins across a Retry loop, the
    /// no-mapping/default-branch variants are ignored, and the value survives the agent
    /// restart that a SoftReboot/HardReboot app causes (persistence round trip).
    /// </summary>
    public sealed class ImeLogTrackerExitCodeClassTests
    {
        private const string AppId = "5c95bf94-1cf4-4629-88d1-3f616e7a405c";

        // ---------------------------------------------------------------- AppPackageState

        [Fact]
        public void UpdateExitCodeClass_last_value_wins_and_ignores_empty()
        {
            var pkg = new AppPackageState(AppId, 0);
            Assert.Null(pkg.ExitCodeClass);

            Assert.True(pkg.UpdateExitCodeClass("Retry"));
            Assert.True(pkg.UpdateExitCodeClass(" Success "));
            Assert.Equal("Success", pkg.ExitCodeClass);
            Assert.False(pkg.UpdateExitCodeClass("Success"));
            Assert.False(pkg.UpdateExitCodeClass(""));
            Assert.False(pkg.UpdateExitCodeClass(null));
            Assert.Equal("Success", pkg.ExitCodeClass);
        }

        [Fact]
        public void ToEventData_carries_the_class_only_when_known()
        {
            var pkg = new AppPackageState(AppId, 0);
            Assert.False(pkg.ToEventData().ContainsKey("exitCodeClass"));

            pkg.UpdateExitCode("3010");
            pkg.UpdateExitCodeClass("SoftReboot");
            var data = pkg.ToEventData();
            Assert.Equal("3010", data["exitCode"]);
            Assert.Equal("SoftReboot", data["exitCodeClass"]);
        }

        [Fact]
        public void Restore_round_trips_the_class()
        {
            var pkg = AppPackageState.Restore(
                AppId, 0, "Contoso VPN",
                AppRunAs.System, AppIntent.Install, AppTargeted.Device,
                new HashSet<string>(),
                AppInstallationState.Installed, true, 100, 0, 0,
                exitCode: "3010", exitCodeClass: "SoftReboot");

            Assert.Equal("SoftReboot", pkg.ExitCodeClass);
        }

        // ---------------------------------------------------------------- shipped pack, end to end

        private static string PhaseLine => "[Win32App] In EspPhase: DeviceSetup.";
        private static string CurrentAppLine => @"[Win32App] SetCurrentDirectory: C:\Windows\IMECache\" + AppId + "_1";

        [Fact]
        public async Task Shipped_pattern_captures_the_class_for_the_current_app()
        {
            using var h = new ShippedImePackHarness();
            h.Tracker.PackageStates.Add(new AppPackageState(AppId, 0));
            h.Append(
                PhaseLine,          // activates the currentPhase pack (IME-EXITCODE, IME-EXITCODE-CLASS)
                CurrentAppLine,     // IME-SET-CURRENT-4 → current app
                "[Win32App] lpExitCode 3010",
                "[Win32App] lpExitCode is defined as SoftReboot");

            await h.Pass();

            var pkg = h.Tracker.PackageStates.GetPackage(AppId);
            Assert.NotNull(pkg);
            Assert.Equal("3010", pkg!.ExitCode);
            Assert.Equal("SoftReboot", pkg.ExitCodeClass);
            Assert.NotEqual(AppInstallationState.Error, pkg.InstallationState);
        }

        [Fact]
        public async Task Retry_loop_that_ends_in_success_reports_the_last_class()
        {
            using var h = new ShippedImePackHarness();
            h.Tracker.PackageStates.Add(new AppPackageState(AppId, 0));
            h.Append(
                PhaseLine, CurrentAppLine,
                "[Win32App] lpExitCode 1618",
                "[Win32App] lpExitCode is defined as Retry",
                "[Win32App] lpExitCode 1618",
                "[Win32App] lpExitCode is defined as Retry",
                "[Win32App] lpExitCode 0",
                "[Win32App] lpExitCode is defined as Success");

            await h.Pass();

            var pkg = h.Tracker.PackageStates.GetPackage(AppId)!;
            Assert.Equal("0", pkg.ExitCode);
            Assert.Equal("Success", pkg.ExitCodeClass);
        }

        [Fact]
        public async Task Failed_class_still_drives_the_error_transition_and_carries_the_class()
        {
            // IME-EXITCODE-CLASS and IME-EXITCODE-MAPPED-FAILED both match the Failed line: the
            // class is captured and the existing error path stays intact.
            using var h = new ShippedImePackHarness();
            var pkg = new AppPackageState(AppId, 0);
            pkg.UpdateState(AppInstallationState.Installing);
            h.Tracker.PackageStates.Add(pkg);
            h.Append(
                PhaseLine, CurrentAppLine,
                "[Win32App] lpExitCode 1603",
                "[Win32App] lpExitCode is defined as Failed");

            await h.Pass();

            Assert.Equal(AppInstallationState.Error, pkg.InstallationState);
            Assert.Equal("Failed", pkg.ExitCodeClass);
            Assert.Equal("IME-EXITCODE-MAPPED-FAILED", pkg.ErrorPatternId);
        }

        [Fact]
        public async Task Default_branch_and_raw_numeric_types_leave_the_class_unset()
        {
            using var h = new ShippedImePackHarness();
            h.Tracker.PackageStates.Add(new AppPackageState(AppId, 0));
            h.Append(
                PhaseLine, CurrentAppLine,
                "[Win32App] lpExitCode 5",
                "[Win32App] lpExitCode is defined as 5",
                "[Win32App] lpExitCode is defined as 5, fall into default now.",
                "[Win32App] Setting enforcementState as: Error with lpExitCode: 5 without mapping");

            await h.Pass();

            var pkg = h.Tracker.PackageStates.GetPackage(AppId)!;
            Assert.Equal("5", pkg.ExitCode);
            Assert.Null(pkg.ExitCodeClass);
        }

        [Fact]
        public async Task Class_survives_the_state_round_trip_of_an_agent_restart()
        {
            // A SoftReboot/HardReboot app completes only after the reboot; the restarted agent
            // must still know the class when the post-reboot Success line arrives.
            using var h = new ShippedImePackHarness();
            h.Tracker.PackageStates.Add(new AppPackageState(AppId, 0));
            h.Append(
                PhaseLine, CurrentAppLine,
                "[Win32App] lpExitCode 1641",
                "[Win32App] lpExitCode is defined as HardReboot");
            await h.Pass();
            h.Tracker.SaveStateForTest();

            using var restarted = h.Build();
            restarted.LoadStateForTest();

            var pkg = restarted.GetAllKnownPackageStates().Single(p => p.Id == AppId);
            Assert.Equal("1641", pkg.ExitCode);
            Assert.Equal("HardReboot", pkg.ExitCodeClass);
        }
    }
}
