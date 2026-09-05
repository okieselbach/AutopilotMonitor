using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Signals;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;
using AutopilotMonitor.Shared.Models;

#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Codex Finding 1 regression gate: without the emitter hook in
    /// <see cref="SignalIngress"/>, no signal ever reached the telemetry transport and the
    /// backend <c>/api/sessions/{id}/signals</c> endpoint saw zero traffic. These tests
    /// verify that every accepted signal also lands on the transport, and that a throwing
    /// transport does NOT take down the ingress worker.
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class SignalIngressUploadPathTests
    {
        private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
        private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static Evidence RawEvidence(string id = "raw-1") =>
            new Evidence(EvidenceKind.Raw, id, $"evidence-{id}");

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public SignalLogWriter SignalLog { get; }
            public SessionTraceOrdinalProvider TraceCounter { get; } = new SessionTraceOrdinalProvider();
            public FakeDecisionStepProcessor Processor { get; } = new FakeDecisionStepProcessor();
            public VirtualClock Clock { get; } = new VirtualClock(At);
            public DecisionEngine Engine { get; } = new DecisionEngine();
            public FakeTelemetryTransport Transport { get; } = new FakeTelemetryTransport();

            public Rig() { SignalLog = new SignalLogWriter(Tmp.File("signal-log.jsonl")); }

            public SignalIngress Build(TelemetrySignalEmitter? emitter)
                => new SignalIngress(
                    engine: Engine,
                    signalLog: SignalLog,
                    traceCounter: TraceCounter,
                    processor: Processor,
                    clock: Clock,
                    backPressureObserver: null,
                    signalEmitter: emitter);

            public void Dispose() => Tmp.Dispose();
        }

        private static bool WaitFor(Func<bool> cond, int timeoutMs = 5000)
            => SpinWait.SpinUntil(cond, timeoutMs);

        [Fact]
        public void Post_emits_to_transport_after_SignalLog_append_succeeds()
        {
            using var rig = new Rig();
            var emitter = new TelemetrySignalEmitter(rig.Transport, SessionId, TenantId);
            var ing = rig.Build(emitter);
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("e-1"));

            Assert.True(WaitFor(() => rig.Transport.EnqueueCount == 1));
            Assert.Equal(TelemetryItemKind.Signal, rig.Transport.Enqueued[0].Kind);
            Assert.Equal($"{TenantId}_{SessionId}", rig.Transport.Enqueued[0].PartitionKey);

            ing.Stop();
        }

        [Fact]
        public void Post_emits_one_transport_item_per_signal()
        {
            using var rig = new Rig();
            var emitter = new TelemetrySignalEmitter(rig.Transport, SessionId, TenantId);
            var ing = rig.Build(emitter);
            ing.Start();

            for (int i = 0; i < 5; i++)
            {
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence($"e-{i}"));
            }

            Assert.True(WaitFor(() => rig.Transport.EnqueueCount == 5));
            // RowKeys are D19(ordinal) — SignalIngress assigns ordinals 0..4 against an empty log.
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(((long)i).ToString("D19"), rig.Transport.Enqueued[i].RowKey);
            }

            ing.Stop();
        }

        [Fact]
        public void Post_without_emitter_keeps_pre_M5_behaviour_unchanged()
        {
            // emitter=null (the default) means the agent runs with SignalLog-only persistence.
            // Regression guard so the optional-param wiring never becomes required.
            using var rig = new Rig();
            var ing = rig.Build(emitter: null);
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());

            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 1));
            Assert.Equal(0, rig.Transport.EnqueueCount);

            ing.Stop();
        }

        [Fact]
        public void Transport_throw_is_swallowed_and_ingress_keeps_reducing()
        {
            using var rig = new Rig();
            var throwingTransport = new ThrowingTelemetryTransport();
            var emitter = new TelemetrySignalEmitter(throwingTransport, SessionId, TenantId);
            var ing = rig.Build(emitter);
            ing.Start();

            // Even with the transport failing on every Enqueue, the reducer must still see the
            // signal — local SignalLog is authoritative per §2.7c.
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());

            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 1));
            Assert.Equal(1, throwingTransport.EnqueueAttempts);

            ing.Stop();
        }

        private sealed class ThrowingTelemetryTransport : ITelemetryTransport
        {
            public int EnqueueAttempts;

            public long LastUploadedItemId => -1;

            public TelemetryItem Enqueue(TelemetryItemDraft draft)
            {
                Interlocked.Increment(ref EnqueueAttempts);
                throw new InvalidOperationException("spool overflow (test)");
            }

            public System.Threading.Tasks.Task<DrainResult> DrainAllAsync(CancellationToken cancellationToken = default)
                => System.Threading.Tasks.Task.FromResult(DrainResult.Empty());

            public void Dispose() { }
        }
    }
}
