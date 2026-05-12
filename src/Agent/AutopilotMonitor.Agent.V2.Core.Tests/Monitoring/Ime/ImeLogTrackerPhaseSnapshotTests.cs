#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// F5 (debrief 7dd4e593) — V2 clears <c>_packageStates</c> on the DeviceSetup→AccountSetup
    /// ESP transition (intentional, prevents the IgnoreList from growing unboundedly). The
    /// termination summary path therefore must read the deduped union of phase snapshots and
    /// the live list. <see cref="ImeLogTracker.GetAllKnownPackageStates"/> is the consolidated
    /// view consumed by <c>FinalStatusBuilder</c> and <c>app_tracking_summary</c>.
    /// </summary>
    public sealed class ImeLogTrackerPhaseSnapshotTests
    {
        private static AppPackageState NewPkg(string id, AppTargeted targeted, AppInstallationState terminal)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            // UpdateState's inverse-detection guard rewrites Installed→Skipped without a
            // prior Installing flip; lifecycle the package through Installing first so the
            // terminal sticks (mirrors the live IME log flow).
            if (terminal == AppInstallationState.Installed)
                pkg.UpdateState(AppInstallationState.Installing);
            pkg.UpdateState(terminal);
            typeof(AppPackageState).GetProperty(nameof(AppPackageState.Targeted))!.SetValue(pkg, targeted);
            return pkg;
        }

        private static ImeLogTracker BuildTracker(TempDirectory tmp) =>
            new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));

        [Fact]
        public void GetAllKnownPackageStates_empty_when_no_snapshot_and_no_live()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            Assert.Empty(tracker.GetAllKnownPackageStates());
        }

        [Fact]
        public void GetAllKnownPackageStates_returns_live_packages_only()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.PackageStates.Add(NewPkg("user-1", AppTargeted.User, AppInstallationState.Installed));
            tracker.PackageStates.Add(NewPkg("user-2", AppTargeted.User, AppInstallationState.Installed));

            var all = tracker.GetAllKnownPackageStates();

            Assert.Equal(2, all.Count);
            Assert.Contains(all, p => p.Id == "user-1");
            Assert.Contains(all, p => p.Id == "user-2");
        }

        [Fact]
        public void GetAllKnownPackageStates_returns_snapshot_packages_when_live_is_empty()
        {
            // Reproduces the live-session scenario: at the AccountSetup transition the tracker
            // moved 8 DeviceSetup apps into the snapshot dict and cleared _packageStates. If
            // no user-phase apps had been discovered yet the live list is empty, but the
            // SummaryDialog must still show the 8 DeviceSetup apps.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.SeedPhaseSnapshotForTesting("DeviceSetup", new[]
            {
                NewPkg("device-1", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("device-2", AppTargeted.Device, AppInstallationState.Installed),
            });

            var all = tracker.GetAllKnownPackageStates();

            Assert.Equal(2, all.Count);
            Assert.All(all, p => Assert.Equal(AppTargeted.Device, p.Targeted));
        }

        [Fact]
        public void GetAllKnownPackageStates_returns_union_of_snapshot_and_live()
        {
            // Live-session scenario from session 7dd4e593: 8 Device apps in the snapshot
            // (post-clear), 3 User apps in the live list. Termination summary expects 11.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.SeedPhaseSnapshotForTesting("DeviceSetup", new[]
            {
                NewPkg("device-1", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("device-2", AppTargeted.Device, AppInstallationState.Installed),
            });
            tracker.PackageStates.Add(NewPkg("user-1", AppTargeted.User, AppInstallationState.Installed));
            tracker.PackageStates.Add(NewPkg("user-2", AppTargeted.User, AppInstallationState.Installed));
            tracker.PackageStates.Add(NewPkg("user-3", AppTargeted.User, AppInstallationState.Installed));

            var all = tracker.GetAllKnownPackageStates();

            Assert.Equal(5, all.Count);
            Assert.Equal(2, all.Count(p => p.Targeted == AppTargeted.Device));
            Assert.Equal(3, all.Count(p => p.Targeted == AppTargeted.User));
        }

        [Fact]
        public void PhasePackageSnapshots_survive_save_and_load_roundtrip()
        {
            // Codex follow-up (882fef64 PR3-PR5 review): on hybrid-join + multi-reboot
            // enrollments the agent commonly restarts mid-AccountSetup. Before this fix the
            // snapshot dict was lost on restart and DeviceSetup apps disappeared from
            // FinalStatus + app_tracking_summary. Round-trip the persistence path on the
            // same state directory to prove the dict survives.
            using var tmp = new TempDirectory();
            var stateDir = System.IO.Path.Combine(tmp.Path, "State");
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            using (var trackerA = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: logger,
                stateDirectory: stateDir))
            {
                trackerA.SeedPhaseSnapshotForTesting("DeviceSetup", new[]
                {
                    NewPkg("device-1", AppTargeted.Device, AppInstallationState.Installed),
                    NewPkg("device-2", AppTargeted.Device, AppInstallationState.Installed),
                });
                trackerA.PackageStates.Add(NewPkg("user-1", AppTargeted.User, AppInstallationState.Installed));

                trackerA.SaveStateForTest();
            }

            // Fresh tracker on the same state dir — simulates an agent restart picking up the
            // persisted state. LoadStateForTest replaces the synchronous Start() path so file
            // watchers stay out of the test.
            using (var trackerB = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: logger,
                stateDirectory: stateDir))
            {
                trackerB.LoadStateForTest();

                var all = trackerB.GetAllKnownPackageStates();
                Assert.Equal(3, all.Count);
                Assert.Equal(2, all.Count(p => p.Targeted == AppTargeted.Device));
                Assert.Equal(1, all.Count(p => p.Targeted == AppTargeted.User));
                Assert.Contains(all, p => p.Id == "device-1");
                Assert.Contains(all, p => p.Id == "device-2");
                Assert.Contains(all, p => p.Id == "user-1");
            }
        }

        [Fact]
        public void LoadState_tolerates_old_snapshot_files_without_PhasePackageSnapshots_field()
        {
            // Backwards-compat: agent rolled out before this field exists has snapshot files
            // with no PhasePackageSnapshots entry. Loading must NOT throw and must leave the
            // in-memory dict empty (degrades to live-only view).
            using var tmp = new TempDirectory();
            var stateDir = System.IO.Path.Combine(tmp.Path, "State");
            System.IO.Directory.CreateDirectory(stateDir);

            // Hand-craft an old-shape state file: contains only fields that pre-dated the
            // PhasePackageSnapshots addition. JSON-deserialiser must default the new field
            // to null without complaining.
            var oldJson = "{\"CurrentPhaseOrder\":1,\"LastEspPhaseDetected\":\"DeviceSetup\",\"AllAppsCompletedFired\":false,\"LogPhaseIsCurrentPhase\":true,\"SeenAppIds\":[],\"IgnoreList\":[],\"CurrentPackageId\":null,\"Packages\":[],\"FilePositions\":{}}";
            System.IO.File.WriteAllText(System.IO.Path.Combine(stateDir, "ime-tracker-state.json"), oldJson);

            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var tracker = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: logger,
                stateDirectory: stateDir);

            // Must not throw.
            tracker.LoadStateForTest();
            // No snapshots restored, no live packages → empty consolidated view.
            Assert.Empty(tracker.GetAllKnownPackageStates());
        }

        [Fact]
        public void GetAllKnownPackageStates_dedupes_by_id_with_live_winning()
        {
            // Defensive: ESP-phase moves all known IDs into IgnoreList so an app cannot
            // reappear in _packageStates under the same Id, but if a future code path ever
            // re-adds an Id present in a snapshot the live entry must win — its
            // InstallationState reflects the most recent observation.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.SeedPhaseSnapshotForTesting("DeviceSetup", new[]
            {
                NewPkg("shared-id", AppTargeted.Device, AppInstallationState.Error),
            });
            tracker.PackageStates.Add(NewPkg("shared-id", AppTargeted.User, AppInstallationState.Installed));

            var all = tracker.GetAllKnownPackageStates();

            var entry = Assert.Single(all);
            Assert.Equal(AppTargeted.User, entry.Targeted);
            Assert.Equal(AppInstallationState.Installed, entry.InstallationState);
        }

        // ----------------------------------------------------------------
        // c117946b debrief (2026-05-12): PromoteActiveInstallsToStuck
        // ----------------------------------------------------------------

        private static AppPackageState NewPkgInstalling(string id, string name)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            pkg.UpdateState(AppInstallationState.Installing);
            typeof(AppPackageState).GetProperty(nameof(AppPackageState.Name))!.SetValue(pkg, name);
            return pkg;
        }

        [Fact]
        public void PromoteActiveInstallsToStuck_promotes_installing_apps_to_error_and_fires_callback()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            var stuck = NewPkgInstalling("app-1", "StuckApp");
            tracker.PackageStates.Add(stuck);

            var observed = new List<(string id, AppInstallationState oldState, AppInstallationState newState)>();
            tracker.OnAppStateChanged = (pkg, oldS, newS) => observed.Add((pkg.Id, oldS, newS));

            var result = tracker.PromoteActiveInstallsToStuck(
                "esp_apps_timeout",
                "Install status unconfirmed — ESP timed out while still installing.");

            // Tracker state: app flipped to Error with the canonical ErrorPatternId + ErrorDetail.
            Assert.Equal(AppInstallationState.Error, stuck.InstallationState);
            Assert.Equal("esp_apps_timeout", stuck.ErrorPatternId);
            Assert.Contains("ESP timed out", stuck.ErrorDetail);

            // The standard state-change callback fired so the adapter can emit a regular
            // app_install_failed event (carrying the new failureType/confidence tags).
            Assert.Single(observed);
            Assert.Equal("app-1", observed[0].id);
            Assert.Equal(AppInstallationState.Installing, observed[0].oldState);
            Assert.Equal(AppInstallationState.Error, observed[0].newState);

            // Return value enumerates the promoted appIds for the caller's log line.
            Assert.Equal(new[] { "app-1" }, result);
        }

        [Fact]
        public void PromoteActiveInstallsToStuck_skips_apps_not_in_installing_state()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            // Per design (user vote, 2026-05-12): only `Installing` is promoted. Downloading,
            // Postponed and pending stay untouched because the agent can't claim "likely
            // stuck" with confidence about them.
            var dl = new AppPackageState("a", 0);
            dl.UpdateState(AppInstallationState.Downloading);
            var done = new AppPackageState("b", 1);
            done.UpdateState(AppInstallationState.Installing);
            done.UpdateState(AppInstallationState.Installed);
            var postponed = new AppPackageState("c", 2);
            postponed.UpdateState(AppInstallationState.Postponed);

            tracker.PackageStates.Add(dl);
            tracker.PackageStates.Add(done);
            tracker.PackageStates.Add(postponed);

            var observed = new List<string>();
            tracker.OnAppStateChanged = (pkg, _, _) => observed.Add(pkg.Id);

            var result = tracker.PromoteActiveInstallsToStuck("esp_apps_timeout", "msg");

            Assert.Empty(result);
            Assert.Empty(observed);
            Assert.Equal(AppInstallationState.Downloading, dl.InstallationState);
            Assert.Equal(AppInstallationState.Installed, done.InstallationState);
            Assert.Equal(AppInstallationState.Postponed, postponed.InstallationState);
        }

        [Fact]
        public void PromoteActiveInstallsToStuck_empty_list_when_no_installing_apps()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            var result = tracker.PromoteActiveInstallsToStuck("esp_apps_timeout", "msg");

            Assert.Empty(result);
        }
    }
}
