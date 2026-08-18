using System;
using System.IO;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    public sealed class TelemetrySpoolTests
    {
        private static TelemetryItemDraft EventDraft(string rowKey = "row-1") =>
            new TelemetryItemDraft(
                kind: TelemetryItemKind.Event,
                partitionKey: "tenant_session",
                rowKey: rowKey,
                payloadJson: "{\"EventType\":\"agent_started\"}",
                isSessionScoped: true);

        private static TelemetryItemDraft SignalDraft(string rowKey = "0000000000") =>
            new TelemetryItemDraft(
                kind: TelemetryItemKind.Signal,
                partitionKey: "tenant_session",
                rowKey: rowKey,
                payloadJson: "{\"Kind\":\"SessionStarted\"}",
                isSessionScoped: true);

        private static VirtualClock Clock() => new VirtualClock(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc));

        [Fact]
        public void Enqueue_assigns_monotonic_TelemetryItemId_starting_at_zero()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());

            var a = spool.Enqueue(EventDraft("a"));
            var b = spool.Enqueue(SignalDraft("0000000001"));
            var c = spool.Enqueue(EventDraft("c"));

            Assert.Equal(0, a.TelemetryItemId);
            Assert.Equal(1, b.TelemetryItemId);
            Assert.Equal(2, c.TelemetryItemId);
            Assert.Equal(2, spool.LastAssignedItemId);
        }

        [Fact]
        public void Enqueue_session_scoped_sets_SessionTraceOrdinal_equal_to_TelemetryItemId()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());

            var item = spool.Enqueue(EventDraft());
            Assert.Equal(item.TelemetryItemId, item.SessionTraceOrdinal);
        }

        [Fact]
        public void Enqueue_non_session_scoped_has_null_SessionTraceOrdinal()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());

            var draft = new TelemetryItemDraft(
                kind: TelemetryItemKind.Event,
                partitionKey: "tenant_agent-global",
                rowKey: "rk",
                payloadJson: "{}",
                isSessionScoped: false);

            var item = spool.Enqueue(draft);
            Assert.Null(item.SessionTraceOrdinal);
        }

        [Fact]
        public void Enqueue_is_flushed_to_disk_immediately_plan_L12()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(EventDraft("row-flush"));

            var spoolFile = Path.Combine(tmp.Path, "spool.jsonl");
            var bytes = File.ReadAllBytes(spoolFile);
            Assert.True(bytes.Length > 0);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("row-flush", text);
        }

        [Fact]
        public void Peek_returns_pending_items_after_cursor_in_order()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 5; i++) spool.Enqueue(EventDraft($"r{i}"));

            var peek = spool.Peek(10);
            Assert.Equal(5, peek.Count);
            for (int i = 0; i < 5; i++) Assert.Equal(i, peek[i].TelemetryItemId);
        }

        [Fact]
        public void Peek_respects_max_argument()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 10; i++) spool.Enqueue(EventDraft($"r{i}"));

            var peek = spool.Peek(3);
            Assert.Equal(3, peek.Count);
            Assert.Equal(0, peek[0].TelemetryItemId);
            Assert.Equal(2, peek[2].TelemetryItemId);
        }

        [Fact]
        public void Peek_after_MarkUploaded_skips_already_uploaded_items()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 5; i++) spool.Enqueue(EventDraft($"r{i}"));

            spool.MarkUploaded(2);
            var remaining = spool.Peek(10);
            Assert.Equal(2, remaining.Count);
            Assert.Equal(3, remaining[0].TelemetryItemId);
            Assert.Equal(4, remaining[1].TelemetryItemId);
        }

        [Theory]
        // Cursor starts at high-water LastUploadedItemId=2; any value at or below must be a no-op
        // so upload retries cannot rewind the spool pointer and replay already-flushed items.
        [InlineData(0)]   // strict regression
        [InlineData(1)]   // one below high-water
        [InlineData(2)]   // same value (idempotent; NEW coverage)
        [InlineData(-1)]  // negative edge value (NEW coverage)
        [InlineData(-5)]  // deep negative
        public void MarkUploaded_at_or_below_last_uploaded_is_ignored(int regressionValue)
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            for (int i = 0; i < 3; i++) spool.Enqueue(EventDraft($"r{i}"));

            spool.MarkUploaded(2);
            spool.MarkUploaded(regressionValue);

            Assert.Equal(2, spool.LastUploadedItemId);
        }

        [Fact]
        public void MarkUploaded_beyond_last_assigned_throws()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(EventDraft("only"));
            Assert.Throws<InvalidOperationException>(() => spool.MarkUploaded(99));
        }

        [Fact]
        public void New_spool_on_existing_dir_recovers_counters()
        {
            // Simulates agent restart — §2.7 counter-recovery rules 1 + 3.
            using var tmp = new TempDirectory();

            var s1 = new TelemetrySpool(tmp.Path, Clock());
            s1.Enqueue(EventDraft("a"));
            s1.Enqueue(EventDraft("b"));
            s1.Enqueue(EventDraft("c"));
            s1.MarkUploaded(1);

            var s2 = new TelemetrySpool(tmp.Path, Clock());
            Assert.Equal(2, s2.LastAssignedItemId);
            Assert.Equal(1, s2.LastUploadedItemId);

            var pending = s2.Peek(10);
            Assert.Single(pending);
            Assert.Equal(2, pending[0].TelemetryItemId);

            var d = s2.Enqueue(EventDraft("d"));
            Assert.Equal(3, d.TelemetryItemId);
        }

        [Fact]
        public void Scan_skips_corrupt_tail_line_simulating_crash()
        {
            using var tmp = new TempDirectory();
            var s1 = new TelemetrySpool(tmp.Path, Clock());
            s1.Enqueue(EventDraft("a"));

            File.AppendAllText(
                Path.Combine(tmp.Path, "spool.jsonl"),
                "{\"Kind\":\"Event\",\"PartitionKey\":\"corrupt-no-",
                Encoding.UTF8);

            var s2 = new TelemetrySpool(tmp.Path, Clock());
            Assert.Equal(0, s2.LastAssignedItemId);
            var next = s2.Enqueue(EventDraft("next"));
            Assert.Equal(1, next.TelemetryItemId);
        }

        [Fact]
        public void Enqueue_with_RequiresImmediateFlush_raises_ImmediateFlushRequested()
        {
            // Regression guard for the "30 s gap at session start" first-session bug. Before
            // this wakeup, RequiresImmediateFlush=true items were persisted but the drain loop
            // still slept the full _drainInterval before picking them up. If this event ever
            // stops firing the delay comes back, and critical lifecycle items (agent_started,
            // enrollment_failed on auth-failure, …) will again wait up to 30 s before upload.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());

            var signalled = 0;
            spool.ImmediateFlushRequested += (s, e) => System.Threading.Interlocked.Increment(ref signalled);

            var immediate = new TelemetryItemDraft(
                kind: TelemetryItemKind.Event,
                partitionKey: "tenant_session",
                rowKey: "immediate",
                payloadJson: "{\"EventType\":\"agent_started\"}",
                isSessionScoped: true,
                requiresImmediateFlush: true);

            spool.Enqueue(immediate);

            Assert.Equal(1, System.Threading.Volatile.Read(ref signalled));
        }

        [Fact]
        public void PendingBytes_tracks_enqueue_and_shrinks_to_zero_when_all_uploaded()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            Assert.Equal(0, spool.PendingBytes);

            spool.Enqueue(EventDraft("a"));
            spool.Enqueue(EventDraft("b"));
            spool.Enqueue(EventDraft("c"));

            // PendingBytes must equal the full file size while nothing is uploaded yet
            // (every line written by Enqueue is still pending).
            Assert.Equal(spool.SpoolFileSizeBytes, spool.PendingBytes);

            spool.MarkUploaded(2);
            Assert.Equal(0, spool.PendingBytes);
            // File keeps its append-only size — the two measures MUST diverge here,
            // otherwise the pressure trigger regresses to the file-size false positive.
            Assert.True(spool.SpoolFileSizeBytes > 0);
        }

        [Fact]
        public void PendingBytes_after_partial_upload_equals_remaining_tail_line_bytes()
        {
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());
            spool.Enqueue(EventDraft("a"));
            spool.Enqueue(EventDraft("b"));
            spool.Enqueue(EventDraft("c-longer-rowkey-for-distinct-length"));

            spool.MarkUploaded(1);

            // Expected = on-disk byte length of the last line incl. its trailing \n.
            var lines = File.ReadAllLines(Path.Combine(tmp.Path, "spool.jsonl"), Encoding.UTF8);
            var expected = Encoding.UTF8.GetByteCount(lines[2]) + 1;
            Assert.Equal(expected, spool.PendingBytes);
        }

        [Fact]
        public void PendingBytes_is_rehydrated_on_restart_from_pending_tail_only()
        {
            using var tmp = new TempDirectory();
            var s1 = new TelemetrySpool(tmp.Path, Clock());
            s1.Enqueue(EventDraft("a"));
            s1.Enqueue(EventDraft("b"));
            s1.Enqueue(EventDraft("c"));
            s1.MarkUploaded(1);
            var expectedPendingBytes = s1.PendingBytes;
            Assert.True(expectedPendingBytes > 0);

            // Restart: rehydrated PendingBytes must match the pre-restart value exactly
            // (uploaded prefix excluded), not the full file size.
            var s2 = new TelemetrySpool(tmp.Path, Clock());
            Assert.Equal(expectedPendingBytes, s2.PendingBytes);
            Assert.True(s2.PendingBytes < s2.SpoolFileSizeBytes);
        }

        [Fact]
        public void PendingBytes_rehydrate_ignores_corrupt_tail_line()
        {
            using var tmp = new TempDirectory();
            var s1 = new TelemetrySpool(tmp.Path, Clock());
            s1.Enqueue(EventDraft("a"));
            var cleanPendingBytes = s1.PendingBytes;

            File.AppendAllText(
                Path.Combine(tmp.Path, "spool.jsonl"),
                "{\"Kind\":\"Event\",\"PartitionKey\":\"corrupt-no-",
                Encoding.UTF8);

            var s2 = new TelemetrySpool(tmp.Path, Clock());
            Assert.Equal(cleanPendingBytes, s2.PendingBytes);
        }

        [Fact]
        public void Enqueue_without_RequiresImmediateFlush_does_not_raise_ImmediateFlushRequested()
        {
            // Batched items (performance_snapshot, routine signal/transition JSONL) must not
            // wake the drain loop — otherwise the wakeup degenerates into a tight loop and the
            // periodic-drain cadence loses its purpose.
            using var tmp = new TempDirectory();
            var spool = new TelemetrySpool(tmp.Path, Clock());

            var signalled = 0;
            spool.ImmediateFlushRequested += (s, e) => System.Threading.Interlocked.Increment(ref signalled);

            spool.Enqueue(EventDraft("batched"));      // requiresImmediateFlush defaults to false
            spool.Enqueue(SignalDraft("0000000001"));

            Assert.Equal(0, System.Threading.Volatile.Read(ref signalled));
        }
    }
}
