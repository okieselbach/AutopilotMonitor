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

            public Fixture()
            {
                var clock = new VirtualClock(Fixed);
                var post = new InformationalEventPost(Sink, clock);
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger);
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
    }
}
