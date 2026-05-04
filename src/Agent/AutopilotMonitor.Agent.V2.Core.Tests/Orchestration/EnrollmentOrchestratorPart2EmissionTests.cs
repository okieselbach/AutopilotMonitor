using System;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// PR-A acceptance: <see cref="EnrollmentOrchestrator"/> emits the
    /// <c>whiteglove_resumed</c> lifecycle event exactly once when Start() detects a
    /// persisted <see cref="SessionStage.WhiteGloveSealed"/> snapshot, and emits nothing
    /// on a fresh first boot. The post is positioned AFTER the <c>onIngressReady</c>
    /// hook (so caller-side <c>agent_started</c> lands first, V1-symmetric ordering)
    /// and BEFORE collector hosts start (so the resumed-marker precedes any collector
    /// telemetry).
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EnrollmentOrchestratorPart2EmissionTests
    {
        [Fact]
        public void Fresh_start_does_not_emit_whiteglove_resumed()
        {
            using var rig = new EnrollmentOrchestratorRig();
            Directory.CreateDirectory(rig.StateDir);

            using var sut = rig.Build();
            sut.Start();

            Assert.False(sut.IsWhiteGlovePart2);

            sut.Stop();

            // Stop's terminal drain ships any pending items. None of the Event-kind
            // items must be a whiteglove_resumed event.
            Assert.DoesNotContain(
                rig.Uploader.Received.SelectMany(b => b),
                item => item.Kind == AutopilotMonitor.Agent.V2.Core.Transport.Telemetry.TelemetryItemKind.Event &&
                        item.PayloadJson != null &&
                        item.PayloadJson.Contains("\"whiteglove_resumed\""));
        }

        [Fact]
        public void Whiteglove_sealed_snapshot_emits_whiteglove_resumed_exactly_once()
        {
            using var rig = new EnrollmentOrchestratorRig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed Part-1 sealed state.
            var sealedState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .WithStepIndex(7)
                .Build();
            new SnapshotPersistence(Path.Combine(rig.StateDir, "snapshot.json")).Save(sealedState);

            rig.Uploader.QueueOk(20);

            using var sut = rig.Build();
            sut.Start();

            Assert.True(sut.IsWhiteGlovePart2);
            sut.Stop();

            // Exactly one Event-kind item carries whiteglove_resumed across all batches.
            // (The corresponding InformationalEvent signal is also persisted to the spool
            // for replay, but only the Event-kind item is the wire-level enrollment event.)
            var resumedEvents = rig.Uploader.Received
                .SelectMany(b => b)
                .Where(item =>
                    item.Kind == AutopilotMonitor.Agent.V2.Core.Transport.Telemetry.TelemetryItemKind.Event &&
                    item.PayloadJson != null &&
                    item.PayloadJson.Contains("\"whiteglove_resumed\""))
                .ToList();
            Assert.Single(resumedEvents);

            // Carries previousStage + resumedAtUtc payload markers.
            var payload = resumedEvents[0].PayloadJson!;
            Assert.Contains("WhiteGloveSealed", payload);
            Assert.Contains("resumedAtUtc", payload);
        }

        [Fact]
        public void Whiteglove_resumed_is_emitted_after_onIngressReady_hook_runs()
        {
            using var rig = new EnrollmentOrchestratorRig();
            Directory.CreateDirectory(rig.StateDir);

            var sealedState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .WithStepIndex(3)
                .Build();
            new SnapshotPersistence(Path.Combine(rig.StateDir, "snapshot.json")).Save(sealedState);

            using var sut = rig.Build();

            // While onIngressReady is running, the SignalLog must NOT yet contain the
            // InformationalEvent that carries whiteglove_resumed — Plan §11 V1 ordering:
            // agent_started lands first, then whiteglove_resumed.
            bool? logHadResumedDuringHook = null;
            sut.Start(ingress =>
            {
                var signalLog = GetSignalLog(sut);
                logHadResumedDuringHook = signalLog.ReadAll().Any(s =>
                    s.Kind == DecisionSignalKind.InformationalEvent &&
                    s.Payload != null &&
                    s.Payload.TryGetValue("eventType", out var et) &&
                    et == "whiteglove_resumed");
            });

            Assert.True(sut.IsWhiteGlovePart2);
            Assert.False(
                logHadResumedDuringHook,
                "whiteglove_resumed leaked onto the signal log BEFORE onIngressReady returned.");

            // Spin until the worker thread has reduced the post into the log.
            var signalLog = GetSignalLog(sut);
            Assert.True(SpinWait.SpinUntil(
                () => signalLog.ReadAll().Any(s =>
                    s.Kind == DecisionSignalKind.InformationalEvent &&
                    s.Payload != null &&
                    s.Payload.TryGetValue("eventType", out var et) &&
                    et == "whiteglove_resumed"),
                3000),
                "Expected whiteglove_resumed InformationalEvent on signal log after Start.");

            sut.Stop();
        }

        [Fact]
        public void Whiteglove_sealed_snapshot_resumes_through_archive_and_reset_only()
        {
            // PR-A removed the SessionRecovered post and PR-B removed the enum value +
            // handler entirely; the resume path is now archive-and-reset followed by a
            // fresh Classic enrollment plus a single whiteglove_resumed timeline event.
            // No "session-recovered" lifecycle signal should land in the signal log.
            using var rig = new EnrollmentOrchestratorRig();
            Directory.CreateDirectory(rig.StateDir);

            var sealedState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .WithStepIndex(2)
                .Build();
            new SnapshotPersistence(Path.Combine(rig.StateDir, "snapshot.json")).Save(sealedState);

            using var sut = rig.Build();
            sut.Start();

            // Give the worker a moment.
            Thread.Sleep(100);
            var signalLog = GetSignalLog(sut);
            // No signal carrying a session-recovered identifier in payload/source.
            Assert.DoesNotContain(
                signalLog.ReadAll(),
                s => s.SourceOrigin != null && s.SourceOrigin.Contains("session_recovered"));

            sut.Stop();
        }

        private static ISignalLogWriter GetSignalLog(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_signalLog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (ISignalLogWriter)field!.GetValue(sut)!;
        }
    }
}
