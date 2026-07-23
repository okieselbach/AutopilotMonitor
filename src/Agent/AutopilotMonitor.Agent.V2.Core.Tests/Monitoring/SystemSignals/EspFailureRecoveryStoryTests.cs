using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Session 4910a5a5 (2026-07-23) — post-terminal recovery story. Field sequence:
    /// DeviceSetup/Apps failed (culprit app never started installing) → settle window expired
    /// → terminal EspFailureDetected → the user pressed "Try again" ~23 min later → Apps left
    /// the failed state → 10/10 installed → DeviceSetup resolved. Pre-fix, that recovery only
    /// appeared as a generic <c>subcategory_state_change</c>. Coverage:
    /// <list type="bullet">
    ///   <item><c>esp_failure_retry_detected</c> fires once when the failed subcategory leaves
    ///         the failed state AFTER the terminal fire.</item>
    ///   <item><c>esp_failure_recovered</c> fires once when the category then resolves to
    ///         success.</item>
    ///   <item>An in-settle-window recovery keeps emitting only
    ///         <c>esp_failure_settle_recovered</c> — the post-terminal pair stays silent.</item>
    ///   <item>Apps failures carry <c>LikelyCulpritApps</c> on the EspFailureDetected args,
    ///         never-started apps ranked first.</item>
    /// </list>
    /// </summary>
    public sealed class EspFailureRecoveryStoryTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 7, 23, 4, 30, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeSignalIngressSink Sink { get; } = new FakeSignalIngressSink();
            public List<EspFailureDetectedEventArgs> EspFailures { get; } = new List<EspFailureDetectedEventArgs>();
            public ProvisioningStatusTracker Tracker { get; }

            public Fixture(Func<IReadOnlyList<AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppPackageState>>? packageStatesProbe = null)
            {
                var clock = new VirtualClock(Fixed);
                var post = new InformationalEventPost(Sink, clock);
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger,
                    appxScanner: new FakeAppxDeploymentFailureScanner(),
                    backgroundDispatcher: action => action(),
                    packageStatesProbe: packageStatesProbe);
                Tracker.EspFailureDetected += (_, args) => EspFailures.Add(args);
            }

            public IReadOnlyList<(string EventType, IReadOnlyDictionary<string, object> Data)> CapturedEvents()
            {
                return Sink.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                    .Select(p =>
                    {
                        var eventType = p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var et) ? et : "";
                        var data = p.TypedPayload as IReadOnlyDictionary<string, object>
                                   ?? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>();
                        return (eventType, data);
                    })
                    .ToList();
            }

            /// <summary>Drive the session-4910a5a5 shape up to the terminal fire.</summary>
            public void DriveToTerminalDeviceSetupAppsFailure()
            {
                Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                    ""categorySucceeded"": null,
                    ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (1 of 1 applied)""},
                    ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (9 of 10 installed)""}
                }");
                Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                    ""categorySucceeded"": null,
                    ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (1 of 1 applied)""},
                    ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
                }");
                Tracker.TriggerSettleTimerForTest("DeviceSetupCategory.Status");
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void PostTerminalRetry_EmitsRetryDetected_Once()
        {
            using var f = new Fixture();
            f.DriveToTerminalDeviceSetupAppsFailure();
            Assert.Single(f.EspFailures);

            // "Try again": Apps leaves the failed state well after the settle window.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (1 of 1 applied)""},
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working on it...)""}
            }");

            var retry = Assert.Single(f.CapturedEvents(), e => e.EventType == "esp_failure_retry_detected");
            Assert.Equal("DeviceSetup", (string)retry.Data["category"]);
            Assert.Equal("Apps", (string)retry.Data["subcategory"]);
            Assert.Equal("failed", (string)retry.Data["previousState"]);
            Assert.Equal("inProgress", (string)retry.Data["newState"]);
            Assert.Equal("Provisioning_DeviceSetup_Apps_Failed", (string)retry.Data["failureType"]);
            Assert.True(retry.Data.ContainsKey("minutesSinceFailure"));

            // A later unrelated transition does not re-emit.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (1 of 1 applied)""},
                ""AppsSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Apps (10 of 10 installed)""}
            }");
            Assert.Single(f.CapturedEvents(), e => e.EventType == "esp_failure_retry_detected");
        }

        [Fact]
        public void PostTerminalRecovery_EmitsRecovered_OnCategorySuccess()
        {
            using var f = new Fixture();
            f.DriveToTerminalDeviceSetupAppsFailure();

            // Retry → all apps installed → Windows resolves the category boolean.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (1 of 1 applied)""},
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working on it...)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": true,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (1 of 1 applied)""},
                ""AppsSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Apps (10 of 10 installed)""}
            }");

            var recovered = Assert.Single(f.CapturedEvents(), e => e.EventType == "esp_failure_recovered");
            Assert.Equal("DeviceSetup", (string)recovered.Data["category"]);
            Assert.Equal("Apps", (string)recovered.Data["failedSubcategory"]);
            Assert.Equal("Provisioning_DeviceSetup_Apps_Failed", (string)recovered.Data["failureType"]);
            Assert.True(recovered.Data.ContainsKey("minutesSinceFailure"));

            // Duplicate category-success writes do not re-emit (fire-once).
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": true,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (1 of 1 applied)""},
                ""AppsSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Apps (10 of 10 installed, take 2)""}
            }");
            Assert.Single(f.CapturedEvents(), e => e.EventType == "esp_failure_recovered");
        }

        [Fact]
        public void InSettleWindowRecovery_KeepsPostTerminalPairSilent()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (0 of 1 installed)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
            }");
            // Recovery INSIDE the settle window — terminal fire is suppressed.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working on it...)""}
            }");
            f.Tracker.TriggerSettleTimerForTest("DeviceSetupCategory.Status");
            Assert.Empty(f.EspFailures);

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": true,
                ""AppsSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Apps (1 of 1 installed)""}
            }");

            var events = f.CapturedEvents();
            Assert.Contains(events, e => e.EventType == "esp_failure_settle_recovered");
            Assert.DoesNotContain(events, e => e.EventType == "esp_failure_retry_detected");
            Assert.DoesNotContain(events, e => e.EventType == "esp_failure_recovered");
        }

        [Fact]
        public void AppsFailure_TerminalArgs_CarryLikelyCulprits_NeverStartedFirst()
        {
            var neverStarted = BuildApp("app-bg", "Teams Backgrounds v2",
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Unknown,
                downloadingSeen: false);
            var startedButPending = BuildApp("app-slow", "Slow Installer",
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Unknown,
                downloadingSeen: true);
            var installed = BuildApp("app-ok", "Falcon Sensor",
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Installed,
                downloadingSeen: true);
            using var f = new Fixture(() => new[] { startedButPending, neverStarted, installed });

            f.DriveToTerminalDeviceSetupAppsFailure();

            var fired = Assert.Single(f.EspFailures);
            Assert.Equal(2, fired.LikelyCulpritApps.Count);
            // Never-started app ranked first — it starved the ESP gate without any IME event.
            Assert.Equal("Teams Backgrounds v2", fired.LikelyCulpritApps[0]);
            Assert.Equal("Slow Installer", fired.LikelyCulpritApps[1]);
        }

        [Fact]
        public void NonAppsFailure_TerminalArgs_CarryNoCulprits()
        {
            var neverStarted = BuildApp("app-bg", "Teams Backgrounds v2",
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Unknown,
                downloadingSeen: false);
            using var f = new Fixture(() => new[] { neverStarted });

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Working""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Failed""}
            }");
            f.Tracker.TriggerSettleTimerForTest("DeviceSetupCategory.Status");

            var fired = Assert.Single(f.EspFailures);
            Assert.Empty(fired.LikelyCulpritApps);
        }

        private static AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppPackageState BuildApp(
            string id, string name,
            AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState state,
            bool downloadingSeen)
        {
            var pkg = new AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppPackageState(id, 0);
            pkg.UpdateName(name);
            pkg.UpdateIntent(AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppIntent.Install);
            pkg.UpdateTargeted(AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppTargeted.Device);
            // DownloadingOrInstallingSeen is derived inside UpdateState (Downloading..Installing
            // range) — drive it through a Downloading transition instead of setting it directly.
            if (downloadingSeen)
                pkg.UpdateState(AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Downloading);
            if (state != AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Unknown
                && state != AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Downloading)
                pkg.UpdateState(state);
            return pkg;
        }
    }
}
