using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

#pragma warning disable xUnit1031  // Task.Wait + SpinWait for deterministic thread coordination

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    // Persistence-IO + Task-coordination tests are timing-sensitive enough that the
    // parallel xUnit pool starves the worker threads on a busy CI box. Serialised
    // against the other threading-sensitive classes via SerialThreading.
    [Collection("SerialThreading")]
    public sealed class EnrollmentOrchestratorTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);


        // ========================================================================= Ctor

        [Fact]
        public void Ctor_rejects_empty_required_strings()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            Assert.Throws<ArgumentException>(() => new EnrollmentOrchestrator(
                "", "T1", rig.StateDir, rig.TransportDir, rig.Clock, rig.Logger, rig.Uploader, rig.Classifiers));
            Assert.Throws<ArgumentException>(() => new EnrollmentOrchestrator(
                "S1", "", rig.StateDir, rig.TransportDir, rig.Clock, rig.Logger, rig.Uploader, rig.Classifiers));
            Assert.Throws<ArgumentException>(() => new EnrollmentOrchestrator(
                "S1", "T1", "", rig.TransportDir, rig.Clock, rig.Logger, rig.Uploader, rig.Classifiers));
            Assert.Throws<ArgumentException>(() => new EnrollmentOrchestrator(
                "S1", "T1", rig.StateDir, "", rig.Clock, rig.Logger, rig.Uploader, rig.Classifiers));
        }

        [Fact]
        public void Ctor_rejects_null_dependencies()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            Assert.Throws<ArgumentNullException>(() => new EnrollmentOrchestrator(
                "S1", "T1", rig.StateDir, rig.TransportDir, null!, rig.Logger, rig.Uploader, rig.Classifiers));
            Assert.Throws<ArgumentNullException>(() => new EnrollmentOrchestrator(
                "S1", "T1", rig.StateDir, rig.TransportDir, rig.Clock, null!, rig.Uploader, rig.Classifiers));
            Assert.Throws<ArgumentNullException>(() => new EnrollmentOrchestrator(
                "S1", "T1", rig.StateDir, rig.TransportDir, rig.Clock, rig.Logger, null!, rig.Classifiers));
            Assert.Throws<ArgumentNullException>(() => new EnrollmentOrchestrator(
                "S1", "T1", rig.StateDir, rig.TransportDir, rig.Clock, rig.Logger, rig.Uploader, null!));
        }

        [Fact]
        public void Observability_before_start_throws()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            Assert.Throws<InvalidOperationException>(() => sut.CurrentState);
            Assert.Throws<InvalidOperationException>(() => sut.IngressSink);
            // Regression guard: Program.WireTelemetryServerResponse must run AFTER Start().
            // Touching Transport pre-Start threw InvalidOperationException and crashed the agent
            // on every VM after the first-run-ghost-detect fix let it reach this far.
            Assert.Throws<InvalidOperationException>(() => sut.Transport);
        }

        [Fact]
        public void Transport_is_available_after_start()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();
            Assert.NotNull(sut.Transport);
        }

        // ========================================================================= Start

        [Fact]
        public void Start_instantiates_pipeline_no_exception()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();

            sut.Start();

            Assert.NotNull(sut.CurrentState);
            Assert.Equal("S1", sut.CurrentState.SessionId);
            Assert.Equal("T1", sut.CurrentState.TenantId);
            Assert.NotNull(sut.IngressSink);
            Assert.False(sut.IsQuarantineRequested);

            sut.Stop();
        }

        [Fact]
        public void Start_creates_state_and_transport_directories()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            Assert.False(Directory.Exists(rig.StateDir));
            Assert.False(Directory.Exists(rig.TransportDir));

            var sut = rig.Build();
            sut.Start();

            Assert.True(Directory.Exists(rig.StateDir));
            Assert.True(Directory.Exists(rig.TransportDir));

            sut.Stop();
        }

        [Fact]
        public void Start_creates_initial_state_when_no_snapshot()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            Assert.Equal(SessionStage.SessionStarted, sut.CurrentState.Stage);
            Assert.Equal(0, sut.CurrentState.StepIndex);

            sut.Stop();
        }

        [Fact]
        public void Start_loads_recovered_snapshot_when_file_exists()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            Directory.CreateDirectory(rig.StateDir);
            // Seed a snapshot with a non-initial stage so recovery is detectable.
            var persistedState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(7)
                .Build();
            var snapshotPath = Path.Combine(rig.StateDir, "snapshot.json");
            var seed = new SnapshotPersistence(snapshotPath);
            seed.Save(persistedState);

            var sut = rig.Build();
            sut.Start();

            Assert.Equal(SessionStage.EspDeviceSetup, sut.CurrentState.Stage);
            Assert.Equal(7, sut.CurrentState.StepIndex);

            sut.Stop();
        }

        [Fact]
        public void Start_twice_throws()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            Assert.Throws<InvalidOperationException>(() => sut.Start());

            sut.Stop();
        }

        // ========================================================================= Stop

        [Fact]
        public void Stop_before_start_is_noop()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();

            sut.Stop();   // does not throw
        }

        [Fact]
        public void Stop_is_idempotent()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            sut.Stop();
            sut.Stop();   // no-op on second call
        }

        [Fact]
        public void Stop_flushes_final_snapshot_with_current_state()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            // Drive a state change — post a SessionStarted synthetic signal so the reducer
            // advances StepIndex. Use the IngressSink to mirror the prod path.
            sut.IngressSink.Post(
                kind: DecisionSignalKind.SessionStarted,
                occurredAtUtc: At,
                sourceOrigin: "Test",
                evidence: new Evidence(EvidenceKind.Synthetic, "test", "test"));

            // Wait until the worker applied it (StepIndex > 0 at minimum).
            Assert.True(SpinWait.SpinUntil(() => sut.CurrentState.StepIndex > 0, 2000));
            var stepIndexBeforeStop = sut.CurrentState.StepIndex;

            sut.Stop();

            // Snapshot file must exist + contain the state AT STOP. SessionStarted may cascade
            // through additional reducer steps (ClassifierTick arm/fire, bootstrap effects,
            // now that the P0 fix actually lets deadlines run instead of dead-ending), so the
            // final StepIndex is >= the value observed before Stop. Stop drains the ingress
            // synchronously before saving the snapshot — so the saved StepIndex matches the
            // state at the moment Save is called, which is what this test is really asserting.
            var snapshotPath = Path.Combine(rig.StateDir, "snapshot.json");
            Assert.True(File.Exists(snapshotPath));
            var reloaded = new SnapshotPersistence(snapshotPath).Load();
            Assert.NotNull(reloaded);
            Assert.Equal(sut.CurrentState.StepIndex, reloaded!.StepIndex);
            Assert.True(reloaded!.StepIndex >= stepIndexBeforeStop,
                $"Snapshot StepIndex ({reloaded.StepIndex}) regressed below observed ({stepIndexBeforeStop}).");
        }

        // ========================================================================= Dispose

        [Fact]
        public void Dispose_stops_orchestrator()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            sut.Dispose();

            // Second Dispose is safe; Start after Dispose throws ObjectDisposedException.
            sut.Dispose();
            Assert.Throws<ObjectDisposedException>(() => sut.Start());
        }

        // ========================================================================= Deadline bridge

        [Fact]
        public void Deadline_fired_posts_DeadlineFired_signal_to_ingress()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            // Schedule a past-due deadline → DeadlineScheduler fires via ThreadPool immediately.
            var scheduler = GetScheduler(sut);
            var pastDue = new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: At.AddMinutes(-1),
                firesSignalKind: DecisionSignalKind.DeadlineFired);
            scheduler.Schedule(pastDue);

            // Wait for the signal-log to record the synthetic DeadlineFired signal. We read
            // via the SignalLogWriter's own thread-safe ReadAll() to avoid racing the worker's
            // file-append lock (File.ReadAllLines opens without FileShare.ReadWrite on net48).
            var signalLog = GetSignalLog(sut);
            Assert.True(SpinWait.SpinUntil(
                () => ContainsDeadlineFired(signalLog),
                3000));

            sut.Stop();
        }

        [Fact]
        public void Deadline_fired_late_keeps_the_due_time_as_stamp_and_carries_it_in_the_payload()
        {
            // A deadline that was due while the device slept (or the agent was down) fires only
            // afterwards. The signal keeps the due time as OccurredAtUtc (D-238: the outage must not
            // become session duration) and repeats it in the payload so the stale-fire guards
            // identify the incarnation independently of the stamp.
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            var dueAt = At.AddMinutes(-5);
            var scheduler = GetScheduler(sut);
            scheduler.Schedule(new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: dueAt,
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = "hello_safety" }));

            var signalLog = GetSignalLog(sut);
            Assert.True(SpinWait.SpinUntil(() => ContainsDeadlineFired(signalLog), 3000));

            DecisionSignal? fired = null;
            foreach (var sig in signalLog.ReadAll())
            {
                if (sig.Kind == DecisionSignalKind.DeadlineFired) { fired = sig; break; }
            }
            Assert.NotNull(fired);
            Assert.Equal(dueAt, fired!.OccurredAtUtc);
            Assert.NotNull(fired.Payload);
            Assert.Equal("hello_safety", fired.Payload![SignalPayloadKeys.Deadline]);
            Assert.Equal(dueAt.ToString("O"), fired.Payload[SignalPayloadKeys.DeadlineDueAtUtc]);

            sut.Stop();
        }

        // ========================================================================= Quarantine

        [Fact]
        public void TriggerQuarantine_sets_flag_and_reason()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build();
            sut.Start();

            Assert.False(sut.IsQuarantineRequested);

            sut.TriggerQuarantine("test-reason");

            Assert.True(sut.IsQuarantineRequested);
            Assert.Equal("test-reason", sut.QuarantineReason);

            sut.Stop();
        }

        // ========================================================================= Helpers

        private static bool ContainsDeadlineFired(ISignalLogWriter signalLog)
        {
            foreach (var sig in signalLog.ReadAll())
            {
                if (sig.Kind == DecisionSignalKind.DeadlineFired) return true;
            }
            return false;
        }

        private static ISignalLogWriter GetSignalLog(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_signalLog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (ISignalLogWriter)field!.GetValue(sut)!;
        }

        /// <summary>
        /// The orchestrator owns its <see cref="DeadlineScheduler"/> as a private field;
        /// tests need it to script past-due deadlines. Reflection access is acceptable for
        /// a single integration-ish test; production code doesn't expose it.
        /// </summary>
        private static DeadlineScheduler GetScheduler(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_scheduler",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            // Field is now typed IDeadlineScheduler (post-#50 #E testability hook); this
            // helper still only runs in the tests that drive the real scheduler (past-due
            // fire etc.), so the cast back to the concrete type is safe.
            return (DeadlineScheduler)field!.GetValue(sut)!;
        }
    }
}
