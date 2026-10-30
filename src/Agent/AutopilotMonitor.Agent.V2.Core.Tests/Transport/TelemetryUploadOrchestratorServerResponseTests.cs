#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// M4.6.ε — <see cref="TelemetryUploadOrchestrator.ServerResponseReceived"/> hook plus the
    /// internal <see cref="TelemetryUploadOrchestrator.IsPaused"/> DeviceBlocked pause logic.
    /// </summary>
    public sealed class TelemetryUploadOrchestratorServerResponseTests
    {
        private static TelemetryItemDraft Draft(long traceOrdinal = 1) =>
            new TelemetryItemDraft(
                kind: TelemetryItemKind.Event,
                partitionKey: "T1_S1",
                rowKey: traceOrdinal.ToString("D10"),
                payloadJson: "{}",
                isSessionScoped: true,
                requiresImmediateFlush: false);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public VirtualClock Clock { get; } = new VirtualClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            public TelemetrySpool Spool { get; }
            public FakeBackendTelemetryUploader Uploader { get; } = new FakeBackendTelemetryUploader();
            public TelemetryUploadOrchestrator Sut { get; }

            public Rig(AgentLogger? logger = null)
            {
                Spool = new TelemetrySpool(Tmp.Path, Clock);
                Sut = new TelemetryUploadOrchestrator(Spool, Uploader, Clock, batchSize: 10, retryBackoffs: new[] { TimeSpan.Zero }, logger: logger);
            }

            public void Dispose() { Sut.Dispose(); Tmp.Dispose(); }
        }

        [Fact]
        public async Task Plain_ok_does_not_raise_ServerResponseReceived()
        {
            using var rig = new Rig();
            rig.Sut.Enqueue(Draft());
            rig.Uploader.QueueOk();

            var fired = 0;
            rig.Sut.ServerResponseReceived += (_, _) => Interlocked.Increment(ref fired);

            await rig.Sut.DrainAllAsync();
            Assert.Equal(0, fired);
        }

        [Fact]
        public async Task Ok_with_kill_signal_raises_ServerResponseReceived_once()
        {
            using var rig = new Rig();
            rig.Sut.Enqueue(Draft());
            rig.Uploader.QueueOkWithSignals(deviceKillSignal: true);

            UploadResult? captured = null;
            var fired = 0;
            rig.Sut.ServerResponseReceived += (_, r) => { Interlocked.Increment(ref fired); captured = r; };

            await rig.Sut.DrainAllAsync();
            Assert.Equal(1, fired);
            Assert.NotNull(captured);
            Assert.True(captured!.DeviceKillSignal);
        }

        [Fact]
        public async Task Ok_with_admin_action_raises_event_with_field()
        {
            using var rig = new Rig();
            rig.Sut.Enqueue(Draft());
            rig.Uploader.QueueOkWithSignals(reason_AdminAction: "failed");

            string? capturedAction = null;
            rig.Sut.ServerResponseReceived += (_, r) => capturedAction = r.AdminAction;

            await rig.Sut.DrainAllAsync();
            Assert.Equal("failed", capturedAction);
        }

        [Fact]
        public async Task Ok_with_actions_list_raises_event_carrying_actions()
        {
            using var rig = new Rig();
            rig.Sut.Enqueue(Draft());
            var actions = new List<ServerAction>
            {
                new ServerAction { Type = ServerActionTypes.RotateConfig, Reason = "periodic" },
            };
            rig.Uploader.QueueOkWithSignals(actions: actions);

            IReadOnlyList<ServerAction>? captured = null;
            rig.Sut.ServerResponseReceived += (_, r) => captured = r.Actions;

            await rig.Sut.DrainAllAsync();
            Assert.NotNull(captured);
            Assert.Single(captured!);
            Assert.Equal(ServerActionTypes.RotateConfig, captured[0].Type);
        }

        [Fact]
        public async Task DeviceBlocked_pauses_drain_until_unblock_at()
        {
            using var rig = new Rig();
            rig.Sut.Enqueue(Draft(traceOrdinal: 1));
            rig.Uploader.QueueOkWithSignals(
                deviceBlocked: true,
                unblockAt: rig.Clock.UtcNow.AddMinutes(10));

            await rig.Sut.DrainAllAsync();
            Assert.True(rig.Sut.IsPaused);

            // New item after block arrives — drain must short-circuit without calling the uploader.
            // Pause is an expected state, not an upload failure: FailedBatches stays 0, but
            // LastErrorReason carries the "device blocked" hint for observability.
            rig.Sut.Enqueue(Draft(traceOrdinal: 2));
            var callsBefore = rig.Uploader.CallCount;
            var drain = await rig.Sut.DrainAllAsync();
            Assert.Equal(callsBefore, rig.Uploader.CallCount);
            Assert.Equal(0, drain.UploadedItems);
            Assert.Contains("device blocked", drain.LastErrorReason ?? string.Empty);

            // Advance clock past UnblockAt — pause lifts, next drain uploads the queued item.
            rig.Uploader.QueueOk();
            rig.Clock.Advance(TimeSpan.FromMinutes(11));
            Assert.False(rig.Sut.IsPaused);
            var drain2 = await rig.Sut.DrainAllAsync();
            Assert.Equal(1, drain2.UploadedItems);
        }

        [Fact]
        public async Task Handler_exception_does_not_break_drain_cursor()
        {
            using var logTmp = new TempDirectory();
            var logger = new AgentLogger(logTmp.Path, AgentLogLevel.Info);
            using var rig = new Rig(logger);
            rig.Sut.Enqueue(Draft());
            rig.Uploader.QueueOkWithSignals(deviceKillSignal: true);

            rig.Sut.ServerResponseReceived += (_, _) => throw new InvalidOperationException("handler bug");

            var drain = await rig.Sut.DrainAllAsync();
            // Upload still succeeded — the cursor moves, drain returns success.
            Assert.Equal(1, drain.UploadedItems);
            Assert.Equal(0, drain.FailedBatches);
            // The handler fault is no longer silent.
            Assert.Contains(
                "[WARN] TelemetryUploadOrchestrator: ServerResponseReceived handler threw: handler bug",
                System.IO.File.ReadAllText(System.IO.Path.Combine(logTmp.Path, "agent.log")));
        }

        /// <summary>
        /// Codex Finding 1 — regression guard. Before the fix, the drain loop raised
        /// <see cref="TelemetryUploadOrchestrator.ServerResponseReceived"/> <b>while</b>
        /// still holding its internal _drainGuard. A realistic termination callback posts
        /// a final event (e.g. <c>agent_shutting_down</c>) and then expects the orchestrator
        /// shutdown path to do one more <see cref="TelemetryUploadOrchestrator.DrainAllAsync"/>
        /// to actually flush that event — that re-entrant drain deadlocked on the guard
        /// and timed out, dropping the final event on the floor. The fix raises events
        /// AFTER the guard is released so a handler can legitimately run nested drain code.
        /// </summary>
        [Fact]
        public async Task Handler_may_invoke_reentrant_drain_without_deadlock()
        {
            using var rig = new Rig();
            rig.Sut.Enqueue(Draft(traceOrdinal: 1));
            rig.Uploader.QueueOkWithSignals(deviceKillSignal: true);

            var reentrantDrain = 0;
            rig.Sut.ServerResponseReceived += (_, _) =>
            {
                // Simulate the termination-handler side of onTerminateRequested: post one
                // more telemetry item (the "agent_shutting_down" analogue) and synchronously
                // flush it via another drain. Before the fix, this would deadlock on the
                // still-held _drainGuard and the 1s timeout below would trip.
                rig.Sut.Enqueue(Draft(traceOrdinal: 2));
                rig.Uploader.QueueOk();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var nested = rig.Sut.DrainAllAsync(cts.Token).GetAwaiter().GetResult();
                Interlocked.Exchange(ref reentrantDrain, nested.UploadedItems);
            };

            var outer = await rig.Sut.DrainAllAsync();

            Assert.Equal(1, outer.UploadedItems);
            Assert.Equal(1, reentrantDrain);  // the agent_shutting_down analogue was flushed
            Assert.Equal(2, rig.Uploader.CallCount);
        }
    }
}
