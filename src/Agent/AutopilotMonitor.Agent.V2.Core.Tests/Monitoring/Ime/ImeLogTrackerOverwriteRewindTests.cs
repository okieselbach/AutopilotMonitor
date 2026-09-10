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
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Session 46749560: IME's trace listener writes every log through one FileMode.Append
    /// stream per process, so a process that resumes logging after another one appended
    /// overwrites those bytes at its own stale position. The tracker had read the first version
    /// and never saw the second — 6 of 16 script results and 4 of 16 executor end blocks were
    /// lost while the final files looked intact. These tests drive that exact write pattern
    /// (positional overwrite behind the bookmark) against the two multi-writer logs and pin the
    /// contract: unchanged bytes never rewind, changed bytes are processed exactly once, and the
    /// executor lines a rewind uncovers belong to the invocation that owns their file position.
    /// </summary>
    public sealed class ImeLogTrackerOverwriteRewindTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 10, 19, 30, 0, DateTimeKind.Utc);

        private static List<ImeLogPattern> MarkerPatterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern
            {
                PatternId = "T-MARK", Category = "always", Enabled = true,
                Pattern = @"^marker (?<n>\d+)", Action = "noop",
                Parameters = new Dictionary<string, string>(),
            },
        };

        private static readonly string[] ScriptPatternIds =
        {
            "PS-AGENT-INVOCATION", "PS-AGENT-ARG", "PS-AGENT-SCRIPT-START", "PS-AGENT-EXITCODE",
            "PS-AGENT-OUTPUT", "PS-AGENT-COMPLETED", "PS-SCRIPT-GENERATED", "PS-SCRIPT-CONTEXT", "PS-SCRIPT-RESULT",
        };

        /// <summary>The shipped pattern JSON — the same source combine.js embeds — so the contract is the real one.</summary>
        private static List<ImeLogPattern> ScriptPatterns()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? patternDir = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "rules", "ime-log-patterns");
                if (Directory.Exists(candidate)) { patternDir = candidate; break; }
                dir = dir.Parent;
            }
            Assert.NotNull(patternDir);

            var byId = new Dictionary<string, ImeLogPattern>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(patternDir, "*.json"))
            {
                var pattern = JsonConvert.DeserializeObject<ImeLogPattern>(File.ReadAllText(file));
                if (pattern?.PatternId != null) byId[pattern.PatternId] = pattern;
            }
            return ScriptPatternIds.Select(id =>
            {
                Assert.True(byId.TryGetValue(id, out var p), $"Shipped IME pattern '{id}' not found under {patternDir}.");
                return p!;
            }).ToList();
        }

        private static string Entry(string message, string thread = "1")
            => $"<![LOG[{message}]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"{thread}\" file=\"\">\r\n";

        private const string PlatformId = "d39eeef3-e88e-41a0-82d7-5e28ee7b5acf";
        private const string PlatformStartLine =
            @"Adding argument powershell with value C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\73d664e4-0886-4a73-b745-c694da45ddb4_d39eeef3-e88e-41a0-82d7-5e28ee7b5acf.ps1 to the named argument list.";
        private const string DetectionStartLine =
            @"Adding argument detectionScript with value C:\Windows\IMECache\DetectionScripts\App_cc46aa62-e93a-41a1-bdd0-44f57e92a66d.ps1 to the named argument list.";
        private const string GeneratedLine =
            @"Script file C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\73d664e4-0886-4a73-b745-c694da45ddb4_d39eeef3-e88e-41a0-82d7-5e28ee7b5acf.ps1 is generated.";
        private const string ResultLine =
            "[PowerShell] User Id = 73d664e4-0886-4a73-b745-c694da45ddb4, Policy id = " + PlatformId + ", policy result = Success";

        private sealed class Harness : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            private readonly string _matchLog;
            private readonly List<ImeLogPattern> _patterns;
            public ImeLogTracker Tracker { get; private set; }
            public DateTime Now { get; set; } = T0;
            public string LogName { get; }
            public string LogPath => Path.Combine(_tmp.Path, LogName);
            public string StateDir { get; }
            public List<ScriptExecutionState> Completed { get; } = new List<ScriptExecutionState>();

            public Harness(string logName, List<ImeLogPattern> patterns)
            {
                LogName = logName;
                _patterns = patterns;
                var logDir = Path.Combine(_tmp.Path, "agent");
                Directory.CreateDirectory(logDir);
                _matchLog = Path.Combine(logDir, "ime-pattern-matches.log");
                StateDir = Path.Combine(_tmp.Path, "state");
                Tracker = Build();
            }

            private ImeLogTracker Build()
            {
                var t = new ImeLogTracker(_tmp.Path, _patterns, new AgentLogger(Path.Combine(_tmp.Path, "agent"), AgentLogLevel.Debug),
                    matchLogPath: _matchLog, stateDirectory: StateDir);
                t.UtcNowProvider = () => Now;
                t.OnScriptCompleted = s => Completed.Add(s);
                return t;
            }

            /// <summary>Agent restart: dispose, rebuild over the same state directory, restore the bookmark.</summary>
            public void Restart()
            {
                Tracker.SaveStateForTest();
                Tracker.Dispose();
                Tracker = Build();
                Tracker.LoadStateForTest();
            }

            public void Append(string text) => File.AppendAllText(LogPath, text, new UTF8Encoding(false));

            /// <summary>What IME's stale writer does: write at its own position, over whatever is there.</summary>
            public void Overwrite(long offset, string text)
            {
                using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }

            public long Length => new FileInfo(LogPath).Length;

            public Task Pass(CancellationToken token = default) => Tracker.CheckLogFilesAsync(token);

            /// <summary>Marker numbers matched so far, in match order.</summary>
            public List<int> Matched()
            {
                if (!File.Exists(_matchLog)) return new List<int>();
                return File.ReadAllLines(_matchLog)
                    .Select(l => Regex.Match(l, @"\[T-MARK\] <!\[LOG\[marker (\d+)"))
                    .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            }

            public ImeTrackerHealth Health => Tracker.GetHealthSnapshot();

            public void Dispose()
            {
                Tracker.Dispose();
                _tmp.Dispose();
            }
        }

        // -----------------------------------------------------------------------
        // Marker-level contract on AgentExecutor.log
        // -----------------------------------------------------------------------

        [Fact]
        public async Task Unchanged_bytes_never_rewind_however_often_they_are_verified()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + "<![LOG[marker 2\nsecond line\n]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n" + Entry("marker 3"));
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3 }, h.Matched());

            for (var i = 0; i < 5; i++)
            {
                h.Tracker.RequestOverwriteCheckForTest();
                h.Now = h.Now.AddSeconds(2);
                await h.Pass();
            }

            Assert.Equal(new[] { 1, 2, 3 }, h.Matched());
            Assert.Equal(0, h.Health.OverwriteRewinds);
            Assert.Equal(5, h.Health.VerifyPasses);
            Assert.Equal(3, h.Tracker.LedgerEntryCountForTest("AgentExecutor.log"));
        }

        [Fact]
        public async Task Non_multi_writer_files_keep_no_ledger_and_are_never_verified()
        {
            using var h = new Harness("AppWorkload.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2"));
            await h.Pass();
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddSeconds(2);
            await h.Pass();

            Assert.Equal(new[] { 1, 2 }, h.Matched());
            Assert.Equal(0, h.Tracker.LedgerEntryCountForTest("AppWorkload.log"));
            Assert.Equal(0, h.Health.VerifyPasses);
            Assert.Equal(0, h.Health.VerifiedBytes);
        }

        [Fact]
        public async Task Block_written_over_read_bytes_is_processed_once_and_the_surviving_tail_is_not_repeated()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            var one = Entry("marker 1");
            h.Append(one + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3 }, h.Matched());

            // A stale writer lays two entries over the first two (same length: marker 3 survives
            // intact at its old offset). No growth, no fragment — only the end signal finds it.
            h.Overwrite(0, Entry("marker 7") + Entry("marker 8"));
            Assert.Equal(3 * one.Length, h.Length);
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 7, 8 }, h.Matched());
            Assert.Equal(1, h.Health.OverwriteRewinds);
            Assert.Equal(3 * one.Length, h.Health.OverwriteBytesReprocessed);
            Assert.Equal(3, h.Tracker.LedgerEntryCountForTest("AgentExecutor.log"));

            // Nothing left to find.
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddSeconds(2);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 7, 8 }, h.Matched());
            Assert.Equal(1, h.Health.OverwriteRewinds);
        }

        [Fact]
        public async Task Block_that_cuts_an_old_entry_leaves_a_fragment_that_is_never_matched()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();

            // Longer than two old entries, shorter than three: the tail of marker 3's line survives
            // as a fragment behind the new block.
            h.Overwrite(0, Entry("marker 7") + Entry("marker 8 is a little longer"));
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 7, 8 }, h.Matched());
            Assert.Equal(1, h.Health.OverwriteRewinds);
        }

        [Fact]
        public async Task Guard_zone_catches_a_short_block_when_the_file_grows_without_any_signal()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();

            // Same-length replacement of marker 1 (no fragment), then an ordinary append by
            // another writer: the growth pass re-checks the last 4 KB and finds the change.
            h.Overwrite(0, Entry("marker 7"));
            h.Append(Entry("marker 4"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 7, 4 }, h.Matched());
            Assert.Equal(1, h.Health.OverwriteRewinds);
            Assert.Equal(1, h.Health.VerifyPasses); // the full check that follows a guard hit
            Assert.True(h.Health.VerifiedBytes > 0);
        }

        [Fact]
        public async Task Guard_hit_rewinds_to_the_true_start_of_the_block_not_the_guard_zone()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            // ~6 KB of entries so the guard zone covers only the last few.
            var sb = new StringBuilder();
            for (var i = 1; i <= 60; i++) sb.Append(Entry($"marker {i}"));
            h.Append(sb.ToString());
            await h.Pass();
            Assert.Equal(60, h.Matched().Count);

            // A block over everything plus one more entry: the guard sees a change at its
            // zone start, the full check must place the rewind at offset 0.
            var block = new StringBuilder();
            for (var i = 101; i <= 161; i++) block.Append(Entry($"marker {i}"));
            h.Overwrite(0, block.ToString());
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            var matched = h.Matched();
            Assert.Equal(60 + 61, matched.Count);
            Assert.Equal(Enumerable.Range(101, 61), matched.Skip(60));
            Assert.Equal(1, h.Health.OverwriteRewinds);
        }

        [Fact]
        public async Task Fragment_at_the_bookmark_triggers_the_check_when_no_ledger_entry_starts_in_the_guard_zone()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            var one = Entry("marker 1");
            var big = Entry("marker 2 " + new string('x', 6000));
            h.Append(one + big);
            await h.Pass();
            Assert.Equal(new[] { 1, 2 }, h.Matched());

            // The 6 KB entry is the last ledger entry and starts before the 4 KB guard zone, so
            // the guard has nothing to check. The stale writer's block is longer than that entry:
            // the pass after it starts mid-line — a fragment — and must not match it raw.
            h.Overwrite(one.Length, Entry("marker 7 " + new string('y', 3000)) + Entry("marker 8 " + new string('z', 3100)));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1, 2 }, h.Matched());

            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 7, 8 }, h.Matched());
            Assert.Equal(1, h.Health.OverwriteRewinds);
        }

        [Fact]
        public async Task Hidden_block_is_found_by_the_cadence_while_a_script_is_in_flight_and_the_cadence_is_throttled()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();

            // A platform script is pending → contention. First pass in contention verifies at once.
            h.Tracker.SeedPendingPlatformScriptForTesting("policyX", exitCode: null, exitObservedAtUtc: null, startedAtUtc: T0);
            h.Now = T0.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(1, h.Health.VerifyPasses);

            // Same-length replacement, no growth, no signal: only the cadence can find it —
            // and not before a second has passed since the last verification.
            h.Overwrite(0, Entry("marker 7"));
            h.Now = T0.AddMilliseconds(500);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3 }, h.Matched());
            Assert.Equal(1, h.Health.VerifyPasses);

            h.Now = T0.AddMilliseconds(1200);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 7 }, h.Matched());
            Assert.Equal(2, h.Health.VerifyPasses);
            Assert.Equal(1, h.Health.OverwriteRewinds);
        }

        [Fact]
        public async Task Without_contention_or_signal_nothing_is_verified()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2"));
            await h.Pass();

            for (var i = 0; i < 20; i++)
            {
                h.Now = h.Now.AddSeconds(1);
                await h.Pass();
            }

            Assert.Equal(0, h.Health.VerifyPasses);
            Assert.Equal(0, h.Health.VerifiedBytes);
        }

        [Fact]
        public async Task Restart_seeds_the_ledger_so_an_overwrite_after_the_restart_is_still_found()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();
            h.Restart();
            Assert.Equal(0, h.Tracker.LedgerEntryCountForTest("AgentExecutor.log"));

            // First pass after the restart: nothing new to read, but the ledger is seeded from
            // the bytes behind the persisted bookmark.
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(3, h.Tracker.LedgerEntryCountForTest("AgentExecutor.log"));

            h.Overwrite(0, Entry("marker 7"));
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 7 }, h.Matched());
            Assert.Equal(1, h.Health.OverwriteRewinds);

            // The counter is part of the persisted health.
            h.Restart();
            Assert.Equal(1, h.Health.OverwriteRewinds);
        }

        [Fact]
        public async Task Rollover_clears_the_ledger_and_the_new_file_is_read_without_a_false_rewind()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();

            File.WriteAllText(h.LogPath, Entry("marker 7") + Entry("marker 8"), new UTF8Encoding(false));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 7, 8 }, h.Matched());

            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddSeconds(2);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 7, 8 }, h.Matched());
            Assert.Equal(0, h.Health.OverwriteRewinds);
            Assert.Equal(2, h.Tracker.LedgerEntryCountForTest("AgentExecutor.log"));
        }

        [Fact]
        public async Task Cancelled_pass_does_not_lose_a_pending_check()
        {
            using var h = new Harness("AgentExecutor.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();
            h.Overwrite(0, Entry("marker 7") + Entry("marker 8"));
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddMilliseconds(100);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await h.Pass(cts.Token);
            }

            // The cancelled pass touched nothing after the rewind; the next one reads it all.
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 7, 8 }, h.Matched());
        }

        // -----------------------------------------------------------------------
        // The field scenario with the shipped patterns
        // -----------------------------------------------------------------------

        [Fact]
        public async Task Executor_end_block_written_over_a_detection_invocation_belongs_to_its_own_script()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());

            // Executor E starts its platform script and waits for PowerShell.
            h.Append(Entry("ExecutorLog AgentExecutor gets invoked") + Entry(PlatformStartLine) + Entry("[Executor] created powershell with process id 12708"));
            await h.Pass();
            var executorPosition = h.Length;

            // Detection executor D appends its whole run while E waits — the tracker reads it.
            var detectionBlock = Entry("ExecutorLog AgentExecutor gets invoked") + Entry(DetectionStartLine)
                + Entry("Powershell exit code is 3") + Entry("write output done. output = detected nothing, error = ")
                + Entry("Agent executor completed.") + Entry(@"Writing result: False to resultFilePath: C:\x\App_cc46aa62.txt");
            h.Append(detectionBlock);
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            // E ends: its end block lands at its own stale position, over D's lines.
            var endBlock = Entry("Powershell exit code is 0") + Entry("write output done. output = Hello from E, error = ") + Entry("Agent executor completed.");
            Assert.True(endBlock.Length < detectionBlock.Length);
            h.Overwrite(executorPosition, endBlock);

            // Nothing visible signalled the end; the cadence (E is pending) finds the block.
            h.Now = h.Now.AddSeconds(1.5);
            await h.Pass();
            Assert.Equal(1, h.Health.OverwriteRewinds);

            // IME's result line (in the other file) completes the script with E's own data.
            h.Tracker.ProcessLogMessageForTest(ResultLine);
            var script = Assert.Single(h.Completed);
            Assert.Equal(PlatformId, script.PolicyId);
            Assert.Equal("Success", script.Result);
            Assert.Equal("ime_policy_result", script.ResultSource);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal("Hello from E", script.Stdout);
        }

        [Fact]
        public async Task Detection_exit_lines_read_before_the_overwrite_never_reach_the_platform_script()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(Entry("ExecutorLog AgentExecutor gets invoked") + Entry(PlatformStartLine) + Entry("[Executor] created powershell with process id 1"));
            await h.Pass();
            h.Append(Entry("ExecutorLog AgentExecutor gets invoked") + Entry(DetectionStartLine)
                + Entry("Powershell exit code is 3") + Entry("write output done. output = detected nothing, error = ") + Entry("Agent executor completed."));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            // The platform result arrives without any end block ever becoming visible: held for
            // the grace, then emitted — the detection's exit code 3 must not stand in.
            h.Tracker.ProcessLogMessageForTest(ResultLine);
            Assert.Empty(h.Completed);
            h.Now = h.Now.Add(ImeLogTracker.PlatformScriptEndBlockGrace);
            h.Tracker.FlushPendingPlatformScriptResults(h.Now);
            var script = Assert.Single(h.Completed);
            Assert.Equal("Success", script.Result);
            Assert.Null(script.ExitCode);
            Assert.Null(script.Stdout);
        }

        [Fact]
        public async Task Ime_result_line_hidden_by_an_executor_start_line_is_recovered_from_the_ime_log()
        {
            using var h = new Harness("IntuneManagementExtension.log", ScriptPatterns());
            h.Append(Entry(GeneratedLine, "17") + Entry("Launch powershell executor in user session", "17") + Entry("process id = 10608", "17"));
            await h.Pass();
            var servicePosition = h.Length;

            // The executor process appends its telemetry start line at the true end of file.
            h.Append(Entry("[Telemetry] Telemetry is enabled. Starting mananger...", "1"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Empty(h.Completed);

            // The service's first write after the script wait lands at ITS position — over the
            // executor's line; the block is longer, so the pass sees a fragment at the bookmark.
            h.Overwrite(servicePosition, Entry("Execution is done, collecting result", "17") + Entry(ResultLine, "17") + Entry("[PowerShell] Success, Result details length: 315", "17"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(1, h.Health.OverwriteRewinds);

            // Recovered on the IME side alone (this harness has no AgentExecutor.log): held for
            // the end block, emitted without an exit code after the grace.
            Assert.Empty(h.Completed);
            h.Now = h.Now.Add(ImeLogTracker.PlatformScriptEndBlockGrace);
            h.Tracker.FlushPendingPlatformScriptResults(h.Now);

            var script = Assert.Single(h.Completed);
            Assert.Equal(PlatformId, script.PolicyId);
            Assert.Equal("Success", script.Result);
            Assert.Equal("ime_policy_result", script.ResultSource);
            Assert.Equal("User", script.RunContext);
        }
    }
}
