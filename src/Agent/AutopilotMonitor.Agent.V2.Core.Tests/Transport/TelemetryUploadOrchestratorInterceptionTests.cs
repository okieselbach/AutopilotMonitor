#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// The transport-local pause after an intercepted upload (a 401/403 something on the network
    /// path answered): no HTTP while it holds, backoff 30 s → 15 min, a success ends it, the
    /// terminal phase gets exactly one attempt despite it, and it is never an auth failure.
    /// Driven by the virtual clock — the pause is a "not before", never a timer.
    /// </summary>
    public sealed class TelemetryUploadOrchestratorInterceptionTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            private int _rowKey;

            public TempDirectory Tmp { get; } = new TempDirectory();
            public VirtualClock Clock { get; } = new VirtualClock(Start);
            public FakeBackendTelemetryUploader Uploader { get; } = new FakeBackendTelemetryUploader();
            public TelemetryUploadOrchestrator Sut { get; }

            public Rig()
            {
                var spool = new TelemetrySpool(Tmp.Path, Clock);
                Sut = new TelemetryUploadOrchestrator(spool, Uploader, Clock, batchSize: 10, retryBackoffs: new[] { TimeSpan.Zero });
            }

            public void Enqueue(int count = 1)
            {
                for (int i = 0; i < count; i++)
                {
                    var rowKey = (++_rowKey).ToString("D10");
                    Sut.Enqueue(new TelemetryItemDraft(
                        kind: TelemetryItemKind.Event,
                        partitionKey: "T1_S1",
                        rowKey: rowKey,
                        payloadJson: "{}",
                        isSessionScoped: true,
                        requiresImmediateFlush: false));
                }
            }

            public void Dispose() { Sut.Dispose(); Tmp.Dispose(); }
        }

        [Fact]
        public async Task Intercepted_upload_retains_the_batch_and_later_drains_make_no_http_call()
        {
            using var rig = new Rig();
            rig.Enqueue(2);
            rig.Uploader.QueueIntercepted();

            var first = await rig.Sut.DrainAllAsync();

            Assert.Equal(1, rig.Uploader.CallCount);
            Assert.Equal(1, first.FailedBatches);
            Assert.Equal(0, first.UploadedItems);
            Assert.Contains("intercepted", first.LastErrorReason);
            Assert.Equal(-1, rig.Sut.LastUploadedItemId);
            Assert.True(rig.Sut.IsInterceptionPaused);
            Assert.False(rig.Sut.IsPaused);

            // Periodic drains and immediate-flush wakeups (a new item, another drain) during the
            // pause: no HTTP, and an expected state rather than another failed batch.
            rig.Enqueue();
            var paused = await rig.Sut.DrainAllAsync();
            rig.Clock.Advance(TimeSpan.FromSeconds(29));
            await rig.Sut.DrainAllAsync();

            Assert.Equal(1, rig.Uploader.CallCount);
            Assert.Equal(0, paused.FailedBatches);
            Assert.Contains("upload intercepted, next attempt not before", paused.LastErrorReason);
            Assert.Equal(-1, rig.Sut.LastUploadedItemId);
        }

        [Fact]
        public async Task Backoff_runs_30s_1m_2m_5m_10m_then_15m_capped()
        {
            var expected = new[] { 30, 60, 120, 300, 600, 900, 900 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
            Assert.Equal(expected.Take(6), TelemetryUploadOrchestrator.InterceptionBackoffs);

            using var rig = new Rig();
            rig.Enqueue();
            rig.Uploader.QueueIntercepted(expected.Length);

            foreach (var delay in expected)
            {
                var calls = rig.Uploader.CallCount;
                await rig.Sut.DrainAllAsync();
                Assert.Equal(calls + 1, rig.Uploader.CallCount);

                rig.Clock.Advance(delay - TimeSpan.FromSeconds(1));
                Assert.True(rig.Sut.IsInterceptionPaused);
                await rig.Sut.DrainAllAsync();
                Assert.Equal(calls + 1, rig.Uploader.CallCount);

                rig.Clock.Advance(TimeSpan.FromSeconds(1));
                Assert.False(rig.Sut.IsInterceptionPaused);
            }
        }

        /// <summary>A clock that can be set back, as NTP does to a device that booted with a future RTC time.</summary>
        private sealed class SettableClock : AutopilotMonitor.DecisionCore.Engine.IClock
        {
            public SettableClock(DateTime utcNow) { UtcNow = utcNow; }
            public DateTime UtcNow { get; set; }
            public Task Delay(TimeSpan delay, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        [Fact]
        public async Task Clock_set_back_past_the_pause_start_ends_the_pause_instead_of_stretching_it()
        {
            using var tmp = new TempDirectory();
            var clock = new SettableClock(Start);
            var uploader = new FakeBackendTelemetryUploader().QueueIntercepted(2);
            using var sut = new TelemetryUploadOrchestrator(new TelemetrySpool(tmp.Path, clock), uploader, clock, retryBackoffs: new[] { TimeSpan.Zero });
            sut.Enqueue(new TelemetryItemDraft(TelemetryItemKind.Event, "T1_S1", "0000000001", "{}", isSessionScoped: true));

            await sut.DrainAllAsync();
            clock.UtcNow = Start.AddHours(-2);

            Assert.False(sut.IsInterceptionPaused);
            await sut.DrainAllAsync();
            Assert.Equal(2, uploader.CallCount);
            Assert.True(sut.IsInterceptionPaused);   // re-armed from the corrected clock
        }

        [Fact]
        public async Task Successful_upload_ends_the_pause_and_resets_the_backoff()
        {
            using var rig = new Rig();
            rig.Enqueue(3);
            rig.Uploader.QueueIntercepted(2).QueueOk().QueueIntercepted();

            await rig.Sut.DrainAllAsync();                     // intercepted, 30 s
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            await rig.Sut.DrainAllAsync();                     // intercepted, 1 min
            rig.Clock.Advance(TimeSpan.FromMinutes(1));
            var cleared = await rig.Sut.DrainAllAsync();       // the path is clear

            Assert.Equal(3, cleared.UploadedItems);
            Assert.False(rig.Sut.IsInterceptionPaused);

            rig.Enqueue();
            await rig.Sut.DrainAllAsync();                     // intercepted again: back to 30 s
            rig.Clock.Advance(TimeSpan.FromSeconds(29));
            Assert.True(rig.Sut.IsInterceptionPaused);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(rig.Sut.IsInterceptionPaused);
        }

        [Fact]
        public async Task Terminal_drain_makes_exactly_one_attempt_despite_the_pause()
        {
            using var rig = new Rig();
            rig.Enqueue();
            rig.Uploader.QueueIntercepted(3);
            await rig.Sut.DrainAllAsync();
            Assert.Equal(1, rig.Uploader.CallCount);

            Assert.True(rig.Sut.GrantTerminalAttempt());
            var terminal = await rig.Sut.DrainAllAsync();

            Assert.Equal(2, rig.Uploader.CallCount);
            Assert.Equal(1, terminal.FailedBatches);
            Assert.True(rig.Sut.IsInterceptionPaused);

            // One attempt per process: a later terminal drain (orchestrator.Stop) honours the pause.
            rig.Sut.GrantTerminalAttempt();
            await rig.Sut.DrainAllAsync();
            Assert.Equal(2, rig.Uploader.CallCount);
        }

        [Fact]
        public async Task Terminal_attempt_that_gets_through_drains_everything()
        {
            using var rig = new Rig();
            rig.Enqueue(25);
            rig.Uploader.QueueIntercepted().QueueOk(3);
            await rig.Sut.DrainAllAsync();

            rig.Sut.GrantTerminalAttempt();
            var terminal = await rig.Sut.DrainAllAsync();

            Assert.Equal(25, terminal.UploadedItems);
            Assert.Equal(4, rig.Uploader.CallCount);
            Assert.Equal(24, rig.Sut.LastUploadedItemId);
            Assert.False(rig.Sut.IsInterceptionPaused);
        }

        [Fact]
        public async Task Grant_without_a_pause_changes_nothing_until_an_interception_meets_it()
        {
            using var rig = new Rig();
            Assert.False(rig.Sut.GrantTerminalAttempt());
            rig.Enqueue();
            rig.Uploader.QueueOk().QueueIntercepted(3);

            var normal = await rig.Sut.DrainAllAsync();
            Assert.Equal(1, normal.UploadedItems);

            rig.Enqueue();
            await rig.Sut.DrainAllAsync();                     // intercepted during the terminal phase
            await rig.Sut.DrainAllAsync();                     // the granted attempt
            await rig.Sut.DrainAllAsync();                     // pause honoured again

            Assert.Equal(3, rig.Uploader.CallCount);
        }

        [Fact]
        public async Task DeviceBlocked_pause_still_skips_terminal_drains()
        {
            using var rig = new Rig();
            rig.Enqueue();
            rig.Uploader.QueueOkWithSignals(deviceBlocked: true, unblockAt: Start.AddMinutes(10));
            await rig.Sut.DrainAllAsync();
            Assert.True(rig.Sut.IsPaused);

            rig.Enqueue();
            Assert.False(rig.Sut.GrantTerminalAttempt());
            var terminal = await rig.Sut.DrainAllAsync();

            Assert.Equal(1, rig.Uploader.CallCount);
            Assert.Contains("device blocked", terminal.LastErrorReason);
        }

        [Fact]
        public async Task First_interception_is_reported_once_and_never_as_a_blocked_or_auth_failure()
        {
            using var rig = new Rig();
            rig.Enqueue();
            rig.Uploader.QueueIntercepted(2);
            var reports = new List<InterceptionReport>();
            var blocked = 0;
            rig.Sut.UploadIntercepted += (_, r) => reports.Add(r);
            rig.Sut.UploadBlocked += (_, __) => blocked++;

            await rig.Sut.DrainAllAsync();
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            await rig.Sut.DrainAllAsync();

            Assert.Equal(2, rig.Uploader.CallCount);
            var report = Assert.Single(reports);
            Assert.Equal(Start, report.FirstBlockedAtUtc);
            Assert.Equal(ResponseClassification.ClientCertificateRequired, report.Interception.ForeignReason);
            Assert.Equal(403, report.Interception.StatusCode);
            Assert.Equal(InterceptionFixtures.InspectionIssuer, report.Interception.ServerCertIssuer);
            Assert.Equal(0, blocked);
        }

        [Fact]
        public async Task Permanent_block_report_keeps_its_own_once_per_process_gate()
        {
            using var rig = new Rig();
            rig.Enqueue();
            rig.Uploader.QueueIntercepted().QueuePermanent("http 400: contract").QueuePermanent("http 400: contract");
            int intercepted = 0, blocked = 0;
            rig.Sut.UploadIntercepted += (_, __) => intercepted++;
            rig.Sut.UploadBlocked += (_, __) => blocked++;

            await rig.Sut.DrainAllAsync();
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            await rig.Sut.DrainAllAsync();
            await rig.Sut.DrainAllAsync();

            Assert.Equal(3, rig.Uploader.CallCount);
            Assert.Equal(1, intercepted);
            Assert.Equal(1, blocked);
        }
    }
}
