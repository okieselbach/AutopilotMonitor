#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// IME's sequence per app, driven through the shipped pattern pack: the check before the
    /// install, the installer, "Execution state … Success", the check right after the install,
    /// and the final state (D-392).
    /// <list type="bullet">
    ///   <item>When the check after the install does not find the app, IME flips Success → Error
    ///         with EnforcementErrorCode -2016345060 (0x87D1041C) BEFORE it logs the check's
    ///         result line; that code is IME's verdict and must reach the error transition.</item>
    ///   <item>The check after the install is marked ("expected to have enforcement state:
    ///         Detected"); its result is reported through OnPostInstallDetection, and
    ///         detectionResult stays the check before the install.</item>
    ///   <item>After an exit code that asked for a restart, that check decides nothing; the check
    ///         after the restart (InProgressPendingReboot → Success) is the one reported.</item>
    /// </list>
    /// </summary>
    public sealed class ImeLogTrackerDetectionAfterInstallTests
    {
        private const string AppId = "5c95bf94-1cf4-4629-88d1-3f616e7a405c";
        private const string DependencyId = "9d2b8a71-4e0c-4f5a-a3c6-71b0e2d5c8f4";

        private static string PhaseLine => "[Win32App] In EspPhase: DeviceSetup.";
        private static string CurrentAppLine => @"[Win32App] SetCurrentDirectory: C:\Windows\IMECache\" + AppId + "_1";

        private static string DetectionResult(string state, string appId = AppId)
            => "[Win32App][DetectionActionHandler] Detection for policy with id: " + appId +
               " resulted in action status: Success and detection state: " + state + ".";

        private static string[] CheckAfterInstallMarkers() => new[]
        {
            "[Win32App][DetectionActionHandler] Detection running for policy with id: " + AppId + ".",
            "[Win32App][DetectionActionHandler] The last enforcement action was on policy with id: " + AppId + ".",
            "[Win32App][DetectionActionHandler] Policy with id: " + AppId + " is expected to have enforcement state: Detected.",
        };

        private static string ExecutionDelta(string from, string to, string? errorCode = null)
            => "[Win32App][ReportingManager] Execution state for app with id: " + AppId + " has been updated. Report delta: " +
               "{\"EnforcementState\":{\"OldValue\":\"" + from + "\",\"NewValue\":\"" + to + "\"}" +
               (errorCode == null ? "" : ",\"EnforcementErrorCode\":{\"OldValue\":null,\"NewValue\":" + errorCode + "}") + "}";

        private sealed class Run
        {
            public ShippedImePackHarness Harness { get; } = new ShippedImePackHarness();
            public AppPackageState Package { get; } = new AppPackageState(AppId, 0);
            public List<(AppInstallationState To, string? Code)> Transitions { get; } = new List<(AppInstallationState, string?)>();
            public List<(string Detection, bool AfterReboot)> ChecksAfterInstall { get; } = new List<(string, bool)>();

            public Run()
            {
                Package.UpdateState(AppInstallationState.Installing);
                Harness.Tracker.PackageStates.Add(Package);
                Harness.Tracker.OnAppStateChanged = (p, _, to) => { if (p.Id == AppId) Transitions.Add((to, p.ErrorCode)); };
                Harness.Tracker.OnPostInstallDetection = (p, detection, afterReboot, _) => ChecksAfterInstall.Add((detection, afterReboot));
            }

            public void Lines(params string[] lines) => Harness.Append(lines);
        }

        private static string[] CleanInstallThen(params string[] tail)
        {
            var lines = new List<string>
            {
                PhaseLine,
                DetectionResult("NotDetected"),               // the check before the install
                ExecutionDelta("InProgress", "Success"),      // IME holds this back until its check
            };
            lines.AddRange(CheckAfterInstallMarkers());
            lines.AddRange(tail);
            return lines.ToArray();
        }

        [Fact]
        public async Task Check_after_install_failing_carries_IMEs_code_on_the_error_transition()
        {
            var run = new Run();
            using (run.Harness)
            {
                run.Lines(CleanInstallThen(
                    ExecutionDelta("Success", "Error", "-2016345060"),
                    DetectionResult("NotDetected")));        // the check's own result line comes last

                await run.Harness.Pass();

                Assert.Equal(new[] { AppInstallationState.Installed, AppInstallationState.Error }, run.Transitions.Select(t => t.To));
                Assert.Equal("-2016345060", run.Transitions.Last().Code);
                Assert.Equal(AppInstallationState.Error, run.Package.InstallationState);
                Assert.Equal("IME-ERROR-REPORT", run.Package.ErrorPatternId);
                Assert.Equal(new[] { ("NotDetected", false) }, run.ChecksAfterInstall);
                Assert.Equal("NotDetected", run.Package.DetectionResult);
            }
        }

        [Fact]
        public async Task Check_after_a_clean_install_is_reported_and_detectionResult_stays_the_check_before()
        {
            var run = new Run();
            using (run.Harness)
            {
                run.Lines(CleanInstallThen(DetectionResult("Detected")));

                await run.Harness.Pass();

                Assert.Equal(new[] { ("Detected", false) }, run.ChecksAfterInstall);
                Assert.Equal("NotDetected", run.Package.DetectionResult);
                Assert.Equal(AppInstallationState.Installed, run.Package.InstallationState);
            }
        }

        [Fact]
        public async Task A_different_version_found_after_the_install_is_reported_as_such()
        {
            var run = new Run();
            using (run.Harness)
            {
                run.Lines(CleanInstallThen(DetectionResult("DetectedDifferentVersion")));

                await run.Harness.Pass();

                Assert.Equal(new[] { ("DetectedDifferentVersion", false) }, run.ChecksAfterInstall);
            }
        }

        [Fact]
        public async Task A_dependency_checked_in_the_same_run_is_no_check_after_its_install()
        {
            // IME checks every app of the dependency chain in the run after an install; only the
            // installed app carries the markers.
            var run = new Run();
            using (run.Harness)
            {
                var dependency = new AppPackageState(DependencyId, 1);
                run.Harness.Tracker.PackageStates.Add(dependency);
                run.Lines(CleanInstallThen(
                    DetectionResult("Detected", DependencyId),
                    DetectionResult("Detected")));

                await run.Harness.Pass();

                Assert.Equal(new[] { ("Detected", false) }, run.ChecksAfterInstall);
                Assert.Equal("Detected", dependency.DetectionResult);
            }
        }

        [Fact]
        public async Task The_check_before_a_requested_restart_is_not_reported_the_one_after_it_is()
        {
            var run = new Run();
            using (run.Harness)
            {
                var beforeRestart = new List<string>
                {
                    PhaseLine,
                    CurrentAppLine,
                    "[Win32App] lpExitCode 3010",
                    "[Win32App] lpExitCode is defined as SoftReboot",
                    ExecutionDelta("InProgress", "InProgressPendingReboot"),
                };
                beforeRestart.AddRange(CheckAfterInstallMarkers());
                beforeRestart.Add(DetectionResult("NotDetected"));   // provisional: IME sets no error
                run.Lines(beforeRestart.ToArray());

                await run.Harness.Pass();
                Assert.Empty(run.ChecksAfterInstall);

                run.Lines(
                    ExecutionDelta("InProgressPendingReboot", "Success"),   // after the restart
                    DetectionResult("Detected"));

                await run.Harness.Pass();
                Assert.Equal(new[] { ("Detected", true) }, run.ChecksAfterInstall);
            }
        }

        [Fact]
        public async Task Lines_from_a_previous_enrollment_mark_nothing()
        {
            var run = new Run();
            using (run.Harness)
            {
                run.Harness.AppendAt(run.Harness.Now.AddDays(-2), CleanInstallThen(DetectionResult("Detected")));

                await run.Harness.Pass();

                Assert.Empty(run.ChecksAfterInstall);
                Assert.Null(run.Package.DetectionResult);
            }
        }

        [Fact]
        public async Task Other_IME_error_reports_carry_their_code_too()
        {
            // A download that fails is the app's first move to Error through the same line.
            var run = new Run();
            using (run.Harness)
            {
                run.Lines(PhaseLine, ExecutionDelta("InProgressDownloadCompleted", "Error", "-2145091577"));

                await run.Harness.Pass();

                Assert.Equal(AppInstallationState.Error, Assert.Single(run.Transitions).To);
                Assert.Equal("-2145091577", run.Package.ErrorCode);
            }
        }

        [Fact]
        public async Task Error_report_without_a_code_leaves_the_code_empty()
        {
            var run = new Run();
            using (run.Harness)
            {
                run.Lines(PhaseLine, ExecutionDelta("InProgress", "Error"));

                await run.Harness.Pass();

                Assert.Equal(AppInstallationState.Error, run.Package.InstallationState);
                Assert.True(string.IsNullOrEmpty(run.Package.ErrorCode));
            }
        }
    }
}
