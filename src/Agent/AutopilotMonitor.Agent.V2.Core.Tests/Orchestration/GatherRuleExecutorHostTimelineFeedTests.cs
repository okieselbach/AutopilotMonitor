#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// GatherRuleExecutorHost feeds its phase_change / phase_exit / on_event triggers from the
    /// POST-REDUCE <see cref="TimelineEventStream"/> (emitted timeline events), not from raw
    /// signals. Regression anchor: session 32312a32 (rsneuffen.de) — a phase_change rule on
    /// FinalizingSetup fired at the raw EspPhaseChanged(FinalizingSetup) signal (ESP exit),
    /// 7 minutes before the engine's RealmJoin-gated phase_transition(FinalizingSetup), and read
    /// a registry key before the RealmJoin package wrote it. With the timeline feed the rule
    /// fires only when the phase declaration is actually emitted.
    /// Rules use the absent-registry-path pattern so no real system state is touched; execution
    /// is observed via the InformationalEvent posts the host sends through its ingress sink.
    /// </summary>
    [Collection("SerialThreading")] // rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorHostTimelineFeedTests : IDisposable
    {
        private const string AbsentPath =
            "HKLM\\SOFTWARE\\AutopilotMonitorTests\\DefinitelyAbsent_5b1f03a2-0000-0000-0000-000000000000";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly CapturingSink _sink = new CapturingSink();
        private readonly TimelineEventStream _stream = new TimelineEventStream();
        private GatherRuleExecutorHost? _host;

        public void Dispose()
        {
            _host?.Dispose();
            _tmp.Dispose();
        }

        private sealed class CapturingSink : ISignalIngressSink
        {
            private readonly List<string> _eventTypes = new List<string>();
            private readonly object _gate = new object();

            public int CountOf(string eventType)
            {
                lock (_gate)
                {
                    var n = 0;
                    foreach (var t in _eventTypes)
                        if (string.Equals(t, eventType, StringComparison.Ordinal)) n++;
                    return n;
                }
            }

            public void Post(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
            {
                if (kind != DecisionSignalKind.InformationalEvent || payload == null) return;
                if (!payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)) return;
                lock (_gate) _eventTypes.Add(eventType);
            }
        }

        private void BuildHost(params GatherRule[] rules)
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _host = new GatherRuleExecutorHost(
                sessionId: "sess",
                tenantId: "tenant",
                ingress: _sink,
                clock: new SystemClock(),
                logger: logger,
                rules: new List<GatherRule>(rules),
                imeLogPathOverride: null,
                unrestrictedMode: true,
                gatherDebugLogPath: null,
                timelineEvents: _stream);
            _host.Start();
        }

        private bool WaitForCount(string eventType, int count, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_sink.CountOf(eventType) >= count) return true;
                Thread.Sleep(25);
            }
            return _sink.CountOf(eventType) >= count;
        }

        private static GatherRule Rule(string id, string trigger, string? triggerPhase = null, string? triggerEventType = null)
            => new GatherRule
            {
                RuleId = id,
                Title = id,
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = trigger,
                TriggerPhase = triggerPhase ?? string.Empty,
                TriggerEventType = triggerEventType ?? string.Empty,
                OutputEventType = "gather_host_test",
                Enabled = true,
            };

        [Fact]
        public void PhaseChangeRule_FiresOnEmittedPhaseDeclaration_NotOnPhaselessEvents()
        {
            BuildHost(Rule("GATHER-HOST-001", "phase_change", triggerPhase: "FinalizingSetup"));

            // The raw-world sequence of session 32312a32 as it reaches the emitted timeline:
            // esp_exiting and hello_wizard_started are emitted WITHOUT a phase declaration
            // (the engine holds FinalizingSetup back behind the RealmJoin gate) — no fire.
            _stream.Publish("esp_exiting", EnrollmentPhase.Unknown);
            _stream.Publish("hello_wizard_started", EnrollmentPhase.Unknown);
            Thread.Sleep(300);
            Assert.Equal(0, _sink.CountOf("gather_host_test"));

            // Only the engine's actual phase declaration fires the rule.
            _stream.Publish("phase_transition", EnrollmentPhase.FinalizingSetup);
            Assert.True(WaitForCount("gather_host_test", 1));
        }

        [Fact]
        public void OnEventRule_FiresForEngineEmittedEventTypes()
        {
            // enrollment_complete is an engine-emitted effect event; it never existed on the raw
            // InformationalEvent signal stream, so the docs-promised "On Event with
            // enrollment_complete" trigger was dead under the previous SignalPosted feed.
            BuildHost(Rule("GATHER-HOST-002", "on_event", triggerEventType: "enrollment_complete"));

            _stream.Publish("enrollment_complete", EnrollmentPhase.Unknown);
            Assert.True(WaitForCount("gather_host_test", 1));
        }

        [Fact]
        public void PhaseFeed_DedupsConsecutiveSamePhaseDeclarations()
        {
            // "Any phase" phase_change rule fires on every phase TRANSITION — a re-declared
            // identical phase (e.g. duplicate esp_phase_changed) must not count as one.
            BuildHost(Rule("GATHER-HOST-003", "phase_change", triggerPhase: ""));

            _stream.Publish("esp_phase_changed", EnrollmentPhase.DeviceSetup);
            Assert.True(WaitForCount("gather_host_test", 1));

            _stream.Publish("esp_phase_changed", EnrollmentPhase.DeviceSetup);
            Thread.Sleep(300);
            Assert.Equal(1, _sink.CountOf("gather_host_test"));

            _stream.Publish("esp_phase_changed", EnrollmentPhase.AccountSetup);
            Assert.True(WaitForCount("gather_host_test", 2));
        }

        [Fact]
        public void PhaseExitRule_FiresWhenEmittedTimelineLeavesThePhase()
        {
            BuildHost(Rule("GATHER-HOST-004", "phase_exit", triggerPhase: "AccountSetup"));

            _stream.Publish("esp_phase_changed", EnrollmentPhase.AccountSetup);
            Thread.Sleep(300);
            Assert.Equal(0, _sink.CountOf("gather_host_test"));

            _stream.Publish("phase_transition", EnrollmentPhase.FinalizingSetup);
            Assert.True(WaitForCount("gather_host_test", 1));
        }

        [Fact]
        public void StoppedHost_NoLongerReactsToTheStream()
        {
            BuildHost(Rule("GATHER-HOST-005", "on_event", triggerEventType: "enrollment_complete"));

            _host!.Stop();
            _stream.Publish("enrollment_complete", EnrollmentPhase.Unknown);
            Thread.Sleep(300);
            Assert.Equal(0, _sink.CountOf("gather_host_test"));
        }
    }
}
