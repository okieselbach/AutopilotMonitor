#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Locks down the flat V1 <c>app_tracking_summary</c> schema produced by
    /// <see cref="AppTrackingSummaryBuilder"/>. The Web hooks
    /// (<c>useSessionDerivedData</c> / <c>useProgressDerivedData</c>) and analyze rules
    /// (e.g. ANALYZE-APP-007's <c>errorCount</c>) read these keys directly. The same builder
    /// drives the per-transition snapshot in <c>ImeLogTrackerAdapter</c> and the terminal
    /// emit in <c>EnrollmentTerminationHandler</c> — both consume the same shape.
    /// </summary>
    public sealed class AppTrackingSummaryBuilderTests
    {
        private static AppPackageState NewPkg(string id, AppTargeted targeted, AppInstallationState state, string? name = null)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            // UpdateState's inverse-detection guard rewrites Installed→Skipped without a
            // prior Installing flip; lifecycle the package through Installing first.
            if (state == AppInstallationState.Installed)
                pkg.UpdateState(AppInstallationState.Installing);
            pkg.UpdateState(state);
            typeof(AppPackageState).GetProperty(nameof(AppPackageState.Targeted))!.SetValue(pkg, targeted);
            if (name != null)
                typeof(AppPackageState).GetProperty(nameof(AppPackageState.Name))!.SetValue(pkg, name);
            return pkg;
        }

        [Fact]
        public void Build_EmptyInputs_YieldsZeroAggregatesAndEmptyNameLists()
        {
            var data = AppTrackingSummaryBuilder.Build(packages: null);

            Assert.Equal(0, (int)data["totalApps"]);
            Assert.Equal(0, (int)data["completedApps"]);
            Assert.Equal(0, (int)data["errorCount"]);
            Assert.Equal(0, (int)data["deviceErrors"]);
            Assert.Equal(0, (int)data["userErrors"]);
            Assert.False((bool)data["hasErrors"]);
            Assert.False((bool)data["isAllCompleted"]);
            Assert.Equal(0, (int)data["ignoredCount"]);
            Assert.Equal(0, (int)data["installed"]);
            Assert.Equal(0, (int)data["downloading"]);
            Assert.Equal(0, (int)data["pending"]);

            Assert.Empty((List<string>)data["installedNames"]);
            Assert.Empty((List<string>)data["failedNames"]);
            Assert.Empty((List<string>)data["pendingNames"]);
        }

        [Fact]
        public void Build_MixedTerminalAndLiveStates_PopulatesV1Schema()
        {
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed,    "App A"),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Installed,    "App B"),
                NewPkg("c", AppTargeted.Device, AppInstallationState.Error,        "App C"),
                NewPkg("d", AppTargeted.User,   AppInstallationState.Skipped,      "App D"),
                NewPkg("e", AppTargeted.User,   AppInstallationState.Postponed,    "App E"),
                NewPkg("f", AppTargeted.Device, AppInstallationState.Downloading,  "App F"),
                NewPkg("g", AppTargeted.Device, AppInstallationState.Installing,   "App G"),
                NewPkg("h", AppTargeted.Device, AppInstallationState.NotInstalled, "App H"),
            };

            var data = AppTrackingSummaryBuilder.Build(packages, ignoredCount: 2);

            Assert.Equal(8, (int)data["totalApps"]);
            Assert.Equal(2, (int)data["installed"]);
            Assert.Equal(1, (int)data["failed"]);
            Assert.Equal(1, (int)data["errorCount"]);
            Assert.Equal(1, (int)data["skipped"]);
            Assert.Equal(1, (int)data["postponed"]);
            Assert.Equal(1, (int)data["downloading"]);
            Assert.Equal(1, (int)data["installing"]);
            Assert.Equal(5, (int)data["completedApps"]);
            Assert.Equal(1, (int)data["pending"]);
            Assert.Equal(2, (int)data["ignoredCount"]);

            Assert.True((bool)data["hasErrors"]);
            Assert.False((bool)data["isAllCompleted"]);
        }

        [Fact]
        public void Build_DeviceVsUserErrors_SplitByAppTargeted()
        {
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Error),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Error),
                NewPkg("c", AppTargeted.User,   AppInstallationState.Error),
            };

            var data = AppTrackingSummaryBuilder.Build(packages);

            Assert.Equal(3, (int)data["errorCount"]);
            Assert.Equal(2, (int)data["deviceErrors"]);
            Assert.Equal(1, (int)data["userErrors"]);
        }

        [Fact]
        public void Build_AllInstalled_FlipsIsAllCompletedTrue()
        {
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Skipped),
                NewPkg("c", AppTargeted.User,   AppInstallationState.Postponed),
            };

            var data = AppTrackingSummaryBuilder.Build(packages);

            Assert.Equal(3, (int)data["completedApps"]);
            Assert.Equal(3, (int)data["totalApps"]);
            Assert.True((bool)data["isAllCompleted"]);
        }

        [Fact]
        public void Build_NameLists_ContainAppNamesPerBucket()
        {
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed,    "Sysinternals"),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Error,        "BrokenApp"),
                NewPkg("c", AppTargeted.User,   AppInstallationState.Skipped,      "OptionalApp"),
                NewPkg("d", AppTargeted.User,   AppInstallationState.Postponed,    "LaterApp"),
                NewPkg("e", AppTargeted.Device, AppInstallationState.NotInstalled, "PendingApp"),
            };

            var data = AppTrackingSummaryBuilder.Build(packages);

            Assert.Equal(new[] { "Sysinternals" }, (List<string>)data["installedNames"]);
            Assert.Equal(new[] { "BrokenApp" },    (List<string>)data["failedNames"]);
            Assert.Equal(new[] { "OptionalApp" },  (List<string>)data["skippedNames"]);
            Assert.Equal(new[] { "LaterApp" },     (List<string>)data["postponedNames"]);
            Assert.Equal(new[] { "PendingApp" },   (List<string>)data["pendingNames"]);
        }

        [Fact]
        public void Build_PendingNeverGoesNegative()
        {
            // Defensive: if state-machine ever feeds inconsistent data, pending is clamped at 0.
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Downloading),
                NewPkg("c", AppTargeted.Device, AppInstallationState.Installing),
            };

            var data = AppTrackingSummaryBuilder.Build(packages);

            Assert.Equal(0, (int)data["pending"]);
        }

        [Fact]
        public void Build_InFlightStates_PopulateInstallingAndDownloadingNameLists()
        {
            // c117946b debrief (2026-05-12) — UI needs a name (not just a counter) for
            // apps still in-flight at session-close, so it can render "App XY still
            // installing" instead of an opaque "installing: 1".
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Downloading, "DownloadingApp"),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Installing,  "InstallingApp"),
                NewPkg("c", AppTargeted.Device, AppInstallationState.Installed,   "DoneApp"),
            };

            var data = AppTrackingSummaryBuilder.Build(packages);

            Assert.Equal(new[] { "DownloadingApp" }, (List<string>)data["downloadingNames"]);
            Assert.Equal(new[] { "InstallingApp" },  (List<string>)data["installingNames"]);
        }

        [Fact]
        public void Build_EspAppsTimeoutErrors_LandInLikelyStuckBucket()
        {
            // c117946b debrief (2026-05-12) — when EnrollmentTerminationHandler promotes
            // a still-installing app to Error on terminal ESP-Apps failure, it stamps
            // ErrorPatternId=esp_apps_timeout via SetErrorContext. The summary must
            // count + name these in `likelyStuckNames` *in addition to* the regular
            // `failedNames` / `errorCount` so the UI can render them with hedged
            // "Likely stuck" wording while the analytics surface still sees them as
            // terminal-error states.
            var stuck = NewPkg("a", AppTargeted.Device, AppInstallationState.Error, "StuckApp");
            stuck.SetErrorContext("esp_apps_timeout", "Install status unconfirmed — ESP timed out while still installing.");

            var realFail = NewPkg("b", AppTargeted.Device, AppInstallationState.Error, "RealFail");
            realFail.SetErrorContext("IME-ERROR-ENFORCEMENT", "Enforcement failure");

            var data = AppTrackingSummaryBuilder.Build(new List<AppPackageState> { stuck, realFail });

            Assert.Equal(2, (int)data["failed"]);
            Assert.Equal(2, (int)data["errorCount"]);
            Assert.Equal(1, (int)data["likelyStuck"]);
            Assert.Equal(new[] { "StuckApp", "RealFail" }, (List<string>)data["failedNames"]);
            Assert.Equal(new[] { "StuckApp" }, (List<string>)data["likelyStuckNames"]);
        }

        [Fact]
        public void Build_NonStuckErrorsOnly_LeavesLikelyStuckBucketEmpty()
        {
            // Regression guard: ordinary IME-reported error patterns must NOT be classified
            // as likely-stuck. Only the canonical `esp_apps_timeout` tag opens that bucket.
            var pkg = NewPkg("a", AppTargeted.Device, AppInstallationState.Error, "RealFail");
            pkg.SetErrorContext("IME-ERROR-DOWNLOAD", "Content download failed");

            var data = AppTrackingSummaryBuilder.Build(new List<AppPackageState> { pkg });

            Assert.Equal(1, (int)data["failed"]);
            Assert.Equal(0, (int)data["likelyStuck"]);
            Assert.Empty((List<string>)data["likelyStuckNames"]);
        }

        [Fact]
        public void Build_DropsV2NestedKeys_EnsuresFlatSchema()
        {
            // The schema is intentionally flat — no perApp/byPhase nested objects. Pinning
            // this so a future refactor doesn't silently re-introduce nested structures
            // (which the RuleEngine flat-lookup path can't traverse without dot-paths).
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
            };

            var data = AppTrackingSummaryBuilder.Build(packages);

            Assert.False(data.ContainsKey("perApp"));
            Assert.False(data.ContainsKey("byPhase"));
            Assert.False(data.ContainsKey("installedApps"));
            Assert.False(data.ContainsKey("failedApps"));
            Assert.False(data.ContainsKey("skippedApps"));
            Assert.False(data.ContainsKey("postponedApps"));
        }
    }
}
