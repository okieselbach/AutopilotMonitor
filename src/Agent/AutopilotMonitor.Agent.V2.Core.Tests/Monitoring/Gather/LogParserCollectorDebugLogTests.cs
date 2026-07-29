using System;
using System.Collections.Generic;
using System.IO;
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
    /// Debug-trace coverage of <see cref="LogParserCollector"/>: the classic
    /// "rule delivers nothing" cases must be explained in the trace — zero matches,
    /// position-tracker skips, and plain-text logs parsed in the default cmtrace mode.
    /// </summary>
    public sealed class LogParserCollectorDebugLogTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly GatherRuleContext _context;
        private readonly string _debugLogPath;

        // %TEMP% lives under C:\Users (hard-blocked) — parse targets sit beside the
        // test assembly instead, admitted via UnrestrictedMode.
        private readonly string _outsideUsers = Path.Combine(
            AppContext.BaseDirectory, "gather-debug-tests-" + Guid.NewGuid().ToString("N"));

        public LogParserCollectorDebugLogTests()
        {
            Directory.CreateDirectory(_outsideUsers);
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _debugLogPath = Path.Combine(_tmp.Path, "gather_rules_debug.log");
            _context = new GatherRuleContext(
                logger, "sess", "tenant",
                evt => _events.Add(evt),
                null,
                new LogFilePositionTracker(),
                new GatherRuleDebugLog(_debugLogPath, logger))
            {
                UnrestrictedMode = true
            };
        }

        public void Dispose()
        {
            _tmp.Dispose();
            try { Directory.Delete(_outsideUsers, recursive: true); } catch { /* best effort */ }
        }

        private string TraceContent() => File.Exists(_debugLogPath) ? File.ReadAllText(_debugLogPath) : string.Empty;

        private static GatherRule Rule(string target, string pattern, string? format = null) => new GatherRule
        {
            RuleId = "GATHER-LP-DEBUG",
            Title = "logparser debug test",
            CollectorType = "logparser",
            Target = target,
            Parameters = format == null
                ? new Dictionary<string, string> { ["pattern"] = pattern }
                : new Dictionary<string, string> { ["pattern"] = pattern, ["format"] = format },
            Trigger = "startup",
            OutputEventType = "logparser_debug_test",
        };

        [Fact]
        public void ZeroMatches_writes_per_file_outcome_line()
        {
            var path = Path.Combine(_outsideUsers, "app.log");
            File.WriteAllText(path, "nothing interesting\nstill nothing\n");

            new LogParserCollector().Execute(Rule(path, "WILL_NEVER_MATCH_[0-9]+", format: "text"), _context);

            Assert.Empty(_events);
            var trace = TraceContent();
            Assert.Contains("app.log: read 2 lines", trace);
            Assert.Contains("matched 0", trace);
            Assert.Contains("mode=text", trace);
        }

        [Fact]
        public void SecondRun_without_new_content_traces_position_skip()
        {
            var path = Path.Combine(_outsideUsers, "twice.log");
            File.WriteAllText(path, "one line\n");
            var rule = Rule(path, "one", format: "text");

            var collector = new LogParserCollector();
            collector.Execute(rule, _context);   // reads the file, advances the position
            collector.Execute(rule, _context);   // nothing new

            Assert.Contains("twice.log: no new content (position", TraceContent());
        }

        [Fact]
        public void PlainTextFile_in_default_cmtrace_mode_gets_format_hint()
        {
            var path = Path.Combine(_outsideUsers, "plain.log");
            File.WriteAllText(path, "plain line one\nplain line two\n");

            new LogParserCollector().Execute(Rule(path, "plain"), _context); // default = cmtrace mode

            Assert.Empty(_events);
            var trace = TraceContent();
            Assert.Contains("parseFailures=2", trace);
            Assert.Contains("every line failed CMTrace parsing — if this is a plain-text log, set parameter format=text", trace);
        }

        [Fact]
        public void Match_traces_line_number_matched_text_and_groups()
        {
            var path = Path.Combine(_outsideUsers, "match.log");
            File.WriteAllText(path, "boring line\nError 0x80070005 in setup\n");

            new LogParserCollector().Execute(
                Rule(path, @"Error (?<code>0x[0-9A-Fa-f]+)", format: "text"), _context);

            Assert.Single(_events);
            var trace = TraceContent();
            Assert.Contains("match.log: line 2 matched \"Error 0x80070005\"", trace);
            Assert.Contains("groups: code=\"0x80070005\"", trace);
        }

        [Fact]
        public void MatchTraces_are_capped_per_file()
        {
            var path = Path.Combine(_outsideUsers, "many.log");
            var sb = new System.Text.StringBuilder();
            for (var i = 1; i <= 15; i++)
                sb.Append("hit number ").Append(i).Append('\n');
            File.WriteAllText(path, sb.ToString());

            new LogParserCollector().Execute(Rule(path, "hit", format: "text"), _context);

            Assert.Equal(15, _events.Count); // all matches still emit events
            var trace = TraceContent();
            Assert.Contains("many.log: line 10 matched", trace);
            Assert.Contains("further matches not traced individually", trace);
            Assert.DoesNotContain("many.log: line 11 matched", trace);
            Assert.Contains("matched 15", trace); // summary still carries the full count
        }

        [Fact]
        public void InvalidRegex_traces_the_pattern_itself()
        {
            var path = Path.Combine(_outsideUsers, "irrelevant.log");
            File.WriteAllText(path, "content\n");

            new LogParserCollector().Execute(Rule(path, "([unclosed", format: "text"), _context);

            Assert.Empty(_events);
            var trace = TraceContent();
            Assert.Contains("invalid regex pattern '([unclosed'", trace);
            Assert.Contains("rule can never match", trace);
        }

        [Fact]
        public void MissingFile_traces_no_files_matched()
        {
            var path = Path.Combine(_outsideUsers, "does-not-exist.log");

            new LogParserCollector().Execute(Rule(path, "x", format: "text"), _context);

            Assert.Contains("no files matched target", TraceContent());
        }
    }
}
