using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Logging;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// The read loop of <c>CheckLogFilesAsync</c> after the bounded-reader hardening: oversized
    /// physical lines are dropped without starving the entries around them, an unterminated
    /// EOF tail is held back for the writer instead of being raw-matched as fragments, a
    /// cancelled pass leaves an exact bookmark, and a hostile pass leaves one Warning line.
    /// </summary>
    public sealed class ImeLogTrackerReadLoopHardeningTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);
        private const string LogName = "AppWorkload.log";

        private static List<ImeLogPattern> Patterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern
            {
                PatternId = "T-MARK", Category = "always", Enabled = true,
                Pattern = @"^marker (?<n>\d+)", Action = "noop",
                Parameters = new Dictionary<string, string>(),
            },
        };

        private static string Entry(string message)
            => $"<![LOG[{message}]LOG]!><time=\"12:00:00.0000000\" date=\"8-30-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

        private sealed class Harness : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            private readonly string _matchLog;
            public ImeLogTracker Tracker { get; }
            public DateTime Now { get; set; } = T0;
            public string LogPath => Path.Combine(_tmp.Path, LogName);

            public Harness()
            {
                var logDir = Path.Combine(_tmp.Path, "agent");
                Directory.CreateDirectory(logDir);
                _matchLog = Path.Combine(logDir, "ime-pattern-matches.log");
                Tracker = new ImeLogTracker(_tmp.Path, Patterns(), new AgentLogger(logDir, AgentLogLevel.Info), matchLogPath: _matchLog);
                Tracker.UtcNowProvider = () => Now;
            }

            public void Append(string text) => File.AppendAllText(LogPath, text, new UTF8Encoding(false));

            public Task Pass(CancellationToken token = default) => Tracker.CheckLogFilesAsync(token);

            /// <summary>Marker numbers matched so far, in order.</summary>
            public List<int> Matched()
            {
                if (!File.Exists(_matchLog)) return new List<int>();
                return File.ReadAllLines(_matchLog)
                    .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"marker (\d+)"))
                    .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            }

            public string AgentLogText()
            {
                var dir = Path.Combine(_tmp.Path, "agent");
                return string.Join("\n", Directory.GetFiles(dir, "agent*.log").Select(File.ReadAllText));
            }

            public void Dispose()
            {
                Tracker.Dispose();
                _tmp.Dispose();
            }
        }

        [Fact]
        public async Task Oversized_single_line_is_dropped_and_the_entries_around_it_still_match()
        {
            using var h = new Harness();
            var huge = new string('x', ImeLogTracker.MaxEntryBytes + 10);
            h.Append(Entry("marker 1") + "\n" + Entry("marker 2 " + huge) + "\n" + Entry("marker 3") + "\n");

            await h.Pass();

            Assert.Equal(new[] { 1, 3 }, h.Matched());
            Assert.Contains("oversizedLines=1", h.AgentLogText());
        }

        [Fact]
        public async Task Oversized_multiline_opener_skips_its_continuation_lines_instead_of_raw_matching_them()
        {
            using var h = new Harness();
            var huge = new string('x', ImeLogTracker.MaxEntryBytes + 10);
            // Opener over the cap without its close, then continuation lines that would each
            // match raw if they reached the matcher, then the close, then a real entry.
            h.Append("<![LOG[marker 5 " + huge + "\nmarker 6\nmarker 7\n]LOG]!><time=\"12:00:00.0000000\" date=\"8-30-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\n"
                     + Entry("marker 8") + "\n");

            await h.Pass();

            Assert.Equal(new[] { 8 }, h.Matched());
        }

        [Fact]
        public async Task Unterminated_tail_is_held_back_until_the_writer_finishes_it()
        {
            using var h = new Harness();
            h.Append(Entry("marker 1") + "\n");
            await h.Pass();
            Assert.Equal(new[] { 1 }, h.Matched());

            // Writer mid-line: the closing tag and newline are not there yet.
            h.Append("<![LOG[marker 2");
            h.Now = T0.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1 }, h.Matched());

            h.Append("]LOG]!><time=\"12:00:00.0000000\" date=\"8-30-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\n");
            h.Now = T0.AddMilliseconds(200);
            await h.Pass();
            Assert.Equal(new[] { 1, 2 }, h.Matched());
        }

        [Fact]
        public async Task Held_tail_settles_after_the_file_stands_still_and_is_then_processed()
        {
            using var h = new Harness();
            // A final line without newline that IS complete (archived file, writer gone).
            h.Append(Entry("marker 1") + "\n" + Entry("marker 2"));
            await h.Pass();
            Assert.Equal(new[] { 1 }, h.Matched());

            h.Now = T0.AddMilliseconds(500);
            await h.Pass();
            Assert.Equal(new[] { 1 }, h.Matched());

            h.Now = T0.Add(ImeLogTracker.HeldTailSettle).AddMilliseconds(1);
            await h.Pass();
            Assert.Equal(new[] { 1, 2 }, h.Matched());

            // And nothing is re-read afterwards.
            h.Now = h.Now.AddSeconds(5);
            await h.Pass();
            Assert.Equal(new[] { 1, 2 }, h.Matched());
        }

        [Fact]
        public async Task Cancelled_pass_bookmarks_exactly_so_nothing_is_lost_or_duplicated()
        {
            using var h = new Harness();
            h.Append(Entry("marker 1") + "\n" + Entry("marker 2") + "\n" + Entry("marker 3") + "\n");

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await h.Pass(cts.Token);
            }
            Assert.Empty(h.Matched());

            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3 }, h.Matched());

            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3 }, h.Matched());
        }

        [Fact]
        public async Task Hostile_pass_leaves_one_warning_summary_not_one_per_line()
        {
            using var h = new Harness();
            var huge = new string('x', ImeLogTracker.MaxEntryBytes + 10);
            var sb = new StringBuilder();
            for (var i = 0; i < 3; i++) sb.Append(Entry("junk " + huge)).Append('\n');
            sb.Append(Entry("marker 9")).Append('\n');
            h.Append(sb.ToString());

            await h.Pass();

            Assert.Equal(new[] { 9 }, h.Matched());
            var text = h.AgentLogText();
            Assert.Equal(1, CountOf(text, "pass skipped work"));
            Assert.Contains("oversizedLines=3", text);
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            var idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
            return count;
        }
    }
}
