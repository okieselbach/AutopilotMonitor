#nullable enable
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
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Shell-Core 62407 "RebootCoalescing" branch — ESP-initiated coalesced reboot (MDM policies
    /// forced a mid-ESP restart → second sign-in). Pure informational corroboration for the
    /// MdmRebootPolicyTracker's per-URI 2800 attribution: no FinalizingSetup transition, no C#
    /// event raise, and — as a DELIBERATE deviation from the other 62407 backfill branches — the
    /// backfill path DOES emit (the live emit races the very reboot that kills the process).
    /// </summary>
    public sealed class ShellCoreTrackerRebootCoalescingTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Utc);
        private const string CoalescingDescription =
            "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_DeviceSetup_RebootCoalescing', Value: '...'.";

        private static (ShellCoreTracker tracker, FakeSignalIngressSink sink) MakeTracker(TempDirectory tmp)
        {
            var sink = new FakeSignalIngressSink();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var tracker = new ShellCoreTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(sink, new VirtualClock(ClockNow)),
                logger: logger,
                helloTracker: null);
            return (tracker, sink);
        }

        private static IReadOnlyList<FakeSignalIngressSink.PostedSignal> ByType(FakeSignalIngressSink sink, string eventType) =>
            sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        private static IReadOnlyDictionary<string, object> Data(FakeSignalIngressSink.PostedSignal s) =>
            (IReadOnlyDictionary<string, object>)s.TypedPayload!;

        [Fact]
        public void ProcessEvent_62407_RebootCoalescing_EmitsWarning_ImmediateUpload()
        {
            using var tmp = new TempDirectory();
            var (tracker, sink) = MakeTracker(tmp);
            using (tracker)
            {
                var sourceTime = ClockNow.AddSeconds(-5);
                tracker.ProcessEvent(
                    eventId: ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                    description: CoalescingDescription,
                    timestamp: sourceTime,
                    providerName: "Microsoft-Windows-Shell-Core",
                    isBackfill: false);

                var s = Assert.Single(ByType(sink, Constants.EventTypes.EspRebootCoalescing));
                Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
                Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);
                Assert.Equal(sourceTime, s.OccurredAtUtc);
                Assert.Equal(CoalescingDescription, Data(s)["description"]);
            }
        }

        [Fact]
        public void ProcessEvent_62407_RebootCoalescing_FiresOncePerRun()
        {
            using var tmp = new TempDirectory();
            var (tracker, sink) = MakeTracker(tmp);
            using (tracker)
            {
                tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                    CoalescingDescription, ClockNow, "Microsoft-Windows-Shell-Core", isBackfill: false);
                tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                    CoalescingDescription, ClockNow.AddSeconds(1), "Microsoft-Windows-Shell-Core", isBackfill: false);

                Assert.Single(ByType(sink, Constants.EventTypes.EspRebootCoalescing));
            }
        }

        [Fact]
        public void ProcessEvent_62407_RebootCoalescing_DoesNotTriggerEspSemantics()
        {
            // The coalescing record is corroboration only — it must not trip the ESP-exit /
            // FinalizingSetup / failure machinery.
            using var tmp = new TempDirectory();
            var (tracker, sink) = MakeTracker(tmp);
            using (tracker)
            {
                var raised = new List<string>();
                tracker.FinalizingSetupPhaseTriggered += (_, reason) => raised.Add($"finalizing:{reason}");
                tracker.EspExited += (_, _) => raised.Add("espExited");
                tracker.EspFailureDetected += (_, type) => raised.Add($"failure:{type}");
                tracker.WhiteGloveCompleted += (_, _) => raised.Add("whiteGlove");

                tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                    CoalescingDescription, ClockNow, "Microsoft-Windows-Shell-Core", isBackfill: false);

                Assert.Empty(raised);
                Assert.False(tracker.IsEspExitedForTest);
                Assert.Empty(ByType(sink, Constants.EventTypes.EspExiting));
                Assert.Empty(ByType(sink, Constants.EventTypes.EspFailure));
            }
        }

        [Fact]
        public void ProcessEvent_62407_ExistingBranches_Unaffected()
        {
            // Ordering regression guard: WhiteGlove/failure/exiting still classify as before —
            // the RebootCoalescing check comes AFTER them.
            using var tmp = new TempDirectory();
            var (tracker, sink) = MakeTracker(tmp);
            using (tracker)
            {
                tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                    "Name: 'CommercialOOBE_ESPProgress_Page_Exiting'", ClockNow, "p", isBackfill: false);
                tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                    "Name: 'CommercialOOBE_ESPProgress_WhiteGlove_Success'", ClockNow, "p", isBackfill: false);

                Assert.Single(ByType(sink, Constants.EventTypes.EspExiting));
                Assert.Single(ByType(sink, Constants.EventTypes.WhiteGloveComplete));
                Assert.Empty(ByType(sink, Constants.EventTypes.EspRebootCoalescing));
            }
        }

        [Fact]
        public void Backfill_RebootCoalescing_Emits_WithBackfilledTrue_AndHistoricalTimestamp()
        {
            // Deliberate deviation from the other 62407 backfill branches: after the coalesced
            // reboot restarts the agent, the backfill re-emission is the only reliable delivery.
            using var tmp = new TempDirectory();
            var (tracker, sink) = MakeTracker(tmp);
            using (tracker)
            {
                var historical = ClockNow.AddMinutes(-3);
                tracker.HandleBackfillRecord(
                    ShellCoreTracker.EventId_ShellCore_WebAppEvent, CoalescingDescription, historical);

                var s = Assert.Single(ByType(sink, Constants.EventTypes.EspRebootCoalescing));
                Assert.Equal(historical, s.OccurredAtUtc);
                Assert.Equal(true, Data(s)["backfilled"]);
                Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            }
        }

        [Fact]
        public void Backfill_RebootCoalescing_AfterLiveFire_DoesNotDoubleEmit_SameProcess()
        {
            // Same-process dedup via the fire-once flag. (Cross-process live+backfill duplication
            // is possible and accepted — exists-semantics rule, identical historical timestamp.)
            using var tmp = new TempDirectory();
            var (tracker, sink) = MakeTracker(tmp);
            using (tracker)
            {
                tracker.ProcessEvent(ShellCoreTracker.EventId_ShellCore_WebAppEvent,
                    CoalescingDescription, ClockNow, "p", isBackfill: false);
                tracker.HandleBackfillRecord(
                    ShellCoreTracker.EventId_ShellCore_WebAppEvent, CoalescingDescription, ClockNow.AddMinutes(-1));

                Assert.Single(ByType(sink, Constants.EventTypes.EspRebootCoalescing));
            }
        }

        [Fact]
        public void Backfill_RebootCoalescing_DoesNotFallThroughToExitOrFailureHandling()
        {
            using var tmp = new TempDirectory();
            var (tracker, sink) = MakeTracker(tmp);
            using (tracker)
            {
                var raised = new List<string>();
                tracker.FinalizingSetupPhaseTriggered += (_, reason) => raised.Add($"finalizing:{reason}");
                tracker.EspExited += (_, _) => raised.Add("espExited");
                tracker.EspFailureDetected += (_, type) => raised.Add($"failure:{type}");

                tracker.HandleBackfillRecord(
                    ShellCoreTracker.EventId_ShellCore_WebAppEvent, CoalescingDescription, ClockNow.AddMinutes(-2));

                Assert.Empty(raised);
                Assert.False(tracker.IsEspExitedForTest);
            }
        }
    }
}
