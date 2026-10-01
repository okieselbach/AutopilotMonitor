using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// Reading semantics of <see cref="LogParserCollector"/> across runs on one context: every
    /// line of a growing log is matched exactly once — no line lost at a maxLines cut, no line
    /// split by a writer that is mid-line, UTF-16 logs readable after the first run, positions
    /// kept per rule, line numbers counted from the start of the file.
    /// </summary>
    public sealed class LogParserCollectorReadingTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly GatherRuleContext _context;

        // %TEMP% lives under C:\Users (hard-blocked) — parse targets sit beside the
        // test assembly instead, admitted via UnrestrictedMode.
        private readonly string _logDir = Path.Combine(
            AppContext.BaseDirectory, "gather-reading-tests-" + Guid.NewGuid().ToString("N"));

        public LogParserCollectorReadingTests()
        {
            Directory.CreateDirectory(_logDir);
            _context = NewContext();
        }

        public void Dispose()
        {
            _tmp.Dispose();
            try { Directory.Delete(_logDir, recursive: true); } catch { /* best effort */ }
        }

        private GatherRuleContext NewContext()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            return new GatherRuleContext(logger, "sess", "tenant",
                evt => _events.Add(evt), null, new GatherRuleSessionState("sess", null, logger))
            {
                UnrestrictedMode = true
            };
        }

        private string LogPath(string name) => Path.Combine(_logDir, name);

        private static GatherRule TextRule(string ruleId, string target, string pattern,
            string trigger = "interval", int? maxLines = null)
        {
            var parameters = new Dictionary<string, string> { ["pattern"] = pattern, ["format"] = "text" };
            if (maxLines.HasValue) parameters["maxLines"] = maxLines.Value.ToString();
            return new GatherRule
            {
                RuleId = ruleId,
                Title = ruleId,
                CollectorType = "logparser",
                Target = target,
                Parameters = parameters,
                Trigger = trigger,
                IntervalSeconds = trigger == "interval" ? 30 : (int?)null,
                OutputEventType = "logparser_reading_test",
            };
        }

        private List<string> Captured(string group) =>
            _events.Select(e => e.Data.TryGetValue(group, out var v) ? (string)v : "<missing>").ToList();

        [Fact]
        public void Two_rules_on_one_file_each_read_the_whole_file()
        {
            var path = LogPath("shared.log");
            File.WriteAllText(path, "ERROR one\nWARN two\nERROR three\n");
            var collector = new LogParserCollector();

            collector.Execute(TextRule("RULE-ERRORS", path, "^ERROR (?<what>\\w+)"), _context);
            collector.Execute(TextRule("RULE-WARNINGS", path, "^WARN (?<what>\\w+)"), _context);

            Assert.Equal(2, _events.Count(e => (string)e.Data["ruleId"] == "RULE-ERRORS"));
            Assert.Single(_events, e => (string)e.Data["ruleId"] == "RULE-WARNINGS");
        }

        [Fact]
        public void MaxLines_cut_loses_no_line_across_runs()
        {
            var path = LogPath("long.log");
            var sb = new StringBuilder();
            for (var i = 1; i <= 30; i++)
                sb.Append("hit ").Append(i.ToString("D3")).Append(' ').Append('x', 90).Append('\n');
            File.WriteAllText(path, sb.ToString());
            var rule = TextRule("RULE-LONG", path, "^hit (?<n>\\d{3})", maxLines: 10);
            var collector = new LogParserCollector();

            for (var run = 0; run < 6; run++)
                collector.Execute(rule, _context);

            var expected = Enumerable.Range(1, 30).Select(i => i.ToString("D3")).ToList();
            Assert.Equal(expected, Captured("n"));
        }

        [Fact]
        public void Line_written_in_two_parts_is_matched_once_and_whole()
        {
            var path = LogPath("partial.log");
            File.WriteAllText(path, "ERR");
            var rule = TextRule("RULE-PARTIAL", path, "^ERROR (?<code>0x[0-9A-F]+)$");
            var collector = new LogParserCollector();

            collector.Execute(rule, _context);          // the writer is mid-line
            File.AppendAllText(path, "OR 0x1F\n");
            collector.Execute(rule, _context);

            Assert.Equal(new List<string> { "0x1F" }, Captured("code"));
        }

        [Fact]
        public void Interval_rule_reads_a_line_still_unfinished_on_its_second_run_once()
        {
            var path = LogPath("never-terminated.log");
            File.WriteAllText(path, "ERROR 0x2A");
            var rule = TextRule("RULE-UNFINISHED", path, "^ERROR (?<code>0x[0-9A-F]+)$");
            var collector = new LogParserCollector();

            collector.Execute(rule, _context);   // held: the writer may still be in the line
            collector.Execute(rule, _context);   // still unfinished — taken as it is
            collector.Execute(rule, _context);   // nothing new

            Assert.Equal(new List<string> { "0x2A" }, Captured("code"));
        }

        [Fact]
        public void One_shot_rule_reads_an_unfinished_line_now_and_whole_on_its_next_run()
        {
            var path = LogPath("startup.log");
            File.WriteAllText(path, "ok\nERR");
            var rule = TextRule("RULE-STARTUP", path, "^ERR(?<rest>.*)$", trigger: "startup");
            var collector = new LogParserCollector();

            collector.Execute(rule, _context);   // agent start: the writer is mid-line
            File.AppendAllText(path, "OR 0x1F\n");
            collector.Execute(rule, _context);   // next agent start

            // Read twice, never split: the second read sees the whole line.
            Assert.Equal(new List<string> { "", "OR 0x1F" }, Captured("rest"));
            Assert.Equal(new long[] { 2, 2 }, _events.Select(e => Convert.ToInt64(e.Data["logLineNumber"])).ToArray());
        }

        [Fact]
        public void Line_feed_written_after_a_trailing_carriage_return_is_consumed_without_a_line()
        {
            var path = LogPath("cr-at-end.log");
            File.WriteAllText(path, "hit\r");
            var rule = TextRule("RULE-CR", path, "^hit$");
            var collector = new LogParserCollector();

            collector.Execute(rule, _context);
            File.AppendAllText(path, "\n");
            collector.Execute(rule, _context);

            Assert.Single(_events);
            var position = _context.SessionState.PeekLogPosition("RULE-CR", path);
            Assert.Equal(new FileInfo(path).Length, position!.Position);
            Assert.Equal(1, position.LineNumber);
        }

        [Fact]
        public void Utf16_log_appended_after_the_first_run_is_still_matched()
        {
            var path = LogPath("transcript.log");
            File.WriteAllText(path, "ERROR first\r\n", Encoding.Unicode);   // UTF-16 LE with BOM
            var rule = TextRule("RULE-UTF16", path, "^ERROR (?<what>\\w+)$");
            var collector = new LogParserCollector();

            collector.Execute(rule, _context);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
            {
                var bytes = new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes("ERROR second\r\n");
                stream.Write(bytes, 0, bytes.Length);
            }
            collector.Execute(rule, _context);

            Assert.Equal(new List<string> { "first", "second" }, Captured("what"));
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("-5")]
        public void MaxLines_that_is_no_positive_number_falls_back_to_the_default(string maxLines)
        {
            var path = LogPath("misconfigured.log");
            File.WriteAllText(path, "hit 1\nhit 2\n");
            var rule = TextRule("RULE-MISCONFIGURED", path, "^hit (?<n>\\d)$");
            rule.Parameters["maxLines"] = maxLines;

            new LogParserCollector().Execute(rule, _context);

            Assert.Equal(new List<string> { "1", "2" }, Captured("n"));
        }

        [Fact]
        public void Line_numbers_count_from_the_start_of_the_file()
        {
            var path = LogPath("numbers.log");
            File.WriteAllText(path, "a\nhit\nb\n");
            var rule = TextRule("RULE-NUMBERS", path, "^hit$");
            var collector = new LogParserCollector();

            collector.Execute(rule, _context);
            File.AppendAllText(path, "c\nhit\n");
            collector.Execute(rule, _context);

            Assert.Equal(new long[] { 2, 5 }, _events.Select(e => Convert.ToInt64(e.Data["logLineNumber"])).ToArray());
        }
    }
}
