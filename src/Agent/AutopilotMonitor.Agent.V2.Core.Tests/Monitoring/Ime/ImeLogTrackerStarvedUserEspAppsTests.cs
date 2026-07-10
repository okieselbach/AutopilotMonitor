using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Liveness plan PR3 — <see cref="ImeLogTracker.GetStarvedUserEspApps"/> semantics. A
    /// starved app is Install-intent, tracked in the current AccountSetup phase, has never
    /// shown download/install activity, and is neither terminal nor failed. Apps that are
    /// alive (Downloading/Installing or any prior progress) and error-state apps (owned by
    /// the failure path) must never be reported. Session a4537c36: Uninstall-intent apps are
    /// excluded — the ESP apps gate does not block on uninstalls.
    /// </summary>
    public sealed class ImeLogTrackerStarvedUserEspAppsTests
    {
        private static ImeLogTracker BuildTracker(TempDirectory tmp) =>
            new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));

        private static AppPackageState AddApp(
            ImeLogTracker tracker,
            string id,
            AppIntent intent,
            AppInstallationState state)
        {
            var pkg = tracker.PackageStates.GetPackage(id, createIfNotFound: true);
            pkg.UpdateIntent(intent);
            if (state == AppInstallationState.Installed)
            {
                pkg.UpdateState(AppInstallationState.Installing);
            }
            if (state != AppInstallationState.Unknown)
            {
                pkg.UpdateState(state);
            }
            return pkg;
        }

        [Fact]
        public void Pending_required_app_is_starved()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-pending", AppIntent.Install, AppInstallationState.Unknown);

            var starved = tracker.GetStarvedUserEspApps();

            var app = Assert.Single(starved);
            Assert.Equal("app-pending", app.Id);
        }

        [Fact]
        public void InProgress_without_download_activity_is_starved()
        {
            // IME announced the app (policy line → InProgress) but enforcement never started —
            // exactly the field shape that starved the gate (user-targeted app on "pending").
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-announced", AppIntent.Install, AppInstallationState.InProgress);

            var starved = tracker.GetStarvedUserEspApps();

            var app = Assert.Single(starved);
            Assert.Equal("app-announced", app.Id);
        }

        [Fact]
        public void Downloading_and_Installing_apps_are_alive_not_starved()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-downloading", AppIntent.Install, AppInstallationState.Downloading);
            AddApp(tracker, "app-installing", AppIntent.Install, AppInstallationState.Installing);

            Assert.Empty(tracker.GetStarvedUserEspApps());
        }

        [Fact]
        public void Terminal_and_error_apps_are_not_starved()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-installed", AppIntent.Install, AppInstallationState.Installed);
            AddApp(tracker, "app-skipped", AppIntent.Install, AppInstallationState.Skipped);
            // Error apps belong to the failure path (app_install_failed names them already).
            AddApp(tracker, "app-failed", AppIntent.Install, AppInstallationState.Error);

            Assert.Empty(tracker.GetStarvedUserEspApps());
        }

        [Fact]
        public void Non_required_apps_are_not_starved()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-available", AppIntent.Available, AppInstallationState.Unknown);
            AddApp(tracker, "app-unknown-intent", AppIntent.Unknown, AppInstallationState.Unknown);

            Assert.Empty(tracker.GetStarvedUserEspApps());
        }

        [Fact]
        public void Outside_AccountSetup_phase_returns_empty()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("DeviceSetup");
            AddApp(tracker, "app-pending", AppIntent.Install, AppInstallationState.Unknown);

            Assert.Empty(tracker.GetStarvedUserEspApps());
        }

        [Fact]
        public void Phase_never_detected_returns_empty()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            AddApp(tracker, "app-pending", AppIntent.Install, AppInstallationState.Unknown);

            Assert.Empty(tracker.GetStarvedUserEspApps());
        }

        [Fact]
        public void Uninstall_intent_apps_are_not_starved()
        {
            // Session a4537c36: system-app removals (Xbox, WMP, …) sat pending through the whole
            // AccountSetup phase and were flagged as "never started installing" — the ESP apps
            // gate does not block on uninstalls, so they must not be reported.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-uninstall-pending", AppIntent.Uninstall, AppInstallationState.Unknown);

            Assert.Empty(tracker.GetStarvedUserEspApps());
        }

        [Fact]
        public void Mixed_intents_report_only_the_pending_install()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-uninstall-pending", AppIntent.Uninstall, AppInstallationState.Unknown);
            AddApp(tracker, "app-install-pending", AppIntent.Install, AppInstallationState.Unknown);

            var starved = tracker.GetStarvedUserEspApps();

            var app = Assert.Single(starved);
            Assert.Equal("app-install-pending", app.Id);
        }

        [Fact]
        public void Mixed_list_reports_only_the_starved_app()
        {
            // The field shape: one app installed, one skipped, one never started.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-installed", AppIntent.Install, AppInstallationState.Installed);
            AddApp(tracker, "app-skipped", AppIntent.Install, AppInstallationState.Skipped);
            AddApp(tracker, "app-starving", AppIntent.Install, AppInstallationState.Unknown);

            var starved = tracker.GetStarvedUserEspApps();

            var app = Assert.Single(starved);
            Assert.Equal("app-starving", app.Id);
        }
    }
}
