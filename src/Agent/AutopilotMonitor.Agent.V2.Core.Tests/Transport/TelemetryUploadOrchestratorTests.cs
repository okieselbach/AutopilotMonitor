using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using Xunit;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    public sealed class TelemetryUploadOrchestratorTests
    {
        private static TelemetryItemDraft Draft(string rowKey) =>
            new TelemetryItemDraft(
                kind: TelemetryItemKind.Event,
                partitionKey: "tenant_session",
                rowKey: rowKey,
                payloadJson: $"{{\"rk\":\"{rowKey}\"}}",
                isSessionScoped: true);

        private static VirtualClock Clock() => new VirtualClock(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc));

        [Fact]
        public async Task Drain_happy_path_uploads_all_pending_and_advances_cursor()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 3; i++) spool.Enqueue(Draft($"r{i}"));

            var uploader = new FakeBackendTelemetryUploader().QueueOk(1);
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock(), batchSize: 100);

            var result = await sut.DrainAllAsync();

            Assert.Equal(3, result.UploadedItems);
            Assert.Equal(0, result.FailedBatches);
            Assert.True(result.Success);
            Assert.Equal(2, sut.LastUploadedItemId);
            Assert.Single(uploader.Received);
        }

        [Fact]
        public async Task Drain_empty_spool_is_noop()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            var uploader = new FakeBackendTelemetryUploader();
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());

            var result = await sut.DrainAllAsync();

            Assert.Equal(0, result.UploadedItems);
            Assert.Equal(0, result.FailedBatches);
            Assert.Equal(0, uploader.CallCount);
        }

        [Fact]
        public async Task Drain_batches_according_to_batchSize()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 10; i++) spool.Enqueue(Draft($"r{i}"));

            var uploader = new FakeBackendTelemetryUploader().QueueOk(4);
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock(), batchSize: 3);

            var result = await sut.DrainAllAsync();

            // 10 items / batch 3 = 4 batches (3 + 3 + 3 + 1)
            Assert.Equal(10, result.UploadedItems);
            Assert.Equal(4, uploader.CallCount);
            Assert.Equal(9, sut.LastUploadedItemId);

            // Verify batch sizes and order
            var received = uploader.Received;
            Assert.Equal(3, received[0].Count);
            Assert.Equal(3, received[1].Count);
            Assert.Equal(3, received[2].Count);
            Assert.Single(received[3]);
            Assert.Equal(0, received[0][0].TelemetryItemId);
            Assert.Equal(9, received[3][0].TelemetryItemId);
        }

        [Fact]
        public async Task Retry_transient_failures_up_to_3_attempts_then_succeed()
        {
            // Plan L.17 — Transient-Retry 100/400/1600ms. VirtualClock.Delay instant.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("only"));

            var uploader = new FakeBackendTelemetryUploader()
                .QueueTransient("first")
                .QueueTransient("second")
                .QueueOk();

            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            var result = await sut.DrainAllAsync();

            Assert.True(result.Success);
            Assert.Equal(1, result.UploadedItems);
            Assert.Equal(3, uploader.CallCount);
            Assert.Equal(0, sut.LastUploadedItemId);
        }

        [Fact]
        public async Task Retry_gives_up_after_all_transient_backoffs_exhausted()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("only"));

            var uploader = new FakeBackendTelemetryUploader()
                .QueueTransient()
                .QueueTransient()
                .QueueTransient()
                .QueueTransient("final-transient");

            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            var result = await sut.DrainAllAsync();

            Assert.False(result.Success);
            Assert.Equal(0, result.UploadedItems);
            Assert.Equal(1, result.FailedBatches);
            Assert.Equal(4, uploader.CallCount);   // 1 initial + 3 retries
            Assert.Contains("final-transient", result.LastErrorReason);
            Assert.Equal(-1, sut.LastUploadedItemId);  // cursor not advanced
        }

        [Fact]
        public async Task Unknown_permanent_failure_RETAINS_batch_and_reports_blocked_once()
        {
            // P1: an unknown permanent 4xx (400 contract bug / 404 route / tenant-validation) must
            // NOT discard data on a guess. The cursor stays put (batch retained), and a one-shot
            // UploadBlocked event surfaces the stuck pipeline. No poison skip.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("only"));

            var uploader = new FakeBackendTelemetryUploader().QueuePermanent("http 400: contract").QueuePermanent("http 400: contract");
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            PoisonReport? poison = null;
            int blockedCount = 0;
            sut.BatchPoisoned += (_, p) => poison = p;
            sut.UploadBlocked += (_, __) => blockedCount++;

            var first = await sut.DrainAllAsync();
            Assert.False(first.Success);
            Assert.Equal(1, uploader.CallCount);       // no retries for permanent
            Assert.Equal(-1, sut.LastUploadedItemId);  // RETAINED — cursor NOT advanced
            Assert.Null(poison);                        // never discarded
            Assert.Equal(1, blockedCount);              // surfaced once

            // Second drain still blocked, but the blocked event is one-shot per process.
            var second = await sut.DrainAllAsync();
            Assert.Equal(-1, sut.LastUploadedItemId);
            Assert.Equal(1, blockedCount);
        }

        [Fact]
        public async Task TooLarge_splits_batch_and_uploads_everything()
        {
            // P1: 413 is a size problem, not poison. Split & retry — never discard a multi-item batch.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 4; i++) spool.Enqueue(Draft($"r{i}"));

            // First attempt (batch of 4) → 413; orchestrator halves to 2 and the two halves upload.
            var uploader = new FakeBackendTelemetryUploader().QueueTooLarge().QueueOk().QueueOk();
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock(), batchSize: 4);

            var result = await sut.DrainAllAsync();

            Assert.True(result.Success);
            Assert.Equal(4, result.UploadedItems);     // nothing lost
            Assert.Equal(3, sut.LastUploadedItemId);
            Assert.Equal(3, uploader.CallCount);       // 1 oversized + 2 half-batches
        }

        [Fact]
        public async Task TooLarge_single_item_is_quarantined_as_oversize()
        {
            // The ONE locally-provable poison: a lone item the backend rejects with 413 even on its
            // own can never be sent — quarantine just it (advance by one) and surface it.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("huge"));

            var uploader = new FakeBackendTelemetryUploader().QueueTooLarge("http 413");
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            PoisonReport? poison = null;
            sut.BatchPoisoned += (_, p) => poison = p;

            var result = await sut.DrainAllAsync();

            Assert.Equal(0, sut.LastUploadedItemId);   // advanced past the single oversized item
            Assert.NotNull(poison);
            Assert.Equal(1, poison!.ItemCount);
            Assert.Contains("oversize", poison.Reason);
            Assert.Equal(PoisonKind.Oversize, poison.Kind); // P3
        }

        [Fact]
        public async Task Explicit_item_poison_drops_named_items_uploads_survivors_and_advances()
        {
            // P1: an EXPLICIT item-level poison signal is the only authorized discard. The named
            // RowKey is dropped; the rest of the batch is re-uploaded; the cursor advances past all.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("r0"));
            spool.Enqueue(Draft("r1"));
            spool.Enqueue(Draft("r2"));

            // First call (batch r0,r1,r2) → poison r1; survivors (r0,r2) re-upload → OK.
            var uploader = new FakeBackendTelemetryUploader()
                .QueuePoison(new[] { "r1" }, "schema_validation_failed")
                .QueueOk();
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock(), batchSize: 100);
            PoisonReport? poison = null;
            sut.BatchPoisoned += (_, p) => poison = p;

            var result = await sut.DrainAllAsync();

            Assert.Equal(2, result.UploadedItems);     // r0 + r2 survived
            Assert.Equal(2, sut.LastUploadedItemId);    // cursor advanced past the whole batch
            Assert.Equal(2, uploader.CallCount);        // poison call + survivor re-upload
            Assert.NotNull(poison);
            Assert.Equal(1, poison!.ItemCount);         // exactly one item dropped (r1)
            Assert.Equal(PoisonKind.BackendRejected, poison.Kind); // P3 — not mislabeled "oversize"
        }

        [Fact]
        public async Task Poison_survivor_reupload_honours_backend_control_signals()
        {
            // P1 fix: a DeviceBlocked (or kill/admin/actions) response on the SURVIVOR re-upload must
            // be honoured exactly like the normal success path — previously it was silently dropped.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("r0"));
            spool.Enqueue(Draft("r1"));

            // Poison r1; the survivor (r0) re-upload comes back with a DeviceBlocked quarantine.
            var uploader = new FakeBackendTelemetryUploader()
                .QueuePoison(new[] { "r1" }, "schema")
                .QueueOkWithSignals(deviceBlocked: true);
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock(), batchSize: 100);
            UploadResult? signalled = null;
            sut.ServerResponseReceived += (_, r) => signalled = r;

            await sut.DrainAllAsync();

            Assert.True(sut.IsPaused);                  // DeviceBlocked from the survivor upload took effect
            Assert.NotNull(signalled);                  // and was surfaced via ServerResponseReceived
            Assert.True(signalled!.DeviceBlocked);
        }

        [Fact]
        public async Task Poison_rowkeys_not_in_batch_retains_everything()
        {
            // A mismatched / stale poison list must not cause data loss — nothing named is in the
            // batch, so we retain (cursor stays) rather than discard or advance.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("r0"));
            spool.Enqueue(Draft("r1"));

            var uploader = new FakeBackendTelemetryUploader().QueuePoison(new[] { "not-in-batch" }, "stale");
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock(), batchSize: 100);
            PoisonReport? poison = null;
            sut.BatchPoisoned += (_, p) => poison = p;

            var result = await sut.DrainAllAsync();

            Assert.False(result.Success);
            Assert.Equal(-1, sut.LastUploadedItemId);   // RETAINED — nothing advanced or dropped
            Assert.Null(poison);
            Assert.Equal(1, uploader.CallCount);        // no survivor re-upload (whole batch retained)
        }

        [Fact]
        public async Task Auth_permanent_failure_retains_cursor_and_is_not_blocked_or_poisoned()
        {
            // 401/403: cursor stays (retained), no poison, and NOT counted as a "blocked" pipeline —
            // the uploader already drove AuthFailureTracker → shutdown, which owns the outcome.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("only"));

            var uploader = new FakeBackendTelemetryUploader().QueueUnauthorized("unauthorized: http 401");
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            PoisonReport? poison = null;
            int blockedCount = 0;
            sut.BatchPoisoned += (_, p) => poison = p;
            sut.UploadBlocked += (_, __) => blockedCount++;

            var result = await sut.DrainAllAsync();

            Assert.False(result.Success);
            Assert.Equal(1, uploader.CallCount);       // no retries
            Assert.Equal(-1, sut.LastUploadedItemId);  // cursor NOT advanced
            Assert.Null(poison);
            Assert.Equal(0, blockedCount);             // auth path owns shutdown, not a "block"
        }

        [Fact]
        public async Task Exception_in_uploader_is_treated_as_transient_and_retried()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("only"));

            var uploader = new FakeBackendTelemetryUploader()
                .QueueThrow(new InvalidOperationException("boom"))
                .QueueOk();

            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            var result = await sut.DrainAllAsync();

            Assert.True(result.Success);
            Assert.Equal(2, uploader.CallCount);
        }

        [Fact]
        public async Task Drain_resumes_on_second_call_after_transient_failure()
        {
            // Plan §2.7a resume-fähig. Failed batch stays in spool; next drain picks it up again.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 3; i++) spool.Enqueue(Draft($"r{i}"));

            // First drain: transient exhaust.
            var uploader = new FakeBackendTelemetryUploader()
                .QueueTransient().QueueTransient().QueueTransient().QueueTransient();
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            var first = await sut.DrainAllAsync();
            Assert.False(first.Success);
            Assert.Equal(-1, sut.LastUploadedItemId);

            // Second drain: uploader is healthy now — must re-upload same items.
            uploader.QueueOk();
            var second = await sut.DrainAllAsync();
            Assert.True(second.Success);
            Assert.Equal(3, second.UploadedItems);
            Assert.Equal(2, sut.LastUploadedItemId);
        }

        [Fact]
        public async Task Drain_after_restart_continues_from_persisted_cursor()
        {
            // Plan §2.7a — LastUploadedItemId survives process restart.
            using var tmp = new TempDirectory();

            // First run: enqueue 5 items; drain with batchSize=3 + 1 OK; uploader exhausts
            // mid-drain, cursor advances to 2, items 3-4 stay pending.
            var spool1 = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 5; i++) spool1.Enqueue(Draft($"r{i}"));
            var uploader1 = new FakeBackendTelemetryUploader().QueueOk(1);
            using (var sut1 = new TelemetryUploadOrchestrator(spool1, uploader1, Clock(), batchSize: 3))
            {
                await sut1.DrainAllAsync();
                Assert.Equal(2, sut1.LastUploadedItemId);
            }

            // Restart: fresh spool + orchestrator on same dir recovers cursor=2.
            var spool2 = new TelemetrySpool(tmp.Path, Clock());
            Assert.Equal(4, spool2.LastAssignedItemId);
            Assert.Equal(2, spool2.LastUploadedItemId);

            spool2.Enqueue(Draft("r5"));

            var uploader2 = new FakeBackendTelemetryUploader().QueueOk(10);
            using var sut2 = new TelemetryUploadOrchestrator(spool2, uploader2, Clock(), batchSize: 10);

            var result = await sut2.DrainAllAsync();

            Assert.Equal(3, result.UploadedItems);    // items 3, 4, 5
            Assert.Equal(5, sut2.LastUploadedItemId);
        }

        [Fact]
        public async Task Cursor_persists_across_orchestrator_instances()
        {
            using var tmp = new TempDirectory();

            var spool1 = new TelemetrySpool(tmp.Path, Clock());
            spool1.Enqueue(Draft("a"));
            spool1.Enqueue(Draft("b"));
            var uploader = new FakeBackendTelemetryUploader().QueueOk();
            using (var sut1 = new TelemetryUploadOrchestrator(spool1, uploader, Clock()))
            {
                await sut1.DrainAllAsync();
            }

            var spool2 = new TelemetrySpool(tmp.Path, Clock());
            Assert.Equal(1, spool2.LastUploadedItemId);
            Assert.Empty(spool2.Peek(10));
        }

        [Fact]
        public async Task Double_drain_is_idempotent_no_duplicate_uploads()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("a"));

            var uploader = new FakeBackendTelemetryUploader().QueueOk(2);
            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());

            var first = await sut.DrainAllAsync();
            var second = await sut.DrainAllAsync();

            Assert.Equal(1, first.UploadedItems);
            Assert.Equal(0, second.UploadedItems);
            Assert.Equal(1, uploader.CallCount);   // second drain saw empty spool
        }

        [Fact]
        public async Task Cancellation_stops_drain_loop()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(Draft("a"));

            var uploader = new FakeBackendTelemetryUploader();
            // Never enqueue OK — uploader will return 'no-script' transient repeatedly.

            using var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            using var cts = new CancellationTokenSource();
            cts.Cancel();   // Pre-cancelled

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => sut.DrainAllAsync(cts.Token));
        }

        [Fact]
        public void Enqueue_after_dispose_throws()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            var uploader = new FakeBackendTelemetryUploader();
            var sut = new TelemetryUploadOrchestrator(spool, uploader, Clock());
            sut.Dispose();

            Assert.Throws<ObjectDisposedException>(() => sut.Enqueue(Draft("x")));
        }
    }
}
