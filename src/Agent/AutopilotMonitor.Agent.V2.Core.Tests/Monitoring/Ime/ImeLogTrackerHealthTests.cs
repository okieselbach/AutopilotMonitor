using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    /// The tracker's self-observation: cumulative health counters, the per-pattern histogram
    /// (every enabled pattern, zeros included), the one-shot degraded callback, and that all of
    /// it survives a restart through the persisted state.
    /// </summary>
    public sealed class ImeLogTrackerHealthTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);
        private const string LogName = "AppWorkload.log";

        private static List<ImeLogPattern> Patterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern { PatternId = "T-MARK", Category = "always", Enabled = true, Pattern = @"^marker (?<n>\d+)", Action = "noop", Parameters = new Dictionary<string, string>() },
            new ImeLogPattern { PatternId = "T-NEVER", Category = "always", Enabled = true, Pattern = @"^never matches", Action = "noop", Parameters = new Dictionary<string, string>() },
            new ImeLogPattern { PatternId = "T-LOOSE", Category = "always", Enabled = true, Pattern = @"loose (?<n>\d+)", Action = "noop", Parameters = new Dictionary<string, string>() },
            new ImeLogPattern { PatternId = "T-OFF", Category = "always", Enabled = false, Pattern = @"^off", Action = "noop", Parameters = new Dictionary<string, string>() },
        };

        private static string Entry(string message)
            => $"<![LOG[{message}]LOG]!><time=\"12:00:00.0000000\" date=\"8-30-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

        private sealed class Harness : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public ImeLogTracker Tracker { get; private set; }
            public DateTime Now { get; set; } = T0;
            public string StateDir { get; }

            public Harness()
            {
                StateDir = Path.Combine(_tmp.Path, "state");
                Tracker = Build();
            }

            private ImeLogTracker Build()
            {
                var t = new ImeLogTracker(_tmp.Path, Patterns(), new AgentLogger(Path.Combine(_tmp.Path, "agent"), AgentLogLevel.Info), stateDirectory: StateDir);
                t.UtcNowProvider = () => Now;
                return t;
            }

            /// <summary>Simulates an agent restart: dispose, build a fresh tracker over the same state directory.</summary>
            public void Restart()
            {
                Tracker.Dispose();
                Tracker = Build();
                Tracker.LoadStateForTest();
            }

            public void Append(string text) => File.AppendAllText(Path.Combine(_tmp.Path, LogName), text, new UTF8Encoding(false));
            public Task Pass() => Tracker.CheckLogFilesAsync(CancellationToken.None);
            public void Dispose() { Tracker.Dispose(); _tmp.Dispose(); }
        }

        [Fact]
        public void Histogram_lists_every_enabled_pattern_with_zero_before_any_match()
        {
            using var h = new Harness();
            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal(new[] { "T-LOOSE", "T-MARK", "T-NEVER" }, health.PatternHits.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(health.PatternHits.Values, v => Assert.Equal(0, v));
            Assert.Equal(1, health.UnanchoredPatterns); // T-LOOSE
            Assert.False(health.HasSkippedWork);
        }

        [Fact]
        public async Task Counters_and_histogram_follow_the_lines_read()
        {
            using var h = new Harness();
            h.Append(Entry("marker 1") + "\n" + Entry("marker 2") + "\n" + Entry("something loose 3") + "\n" + Entry("noise") + "\n");
            await h.Pass();

            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal(4, health.LinesRead);
            Assert.Equal(3, health.EntriesMatched);
            Assert.Equal(2, health.PatternHits["T-MARK"]);
            Assert.Equal(1, health.PatternHits["T-LOOSE"]);
            Assert.Equal(0, health.PatternHits["T-NEVER"]);
            Assert.Equal(1, health.FilesTailed);
            Assert.Equal(0, health.BacklogBytes);
        }

        [Fact]
        public async Task Degraded_callback_fires_once_per_session_with_the_snapshot()
        {
            using var h = new Harness();
            var fired = new List<(ImeTrackerHealth Health, string File, string Pattern)>();
            h.Tracker.OnTrackerDegraded = (health, file, pattern) => fired.Add((health, file, pattern));

            var huge = new string('x', ImeLogTracker.MaxEntryBytes + 10);
            h.Append(Entry("junk " + huge) + "\n" + Entry("marker 1") + "\n");
            await h.Pass();
            h.Append(Entry("junk " + huge) + "\n");
            await h.Pass();

            var f = Assert.Single(fired);
            Assert.Equal(LogName, f.File);
            Assert.Equal(1, f.Health.OversizedLines);
            Assert.True(f.Health.HasSkippedWork);
            Assert.Equal(2, h.Tracker.GetHealthSnapshot().OversizedLines); // still counted after the one-shot
        }

        [Fact]
        public async Task Health_histogram_and_degraded_flag_survive_a_restart()
        {
            using var h = new Harness();
            var fired = 0;
            h.Tracker.OnTrackerDegraded = (_, __, ___) => fired++;
            var huge = new string('x', ImeLogTracker.MaxEntryBytes + 10);
            h.Append(Entry("marker 1") + "\n" + Entry("junk " + huge) + "\n");
            await h.Pass();
            h.Tracker.SaveStateForTest();

            h.Restart();
            h.Tracker.OnTrackerDegraded = (_, __, ___) => fired++;
            h.Append(Entry("marker 2") + "\n" + Entry("junk " + huge) + "\n");
            await h.Pass();

            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal(2, health.PatternHits["T-MARK"]);
            Assert.Equal(4, health.LinesRead);
            Assert.Equal(2, health.OversizedLines);
            Assert.Equal(1, fired); // the persisted flag suppressed the second emission
        }

        [Fact]
        public async Task Handler_time_does_not_count_against_the_line_budget()
        {
            // Session 946ccbd6: a stalled Hyper-V guest spent >2 s between two patterns of one
            // genuine line and the wall-clock budget skipped the rest of the line. The budget
            // bounds Regex.Match time only — a slow handler on an early pattern must leave the
            // later patterns of the same line running and must not count as skipped work.
            using var h = new Harness();
            h.Tracker.OnPatternMatched = id => { if (id == "T-MARK") Thread.Sleep(2300); };
            h.Append(Entry("marker 1 loose 1") + "\n");
            await h.Pass();

            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal(1, health.PatternHits["T-MARK"]);
            Assert.Equal(1, health.PatternHits["T-LOOSE"]); // ordered after T-MARK — still matched
            Assert.Equal(0, health.BudgetBreaks);
            Assert.False(health.HasSkippedWork);
        }

        [Fact]
        public async Task Starved_timeout_is_retried_once_and_the_recovered_match_is_not_skipped_work()
        {
            // Session b9f1d134: a CPU-starved Hyper-V guest tripped the 1 s wall-clock regex
            // timeout on a line the pattern genuinely matches in 5.8 µs. A timeout whose attempt
            // consumed almost no match cost is starvation — retried once, recovered, no warning.
            using var h = new Harness();
            var degraded = 0;
            h.Tracker.OnTrackerDegraded = (_, __, ___) => degraded++;
            h.Tracker.MatchCostReader = () => 0; // attempt cost 0 < gate
            var attempts = 0;
            h.Tracker.MatchInvoker = (id, regex, input) =>
            {
                if (id == "T-MARK" && ++attempts == 1) throw new RegexMatchTimeoutException();
                return regex.Match(input);
            };

            h.Append(Entry("marker 1") + "\n");
            await h.Pass();

            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal(2, attempts);
            Assert.Equal(1, health.PatternHits["T-MARK"]); // recovered
            Assert.Equal(0, health.RegexTimeouts);
            Assert.Equal(1, health.RegexTimeoutRetries);
            Assert.False(health.HasSkippedWork);
            Assert.Equal(0, degraded);
        }

        [Fact]
        public async Task Timeout_that_burned_real_cpu_is_skipped_without_a_retry()
        {
            // The gate exists so a genuinely spinning pattern never gets a second helping of
            // CPU: an attempt at or over the gate is a real timeout, counted and skipped. The
            // wall-clock fallback lands here by construction (a timed-out attempt always
            // measures >= the full timeout > gate).
            using var h = new Harness();
            var degraded = 0;
            h.Tracker.OnTrackerDegraded = (_, __, ___) => degraded++;
            h.Tracker.LineMatchBudget = long.MaxValue; // isolate the gate from the budget
            long cost = 0;
            h.Tracker.MatchCostReader = () => cost += h.Tracker.TimeoutRetryGate; // every delta >= gate
            var attempts = 0;
            h.Tracker.MatchInvoker = (id, regex, input) =>
            {
                if (id == "T-MARK") { attempts++; throw new RegexMatchTimeoutException(); }
                return regex.Match(input);
            };

            h.Append(Entry("marker 1 loose 1") + "\n");
            await h.Pass();

            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal(1, attempts); // no retry
            Assert.Equal(0, health.PatternHits["T-MARK"]);
            Assert.Equal(1, health.PatternHits["T-LOOSE"]); // later patterns still ran
            Assert.Equal(1, health.RegexTimeouts);
            Assert.Equal(0, health.RegexTimeoutRetries);
            Assert.True(health.HasSkippedWork);
            Assert.Equal(1, degraded);
        }

        [Fact]
        public async Task Retry_that_times_out_again_is_counted_once_and_not_retried_further()
        {
            using var h = new Harness();
            h.Tracker.MatchCostReader = () => 0; // every attempt looks starved
            var attempts = 0;
            h.Tracker.MatchInvoker = (id, regex, input) =>
            {
                if (id == "T-MARK") { attempts++; throw new RegexMatchTimeoutException(); }
                return regex.Match(input);
            };

            h.Append(Entry("marker 1") + "\n");
            await h.Pass();

            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal(2, attempts); // exactly one retry
            Assert.Equal(1, health.RegexTimeouts);
            Assert.Equal(1, health.RegexTimeoutRetries);
            Assert.True(health.HasSkippedWork);
        }

        [Fact]
        public async Task Budget_break_skips_the_remaining_patterns_and_names_the_first_skipped()
        {
            using var h = new Harness();
            string? firstSkipped = null;
            h.Tracker.OnTrackerDegraded = (_, __, pattern) => firstSkipped = pattern;
            h.Tracker.LineMatchBudget = 500;
            long cost = 0;
            h.Tracker.MatchCostReader = () => cost += 1000; // every Match charges 1000 units

            h.Append(Entry("marker 1 loose 1") + "\n");
            await h.Pass();

            var health = h.Tracker.GetHealthSnapshot();
            Assert.Equal("T-NEVER", firstSkipped); // pattern order: T-MARK, T-NEVER, T-LOOSE
            Assert.Equal(1, health.PatternHits["T-MARK"]); // matched before the budget was spent
            Assert.Equal(0, health.PatternHits["T-LOOSE"]); // skipped by the break
            Assert.Equal(1, health.BudgetBreaks);
            Assert.True(health.HasSkippedWork);
        }

        [Fact]
        public async Task Retry_counter_survives_a_restart()
        {
            using var h = new Harness();
            h.Tracker.MatchCostReader = () => 0;
            var attempts = 0;
            h.Tracker.MatchInvoker = (id, regex, input) =>
            {
                if (id == "T-MARK" && ++attempts == 1) throw new RegexMatchTimeoutException();
                return regex.Match(input);
            };
            h.Append(Entry("marker 1") + "\n");
            await h.Pass();
            h.Tracker.SaveStateForTest();

            h.Restart();
            Assert.Equal(1, h.Tracker.GetHealthSnapshot().RegexTimeoutRetries);
        }

        [Fact]
        public void Ime_agent_version_is_captured_from_the_pattern_action()
        {
            using var h = new Harness();
            h.Tracker.CompilePatterns(new List<ImeLogPattern>
            {
                new ImeLogPattern { PatternId = "IME-AGENT-VERSION", Category = "always", Enabled = true, Pattern = @"^Agent version is: (?<agentVersion>[\d.]+)", Action = "imeAgentVersion", Parameters = new Dictionary<string, string>() },
            });
            h.Tracker.ProcessLogMessageForTest("Agent version is: 1.105.103.0");
            Assert.Equal("1.105.103.0", h.Tracker.GetHealthSnapshot().ImeAgentVersion);
        }
    }
}
