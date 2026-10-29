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
    /// and the final state. When that last check does not find the app, IME flips Success →
    /// Error with EnforcementErrorCode -2016345060 (0x87D1041C) BEFORE it logs the check's
    /// result line; that code is IME's verdict and must reach the error transition (D-392).
    /// </summary>
    public sealed class ImeLogTrackerDetectionAfterInstallTests
    {
        private const string AppId = "5c95bf94-1cf4-4629-88d1-3f616e7a405c";

        private static string PhaseLine => "[Win32App] In EspPhase: DeviceSetup.";

        private static string DetectionResult(string state)
            => "[Win32App][DetectionActionHandler] Detection for policy with id: " + AppId +
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

        private static (ShippedImePackHarness Harness, AppPackageState Package, List<(AppInstallationState To, string? Code)> Transitions) Installing()
        {
            var h = new ShippedImePackHarness();
            var pkg = new AppPackageState(AppId, 0);
            pkg.UpdateState(AppInstallationState.Installing);
            h.Tracker.PackageStates.Add(pkg);
            var transitions = new List<(AppInstallationState To, string? Code)>();
            h.Tracker.OnAppStateChanged = (p, _, to) => transitions.Add((to, p.ErrorCode));
            return (h, pkg, transitions);
        }

        [Fact]
        public async Task Check_after_install_failing_carries_IMEs_code_on_the_error_transition()
        {
            var (h, pkg, transitions) = Installing();
            using (h)
            {
                var lines = new List<string>
                {
                    PhaseLine,
                    DetectionResult("NotDetected"),               // the check before the install
                    ExecutionDelta("InProgress", "Success"),      // IME holds this back until its check
                };
                lines.AddRange(CheckAfterInstallMarkers());
                lines.Add(ExecutionDelta("Success", "Error", "-2016345060"));
                lines.Add(DetectionResult("NotDetected"));        // the check's own result line comes last
                h.Append(lines.ToArray());

                await h.Pass();

                Assert.Equal(new[] { AppInstallationState.Installed, AppInstallationState.Error }, transitions.Select(t => t.To));
                Assert.Equal("-2016345060", transitions.Last().Code);
                Assert.Equal(AppInstallationState.Error, pkg.InstallationState);
                Assert.Equal("IME-ERROR-REPORT", pkg.ErrorPatternId);
            }
        }

        [Fact]
        public async Task Other_IME_error_reports_carry_their_code_too()
        {
            // A download that fails is the app's first move to Error through the same line.
            var (h, pkg, transitions) = Installing();
            using (h)
            {
                h.Append(PhaseLine, ExecutionDelta("InProgressDownloadCompleted", "Error", "-2145091577"));

                await h.Pass();

                Assert.Equal(AppInstallationState.Error, Assert.Single(transitions).To);
                Assert.Equal("-2145091577", pkg.ErrorCode);
            }
        }

        [Fact]
        public async Task Error_report_without_a_code_leaves_the_code_empty()
        {
            var (h, pkg, _) = Installing();
            using (h)
            {
                h.Append(PhaseLine, ExecutionDelta("InProgress", "Error"));

                await h.Pass();

                Assert.Equal(AppInstallationState.Error, pkg.InstallationState);
                Assert.True(string.IsNullOrEmpty(pkg.ErrorCode));
            }
        }
    }
}
