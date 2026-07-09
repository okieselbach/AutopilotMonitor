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
    /// Session 9d052230 (2026-05-21) regression coverage. When ESP DeviceSetup
    /// <c>Apps</c> transitions to <c>failed</c> with a Windows HRESULT in its
    /// <c>statusText</c> (e.g. <c>"Apps (0x87d1041c)"</c>), the tracker must:
    /// <list type="bullet">
    ///   <item>NOT fire <see cref="ProvisioningStatusTracker.EspFailureDetected"/> immediately —
    ///         a 30 s settle window gives ImeLogTracker time to emit the underlying
    ///         <c>app_install_failed</c> event so the timeline carries the app-level failure.</item>
    ///   <item>Emit <c>esp_failure_settle_started</c> (Info) for observability.</item>
    ///   <item>Emit <c>esp_provisioning_status</c> with a top-level
    ///         <c>failedSubcategoryErrorCode</c> field so the UI / RuleEngine can match on it
    ///         without parsing nested statusText.</item>
    ///   <item>On settle-window expiry, fire <c>EspFailureDetected</c> with enriched
    ///         <see cref="EspFailureDetectedEventArgs"/>.</item>
    /// </list>
    /// </summary>
    public sealed class ProvisioningFailureSettleWindowTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 5, 21, 12, 25, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeSignalIngressSink Sink { get; } = new FakeSignalIngressSink();
            public List<EspFailureDetectedEventArgs> EspFailures { get; } = new List<EspFailureDetectedEventArgs>();
            public ProvisioningStatusTracker Tracker { get; }

            public Fixture(Func<IReadOnlyList<AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppPackageState>> packageStatesProbe = null)
            {
                var clock = new VirtualClock(Fixed);
                var post = new InformationalEventPost(Sink, clock);
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger,
                    // Fake scanner + synchronous dispatcher: the Apps-failure tests below arm the
                    // settle window, which triggers the AppX enrichment scan — the default seams
                    // would hit the real event log via Task.Run (mock ALL system calls).
                    appxScanner: new FakeAppxDeploymentFailureScanner(),
                    backgroundDispatcher: action => action(),
                    packageStatesProbe: packageStatesProbe);
                Tracker.EspFailureDetected += (_, args) => EspFailures.Add(args);
            }

            // Posted EnrollmentEvent equivalents: filter the sink for InformationalEvent signals,
            // pull the eventType from payload and the data from typedPayload.
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

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void FailedAppsSubcategoryWithHResult_armsSettleWindow_DoesNotFireImmediately()
        {
            using var f = new Fixture();

            // First seen — Apps in_progress.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (4 of 4 applied)""},
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Identifying)""}
            }");

            // Apps transitions to failed with HRESULT in statusText.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Security policies (4 of 4 applied)""},
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (0x87d1041c)""}
            }");

            // EspFailureDetected is delayed by the settle window — no immediate fire.
            Assert.Empty(f.EspFailures);

            var captured = f.CapturedEvents();

            // esp_failure_settle_started observability event is emitted.
            var settleStarted = captured.SingleOrDefault(e => e.EventType == "esp_failure_settle_started");
            Assert.NotEqual(default, settleStarted);
            Assert.Equal("DeviceSetup", (string)settleStarted.Data["category"]);
            Assert.Equal("Apps", (string)settleStarted.Data["failedSubcategory"]);
            Assert.Equal("0x87d1041c", (string)settleStarted.Data["errorCode"]);
            Assert.Equal(ProvisioningStatusTracker.ProvisioningFailureSettleWindowSeconds, (int)settleStarted.Data["settleSeconds"]);

            // esp_provisioning_status (subcategory_state_change) carries failedSubcategoryErrorCode top-level.
            var statusEvents = captured.Where(e => e.EventType == AutopilotMonitor.Shared.Constants.EventTypes.EspProvisioningStatus).ToList();
            var transitionEvent = statusEvents.Last(e =>
                e.Data.TryGetValue("changeType", out var ct) && (string)ct == "subcategory_state_change");
            Assert.Equal("0x87d1041c", (string)transitionEvent.Data["failedSubcategoryErrorCode"]);
            Assert.Equal("Apps", (string)transitionEvent.Data["failedSubcategories"]);

            // TriggerSettleTimerForTest synchronously runs the timer callback.
            f.Tracker.TriggerSettleTimerForTest("DeviceSetupCategory.Status");

            var fired = Assert.Single(f.EspFailures);
            Assert.Equal("Provisioning_DeviceSetup_Apps_Failed", fired.FailureType);
            Assert.Equal("0x87d1041c", fired.ErrorCode);
            Assert.Equal("Apps", fired.FailedSubcategory);
            Assert.Equal("DeviceSetup", fired.Category);
        }

        [Fact]
        public void FailedSubcategoryWithoutHResult_stillArmsSettleWindow_ErrorCodeIsNull()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Working""}
            }");

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": false,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Failed""}
            }");

            Assert.Empty(f.EspFailures);
            var captured = f.CapturedEvents();
            var settleStarted = captured.SingleOrDefault(e => e.EventType == "esp_failure_settle_started");
            Assert.NotEqual(default, settleStarted);
            Assert.False(settleStarted.Data.ContainsKey("errorCode"));

            f.Tracker.TriggerSettleTimerForTest("DeviceSetupCategory.Status");

            var fired = Assert.Single(f.EspFailures);
            Assert.Null(fired.ErrorCode);
            Assert.Equal("SecurityPolicies", fired.FailedSubcategory);
        }

        [Fact]
        public void RepeatedFailedProcessing_armsSettleTimer_OnlyOnce()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Identifying)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (0x87d1041c)""}
            }");
            // Re-process with the same failed-state JSON — fire-once guard must prevent a
            // second settle-window arm and a duplicate esp_failure_settle_started event.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": false,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (0x87d1041c)""}
            }");

            var captured = f.CapturedEvents();
            Assert.Single(captured.Where(e => e.EventType == "esp_failure_settle_started"));
        }

        // =====================================================================
        // Session c071e92b — recovery during the settle window (ESP "Try again"
        // flipped Apps failed → inProgress 12 s after settle start; the expiry
        // handler fired the latched args anyway and terminated the agent on a
        // failure Windows no longer reported).
        // =====================================================================

        [Fact]
        public void RecoveryDuringSettleWindow_SuppressesFailure_EmitsRecoveredEvent()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (0 of 1 installed)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
            }");

            // Failure retracted inside the settle window (e.g. user clicked "Try again").
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working on it...)""}
            }");

            f.Tracker.TriggerSettleTimerForTest("AccountSetupCategory.Status");

            // No terminal fire — the failure was retracted.
            Assert.Empty(f.EspFailures);

            var recovered = Assert.Single(f.CapturedEvents()
                .Where(e => e.EventType == "esp_failure_settle_recovered"));
            Assert.Equal("AccountSetup", (string)recovered.Data["category"]);
            Assert.Equal("Apps", (string)recovered.Data["failedSubcategory"]);
            Assert.Equal("inProgress", (string)recovered.Data["observedState"]);
        }

        [Fact]
        public void RecoveryDuringSettleWindow_ReFailure_ArmsFreshSettleWindow_ThenFires()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (0 of 1 installed)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working on it...)""}
            }");
            f.Tracker.TriggerSettleTimerForTest("AccountSetupCategory.Status");
            Assert.Empty(f.EspFailures);

            // The retry fails again — the cleared fire-once gate must allow a FRESH settle window.
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
            }");

            var settleStarted = f.CapturedEvents()
                .Where(e => e.EventType == "esp_failure_settle_started").ToList();
            Assert.Equal(2, settleStarted.Count);

            // Second expiry with the failure still present → terminal fire.
            f.Tracker.TriggerSettleTimerForTest("AccountSetupCategory.Status");
            var fired = Assert.Single(f.EspFailures);
            Assert.Equal("Provisioning_AccountSetup_Apps_Failed", fired.FailureType);
        }

        [Fact]
        public void SubcategoryRecovery_ButCategoryResolvedFailed_StillFires()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (0 of 1 installed)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": false,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
            }");
            // Subcategory flips back but Windows keeps the category-level failed verdict —
            // the resolved boolean is authoritative, the failure must still fire.
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": false,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (Working on it...)""}
            }");

            f.Tracker.TriggerSettleTimerForTest("AccountSetupCategory.Status");

            Assert.Single(f.EspFailures);
            Assert.Empty(f.CapturedEvents().Where(e => e.EventType == "esp_failure_settle_recovered"));
        }

        // =====================================================================
        // Session c071e92b — Apps-subcategory failures name the tracked apps
        // that never completed (e.g. Company Portal Store app stuck "pending",
        // invisible to both the starved-app probe and esp_apps_failure_correlation).
        // =====================================================================

        private static AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppPackageState BuildApp(
            string id, string name,
            AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState state,
            AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppIntent intent,
            AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppTargeted targeted)
        {
            var pkg = new AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppPackageState(id, 0);
            pkg.UpdateName(name);
            pkg.UpdateIntent(intent);
            pkg.UpdateTargeted(targeted);
            if (state != AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Unknown)
                pkg.UpdateState(state);
            return pkg;
        }

        [Fact]
        public void AppsFailure_SettleStarted_NamesTrackedAppsNotCompleted()
        {
            var pending = BuildApp("app-cp", "Company Portal",
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Unknown,
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppIntent.Install,
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppTargeted.User);
            var installed = BuildApp("app-ok", "Falcon Sensor",
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Installed,
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppIntent.Install,
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppTargeted.Device);
            using var f = new Fixture(() => new[] { pending, installed });

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Apps (0 of 1 installed)""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Apps (Error)""}
            }");

            var settleStarted = Assert.Single(f.CapturedEvents()
                .Where(e => e.EventType == "esp_failure_settle_started"));
            Assert.Equal(1, (int)settleStarted.Data["trackedAppsNotCompletedCount"]);
            var apps = (List<Dictionary<string, object>>)settleStarted.Data["trackedAppsNotCompleted"];
            var app = Assert.Single(apps);
            Assert.Equal("Company Portal", (string)app["appName"]);
            Assert.Equal("Unknown", (string)app["state"]);
            Assert.False((bool)app["everStartedInstalling"]);
        }

        [Fact]
        public void NonAppsFailure_SettleStarted_CarriesNoAppFields()
        {
            var pending = BuildApp("app-cp", "Company Portal",
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppInstallationState.Unknown,
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppIntent.Install,
                AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime.AppTargeted.User);
            using var f = new Fixture(() => new[] { pending });

            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""inProgress"",""subcategoryStatusText"":""Working""}
            }");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""SecurityPoliciesSubcategory"": {""subcategoryState"":""failed"",""subcategoryStatusText"":""Failed""}
            }");

            var settleStarted = Assert.Single(f.CapturedEvents()
                .Where(e => e.EventType == "esp_failure_settle_started"));
            Assert.False(settleStarted.Data.ContainsKey("trackedAppsNotCompleted"));
            Assert.False(settleStarted.Data.ContainsKey("trackedAppsNotCompletedCount"));
        }
    }
}
