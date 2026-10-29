#nullable enable
using System;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Logging
{
    /// <summary>
    /// PR2: file-size cap rotation. Long-running verbose sessions used to grow the daily log
    /// into hundreds of MB; the logger now rolls over to a numeric suffix once a segment
    /// reaches the cap, keeping every byte but in manageable files.
    /// </summary>
    public sealed class AgentLoggerTests
    {
        private const string BaseName = "agent";

        [Fact]
        public void Log_writes_to_unsuffixed_base_file_when_dir_is_empty()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info, maxFileSizeBytes: 1024L * 1024);

            logger.Info("hello");

            var basePath = Path.Combine(tmp.Path, BaseName + ".log");
            Assert.True(File.Exists(basePath), $"expected base file at {basePath}");
            Assert.Contains("hello", File.ReadAllText(basePath));
        }

        [Fact]
        public void Log_rotates_when_active_segment_crosses_cap()
        {
            using var tmp = new TempDirectory();
            // 1 KB cap so rotation triggers after a few writes.
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info, maxFileSizeBytes: 1024);

            // Each line ~80 bytes after timestamp/severity envelope; 30 lines clears 1 KB
            // even at the smallest payload. The first lines fill the base file; once the
            // FileInfo.Length read sees >= 1024 the next call rotates to -002.
            for (int i = 0; i < 60; i++)
            {
                logger.Info($"line-{i:D4}-padded-with-some-extra-context-to-cross-1KB-quickly");
            }

            var files = Directory.GetFiles(tmp.Path, BaseName + "*.log").OrderBy(s => s).ToArray();
            Assert.True(files.Length >= 2, $"expected at least 2 segments, got: {string.Join(",", files.Select(Path.GetFileName))}");

            // The unsuffixed file is the first segment; rotated segments use -002 onward.
            var rotated = files.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).EndsWith("-002"));
            Assert.NotNull(rotated);
        }

        [Fact]
        public void Log_writes_rotation_marker_as_first_line_of_new_segment()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info, maxFileSizeBytes: 1024);

            for (int i = 0; i < 60; i++)
            {
                logger.Info($"pad-{i:D4}-content-content-content-content-content-content");
            }

            var rotatedPath = Path.Combine(tmp.Path, BaseName + "-002.log");
            Assert.True(File.Exists(rotatedPath), "expected agent-002.log to exist");
            var first = File.ReadAllLines(rotatedPath).FirstOrDefault();
            Assert.NotNull(first);
            Assert.Contains("AgentLogger: rotated", first);
        }

        [Fact]
        public void Log_continues_existing_rotation_on_restart_without_overwriting()
        {
            using var tmp = new TempDirectory();
            // Pre-seed: looks like an earlier process already rotated up to -003.
            File.WriteAllText(Path.Combine(tmp.Path, BaseName + ".log"), "older-base\n");
            File.WriteAllText(Path.Combine(tmp.Path, BaseName + "-002.log"), "older-002\n");
            File.WriteAllText(Path.Combine(tmp.Path, BaseName + "-003.log"), "older-003\n");

            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info, maxFileSizeBytes: 1024L * 1024);
            logger.Info("after-restart");

            // New entries must land in the highest existing segment (-003), not in a fresh
            // base file. Older content in _003 must be preserved.
            var resumed = File.ReadAllText(Path.Combine(tmp.Path, BaseName + "-003.log"));
            Assert.Contains("older-003", resumed);
            Assert.Contains("after-restart", resumed);

            // Older segments are left alone.
            Assert.Equal("older-base\n", File.ReadAllText(Path.Combine(tmp.Path, BaseName + ".log")));
            Assert.Equal("older-002\n", File.ReadAllText(Path.Combine(tmp.Path, BaseName + "-002.log")));
        }

        [Fact]
        public void Log_transliterates_typographic_punctuation_to_ascii()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info, maxFileSizeBytes: 1024L * 1024);

            // The log file is UTF-8 without BOM; cp1252-guessing viewers (Outlook web
            // preview) render em dashes as mojibake. Our own punctuation must come out
            // as plain ASCII, while genuine non-ASCII payload stays untouched.
            logger.Info("check failed \u2014 range 1\u20132 \u2192 done\u2026 \u2018a\u2019 \u201Cb\u201D nb\u00A0sp f\u00FCr M\u00FCller");

            var text = File.ReadAllText(Path.Combine(tmp.Path, BaseName + ".log"));
            Assert.Contains("check failed - range 1-2 -> done... 'a' \"b\" nb sp f\u00FCr M\u00FCller", text);
        }

        [Fact]
        public void Log_swallows_io_errors_silently()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info, maxFileSizeBytes: 1024L * 1024);

            // Hold an exclusive lock on the log file to force AppendAllText to throw.
            var lockedPath = Path.Combine(tmp.Path, BaseName + ".log");
            File.WriteAllText(lockedPath, ""); // ensure exists
            using var holder = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // Must NOT throw — logger swallows IO errors to keep the agent running.
            logger.Info("dropped");
            // No assertion needed beyond "did not throw"; the regression we guard against is
            // the original swallow-all behaviour, which the rotation refactor must preserve.
        }

        [Theory]
        [InlineData(AgentLogLevel.Info, "INFO")]
        [InlineData(AgentLogLevel.Debug, "INFO,DEBUG")]
        [InlineData(AgentLogLevel.Verbose, "INFO,DEBUG,VERBOSE")]
        [InlineData(AgentLogLevel.Trace, "INFO,DEBUG,VERBOSE,TRACE")]
        public void Each_level_adds_to_the_one_before_it(AgentLogLevel level, string expectedTags)
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, level);

            logger.Info("line");
            logger.Debug("line");
            logger.Verbose("line");
            logger.Trace("line");

            var tags = ReadLines(tmp.Path).Select(Tag);
            Assert.Equal(expectedTags, string.Join(",", tags));
        }

        [Fact]
        public void RepeatKey_sends_an_identical_repeat_to_trace_and_a_change_back_to_its_level()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Trace);

            logger.Info("rule A ran", repeatKey: "rule:A");
            logger.Info("rule A ran", repeatKey: "rule:A");
            logger.Info("rule A ran again with new data", repeatKey: "rule:A");
            logger.Info("rule A ran", repeatKey: "rule:A");

            Assert.Equal(new[] { "INFO", "TRACE", "INFO", "INFO" }, ReadLines(tmp.Path).Select(Tag).ToArray());
        }

        [Fact]
        public void RepeatKey_keeps_subjects_apart()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Trace);

            logger.Debug("scan failed: access denied", repeatKey: "scan:System");
            logger.Debug("scan failed: access denied", repeatKey: "scan:Application");
            logger.Debug("scan failed: access denied", repeatKey: "scan:System");

            Assert.Equal(new[] { "DEBUG", "DEBUG", "TRACE" }, ReadLines(tmp.Path).Select(Tag).ToArray());
        }

        [Fact]
        public void RepeatKey_does_not_remember_a_line_its_level_did_not_write()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            logger.Debug("waiting for the policy", repeatKey: "hello.poll");
            logger.SetLogLevel(AgentLogLevel.Debug);
            logger.Debug("waiting for the policy", repeatKey: "hello.poll");

            Assert.Equal(new[] { "DEBUG" }, ReadLines(tmp.Path).Select(Tag).ToArray());
        }

        private static string[] ReadLines(string dir) =>
            File.ReadAllLines(Path.Combine(dir, BaseName + ".log"));

        // "[2026-10-30 12:00:00.000] [INFO] message" -> "INFO"
        private static string Tag(string line)
        {
            var open = line.IndexOf("] [", StringComparison.Ordinal) + 3;
            return line.Substring(open, line.IndexOf(']', open) - open);
        }
    }
}
