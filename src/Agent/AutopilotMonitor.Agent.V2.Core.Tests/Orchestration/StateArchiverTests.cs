using System;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class StateArchiverTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static AgentLogger MakeLogger(TempDirectory tmp) =>
            new AgentLogger(tmp.Path, AgentLogLevel.Info);

        [Fact]
        public void Archive_moves_reducer_state_segments_but_preserves_event_sequence_counter()
        {
            // event-sequence.json must stay put: Part 2 reuses the same SessionId and the
            // backend orders events by (SessionId, Sequence). Resetting the counter at the
            // resume boundary would collide Part-1 and Part-2 sequence values and break the
            // Inspector + Web split-detection (`splitSequence = resumed.sequence - 1`).
            using var tmp = new TempDirectory();
            var stateDir = tmp.Path;
            File.WriteAllText(Path.Combine(stateDir, "snapshot.json"), "{\"snap\":1}");
            File.WriteAllText(Path.Combine(stateDir, "signal-log.jsonl"), "{\"sig\":1}\n");
            File.WriteAllText(Path.Combine(stateDir, "journal.jsonl"), "{\"jrn\":1}\n");
            File.WriteAllText(Path.Combine(stateDir, "event-sequence.json"), "{\"LastAssignedSequence\":42}");

            var bucket = StateArchiver.ArchiveStateFolder(
                stateDir, "test-archive", () => At, MakeLogger(tmp));

            Assert.NotNull(bucket);
            Assert.True(Directory.Exists(bucket!));
            Assert.True(File.Exists(Path.Combine(bucket!, "snapshot.json")));
            Assert.True(File.Exists(Path.Combine(bucket!, "signal-log.jsonl")));
            Assert.True(File.Exists(Path.Combine(bucket!, "journal.jsonl")));
            Assert.True(File.Exists(Path.Combine(bucket!, "reason.txt")));

            // Reducer-state originals moved out.
            Assert.False(File.Exists(Path.Combine(stateDir, "snapshot.json")));
            Assert.False(File.Exists(Path.Combine(stateDir, "signal-log.jsonl")));
            Assert.False(File.Exists(Path.Combine(stateDir, "journal.jsonl")));

            // Event-sequence counter stayed in place with its Part-1 contents.
            Assert.True(File.Exists(Path.Combine(stateDir, "event-sequence.json")));
            Assert.False(File.Exists(Path.Combine(bucket!, "event-sequence.json")));
            var preservedJson = File.ReadAllText(Path.Combine(stateDir, "event-sequence.json"));
            Assert.Contains("42", preservedJson);
        }

        [Fact]
        public void Archive_bucket_name_is_iso8601_timestamped()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "snapshot.json"), "{}");

            var bucket = StateArchiver.ArchiveStateFolder(
                tmp.Path, "ts-test", () => At, MakeLogger(tmp));

            Assert.NotNull(bucket);
            var bucketName = Path.GetFileName(bucket!);
            // Format: .part1-yyyyMMddTHHmmssfffZ
            Assert.StartsWith(".part1-2026", bucketName);
            Assert.EndsWith("Z", bucketName);
        }

        [Fact]
        public void Multiple_archives_produce_distinct_buckets()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "snapshot.json"), "{\"v\":1}");

            var nowIndex = 0;
            DateTime[] stamps =
            {
                At,
                At.AddSeconds(1),
            };

            var bucket1 = StateArchiver.ArchiveStateFolder(
                tmp.Path, "first", () => stamps[nowIndex++], MakeLogger(tmp));

            // Recreate a fresh snapshot.json and archive again at a later stamp.
            File.WriteAllText(Path.Combine(tmp.Path, "snapshot.json"), "{\"v\":2}");
            var bucket2 = StateArchiver.ArchiveStateFolder(
                tmp.Path, "second", () => stamps[nowIndex++], MakeLogger(tmp));

            Assert.NotNull(bucket1);
            Assert.NotNull(bucket2);
            Assert.NotEqual(bucket1, bucket2);
            Assert.True(Directory.Exists(bucket1!));
            Assert.True(Directory.Exists(bucket2!));
        }

        [Fact]
        public void Archive_with_no_segment_files_returns_null_and_creates_no_bucket()
        {
            using var tmp = new TempDirectory();
            // tmp.Path exists but has no segment files.

            var bucket = StateArchiver.ArchiveStateFolder(
                tmp.Path, "noop", () => At, MakeLogger(tmp));

            Assert.Null(bucket);
            Assert.Empty(Directory.GetDirectories(tmp.Path, ".part1-*"));
        }

        [Fact]
        public void Archive_with_missing_directory_returns_null()
        {
            using var tmp = new TempDirectory();
            var missing = Path.Combine(tmp.Path, "does-not-exist");
            Assert.False(Directory.Exists(missing));

            var bucket = StateArchiver.ArchiveStateFolder(
                missing, "missing", () => At, MakeLogger(tmp));

            Assert.Null(bucket);
        }

        [Fact]
        public void Archive_with_only_some_segment_files_moves_what_exists()
        {
            using var tmp = new TempDirectory();
            // Only snapshot.json + journal.jsonl present; signal-log missing.
            File.WriteAllText(Path.Combine(tmp.Path, "snapshot.json"), "{}");
            File.WriteAllText(Path.Combine(tmp.Path, "journal.jsonl"), "{}");

            var bucket = StateArchiver.ArchiveStateFolder(
                tmp.Path, "partial", () => At, MakeLogger(tmp));

            Assert.NotNull(bucket);
            Assert.True(File.Exists(Path.Combine(bucket!, "snapshot.json")));
            Assert.True(File.Exists(Path.Combine(bucket!, "journal.jsonl")));
            Assert.False(File.Exists(Path.Combine(bucket!, "signal-log.jsonl")));
            Assert.False(File.Exists(Path.Combine(bucket!, "event-sequence.json")));
            Assert.True(File.Exists(Path.Combine(bucket!, "reason.txt")));
        }

        [Fact]
        public void Archive_does_not_create_a_bucket_when_only_event_sequence_counter_is_present()
        {
            // event-sequence.json is no longer in KnownSegmentFiles, so a state directory
            // that only holds the counter (e.g. an aborted bootstrap that failed before the
            // first reducer write) must NOT be considered a Part-1 sealing to archive.
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "event-sequence.json"), "{\"LastAssignedSequence\":7}");

            var bucket = StateArchiver.ArchiveStateFolder(
                tmp.Path, "counter-only", () => At, MakeLogger(tmp));

            Assert.Null(bucket);
            Assert.Empty(Directory.GetDirectories(tmp.Path, ".part1-*"));
            Assert.True(File.Exists(Path.Combine(tmp.Path, "event-sequence.json")));
        }

        [Fact]
        public void Archive_writes_reason_text_into_bucket()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "snapshot.json"), "{}");

            var bucket = StateArchiver.ArchiveStateFolder(
                tmp.Path, "wg_part1_resume_archive", () => At, MakeLogger(tmp));

            Assert.NotNull(bucket);
            var reason = File.ReadAllText(Path.Combine(bucket!, "reason.txt"));
            Assert.Equal("wg_part1_resume_archive", reason);
        }

        [Fact]
        public void Archive_throws_on_null_state_directory()
        {
            using var tmp = new TempDirectory();
            Assert.Throws<ArgumentException>(() =>
                StateArchiver.ArchiveStateFolder(null!, "x", () => At, MakeLogger(tmp)));
        }

        [Fact]
        public void Archive_throws_on_null_clock()
        {
            using var tmp = new TempDirectory();
            Assert.Throws<ArgumentNullException>(() =>
                StateArchiver.ArchiveStateFolder(tmp.Path, "x", null!, MakeLogger(tmp)));
        }
    }
}
