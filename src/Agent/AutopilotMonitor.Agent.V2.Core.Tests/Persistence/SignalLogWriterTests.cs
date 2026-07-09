using System;
using System.IO;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Persistence
{
    public sealed class SignalLogWriterTests
    {
        [Fact]
        public void Append_then_ReadAll_roundtrips_all_fields()
        {
            using var tmp = new TempDirectory();
            var log = tmp.File("signal-log.jsonl");

            var writer = new SignalLogWriter(log);
            var s0 = TestSignals.Raw(0);
            var s1 = TestSignals.Raw(1, DecisionSignalKind.DesktopArrived);

            writer.Append(s0);
            writer.Append(s1);

            Assert.Equal(1, writer.LastOrdinal);

            var readBack = writer.ReadAll();
            Assert.Equal(2, readBack.Count);
            Assert.Equal(0, readBack[0].SessionSignalOrdinal);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, readBack[0].Kind);
            Assert.Equal(1, readBack[1].SessionSignalOrdinal);
            Assert.Equal(DecisionSignalKind.DesktopArrived, readBack[1].Kind);
        }

        [Fact]
        public void Append_is_flushed_to_disk_immediately_plan_L12()
        {
            // Plan §2.7 L.12 — every successful Append returns only after flush-to-disk.
            // We verify the file's raw bytes are present before Append returns by opening
            // the file for shared read while the writer still exists.
            using var tmp = new TempDirectory();
            var log = tmp.File("signal-log.jsonl");

            var writer = new SignalLogWriter(log);
            writer.Append(TestSignals.Raw(0));

            // Concurrent reader opening with FileShare.ReadWrite gets the bytes now.
            var bytesOnDisk = File.ReadAllBytes(log);
            Assert.True(bytesOnDisk.Length > 0);
            var text = Encoding.UTF8.GetString(bytesOnDisk);
            Assert.Contains("\"SessionSignalOrdinal\":0", text);
        }

        [Fact]
        public void Append_rejects_non_monotonic_ordinal()
        {
            using var tmp = new TempDirectory();
            var writer = new SignalLogWriter(tmp.File("log.jsonl"));

            writer.Append(TestSignals.Raw(5));

            Assert.Throws<InvalidOperationException>(() => writer.Append(TestSignals.Raw(5)));
            Assert.Throws<InvalidOperationException>(() => writer.Append(TestSignals.Raw(3)));
        }

        [Fact]
        public void New_writer_on_existing_log_recovers_LastOrdinal()
        {
            using var tmp = new TempDirectory();
            var log = tmp.File("signal-log.jsonl");

            var w1 = new SignalLogWriter(log);
            w1.Append(TestSignals.Raw(0));
            w1.Append(TestSignals.Raw(1));
            w1.Append(TestSignals.Raw(2));

            // Simulate agent restart — fresh writer on same file.
            var w2 = new SignalLogWriter(log);
            Assert.Equal(2, w2.LastOrdinal);

            // Continuation must maintain monotonicity.
            w2.Append(TestSignals.Raw(3));
            Assert.Equal(3, w2.LastOrdinal);
            Assert.Equal(4, w2.ReadAll().Count);
        }

        [Fact]
        public void ReadAll_stops_at_corrupt_tail_line_simulating_crash_mid_append()
        {
            // Plan §2.7 Sonderfall 3 — crash ohne Terminal-Flush; letzte Zeile kann halb-
            // geschrieben sein. Recovery rule: bis zur letzten parsbaren Zeile lesen.
            using var tmp = new TempDirectory();
            var log = tmp.File("signal-log.jsonl");

            var writer = new SignalLogWriter(log);
            writer.Append(TestSignals.Raw(0));
            writer.Append(TestSignals.Raw(1));

            // Append a half-written line manually — simulates a kill-9 mid-write.
            File.AppendAllText(log, "{\"SessionSignalOrdinal\":2,\"Kind\":\"Esp", Encoding.UTF8);

            var reread = new SignalLogWriter(log);
            Assert.Equal(1, reread.LastOrdinal);  // did not pick up the half-line
            Assert.Equal(2, reread.ReadAll().Count);
        }

        [Fact]
        public void ReadAll_skips_byte_identical_duplicate_line_crash_artifact()
        {
            // Field case (session b9b92d89, 2026-07-09): a process kill during the final
            // Append (self-update restart) double-flushed the same buffered line → the
            // identical JSONL line landed twice. That is a crash artifact, not tampering —
            // ReadAll must dedupe it so recovery replay sees a strictly monotonic stream.
            using var tmp = new TempDirectory();
            var log = tmp.File("signal-log.jsonl");

            var writer = new SignalLogWriter(log);
            writer.Append(TestSignals.Raw(0));
            writer.Append(TestSignals.Raw(1));

            // Duplicate the last line verbatim — simulates the double-flush.
            var lines = File.ReadAllLines(log, Encoding.UTF8);
            File.AppendAllText(log, lines[lines.Length - 1] + "\n", Encoding.UTF8);

            var reread = new SignalLogWriter(log);
            Assert.Equal(1, reread.LastOrdinal);

            var signals = reread.ReadAll();
            Assert.Equal(2, signals.Count);
            Assert.Equal(0, signals[0].SessionSignalOrdinal);
            Assert.Equal(1, signals[1].SessionSignalOrdinal);
        }

        [Fact]
        public void ReadAll_does_not_dedupe_non_identical_duplicate_ordinal()
        {
            // A duplicate ordinal carried by a DIFFERENT line (true corruption / tamper)
            // must NOT be silently dropped — it stays in the stream so the replay ordinal
            // check can reject it and trigger quarantine.
            using var tmp = new TempDirectory();
            var log = tmp.File("signal-log.jsonl");

            var writer = new SignalLogWriter(log);
            writer.Append(TestSignals.Raw(0));
            writer.Append(TestSignals.Raw(1));

            // Re-serialize ordinal 1 with a different kind → same ordinal, different bytes.
            var rogue = AutopilotMonitor.DecisionCore.Serialization.SignalSerializer.Serialize(
                TestSignals.Raw(1, DecisionSignalKind.DesktopArrived));
            File.AppendAllText(log, rogue + "\n", Encoding.UTF8);

            var reread = new SignalLogWriter(log);
            Assert.Equal(3, reread.ReadAll().Count);
        }

        [Fact]
        public void ReadAll_on_missing_file_returns_empty()
        {
            using var tmp = new TempDirectory();
            var writer = new SignalLogWriter(tmp.File("does-not-exist.jsonl"));
            Assert.Equal(-1, writer.LastOrdinal);
            Assert.Empty(writer.ReadAll());
        }

        [Fact]
        public void Constructor_creates_missing_parent_directory()
        {
            using var tmp = new TempDirectory();
            var nested = Path.Combine(tmp.Path, "State", "sub", "signal-log.jsonl");

            var writer = new SignalLogWriter(nested);
            writer.Append(TestSignals.Raw(0));

            Assert.True(File.Exists(nested));
        }

        // ============================================================ LastTraceOrdinal (recovery-seed for SessionTraceOrdinalProvider)

        [Fact]
        public void LastTraceOrdinal_empty_log_is_minus_one()
        {
            using var tmp = new TempDirectory();
            var writer = new SignalLogWriter(tmp.File("signal-log.jsonl"));

            Assert.Equal(-1, writer.LastTraceOrdinal);
        }

        [Fact]
        public void LastTraceOrdinal_tracks_max_across_appends_not_last_one()
        {
            // Trace ordinals may arrive out of step with SessionSignalOrdinal if multiple producers
            // compete for the provider (e.g. a classifier verdict stamped with trace=42 interleaved
            // with a raw signal at trace=40 — the raw keeps a lower signal-ordinal but the max trace
            // has already advanced).
            using var tmp = new TempDirectory();
            var writer = new SignalLogWriter(tmp.File("signal-log.jsonl"));

            writer.Append(TestSignals.Raw(ordinal: 0, traceOrdinal: 10));
            writer.Append(TestSignals.Raw(ordinal: 1, traceOrdinal: 42));
            writer.Append(TestSignals.Raw(ordinal: 2, traceOrdinal: 25));   // lower than prior max — must NOT lower max

            Assert.Equal(42, writer.LastTraceOrdinal);
        }

        [Fact]
        public void LastTraceOrdinal_survives_restart_by_rescanning_persisted_log()
        {
            // Core of the recovery seed: a fresh writer on an existing log must reconstruct the
            // max trace ordinal from disk — otherwise SessionTraceOrdinalProvider would start at
            // -1 and the first post-recovery signal would duplicate a previously-persisted trace.
            using var tmp = new TempDirectory();
            var log = tmp.File("signal-log.jsonl");

            var w1 = new SignalLogWriter(log);
            w1.Append(TestSignals.Raw(ordinal: 0, traceOrdinal: 7));
            w1.Append(TestSignals.Raw(ordinal: 1, traceOrdinal: 99));
            w1.Append(TestSignals.Raw(ordinal: 2, traceOrdinal: 50));

            var w2 = new SignalLogWriter(log);
            Assert.Equal(99, w2.LastTraceOrdinal);
        }
    }
}
