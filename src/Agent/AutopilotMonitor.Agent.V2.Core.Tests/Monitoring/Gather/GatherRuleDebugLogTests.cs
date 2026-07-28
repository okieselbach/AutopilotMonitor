using System;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// Writer unit tests for <see cref="GatherRuleDebugLog"/>: line format, lazy directory
    /// creation, size-cap rotation, and the null-writer no-op path on the context.
    /// </summary>
    public sealed class GatherRuleDebugLogTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;

        public GatherRuleDebugLogTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose() => _tmp.Dispose();

        private string LogPath => Path.Combine(_tmp.Path, "sub", "gather_rules_debug.log");

        [Fact]
        public void Write_produces_pipe_delimited_line_and_creates_directory_lazily()
        {
            var log = new GatherRuleDebugLog(LogPath, _logger);
            Assert.False(File.Exists(LogPath)); // lazy — nothing until the first write

            log.Write("RULE-1", GatherRuleDebugLog.StageScope, "skipped: out of scope");

            var lines = File.ReadAllLines(LogPath);
            Assert.Equal(2, lines.Length); // header + entry
            Assert.Contains("| - | config | -- gather rule debug log started --", lines[0]);
            var parts = lines[1].Split(new[] { " | " }, StringSplitOptions.None);
            Assert.Equal(4, parts.Length);
            Assert.Equal("RULE-1", parts[1]);
            Assert.Equal("scope", parts[2]);
            Assert.Equal("skipped: out of scope", parts[3]);
        }

        [Fact]
        public void Write_flattens_newlines_except_for_error_stage()
        {
            var log = new GatherRuleDebugLog(LogPath, _logger);
            log.Write("RULE-1", GatherRuleDebugLog.StageCollector, "line1\r\nline2");
            log.Write("RULE-1", GatherRuleDebugLog.StageError, "boom\r\n   at Frame()");

            var content = File.ReadAllText(LogPath);
            Assert.Contains("line1 line2", content);
            Assert.Contains("   at Frame()", content); // error stage keeps the stack multi-line
        }

        [Fact]
        public void Write_null_ruleId_renders_dash()
        {
            var log = new GatherRuleDebugLog(LogPath, _logger);
            log.Write(null, GatherRuleDebugLog.StageConfig, "no rules delivered");

            Assert.Contains(" | - | config | no rules delivered", File.ReadAllText(LogPath));
        }

        [Fact]
        public void Write_rotates_once_when_file_exceeds_cap()
        {
            var log = new GatherRuleDebugLog(LogPath, _logger, maxBytes: 512);
            for (int i = 0; i < 20; i++)
                log.Write("RULE-1", GatherRuleDebugLog.StageExec, new string('x', 100));

            var oldPath = Path.Combine(Path.GetDirectoryName(LogPath), "gather_rules_debug.old.log");
            Assert.True(File.Exists(oldPath), "expected one .old rotation generation");
            Assert.True(File.Exists(LogPath));
            Assert.Contains("-- rotated", File.ReadAllText(LogPath) + File.ReadAllText(oldPath));
            // Never more than one .old generation
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(LogPath), "*.old.log"));
        }

        [Fact]
        public void WriteStandalone_writes_single_config_line()
        {
            GatherRuleDebugLog.WriteStandalone(LogPath, "no gather rules delivered by backend — nothing to execute", _logger);

            var lines = File.ReadAllLines(LogPath);
            Assert.Contains(lines, l => l.Contains("no gather rules delivered by backend"));
        }

        [Fact]
        public void Context_DebugLog_is_noop_without_writer()
        {
            var context = new GatherRuleContext(
                _logger, "sess", "tenant", _ => { }, null, new LogFilePositionTracker());

            context.DebugLog("RULE-1", GatherRuleDebugLog.StageScope, "should not throw, should not write");

            Assert.False(File.Exists(LogPath));
        }
    }
}
