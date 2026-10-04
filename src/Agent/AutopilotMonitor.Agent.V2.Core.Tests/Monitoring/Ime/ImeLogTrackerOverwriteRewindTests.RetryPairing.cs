using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// B-96m: IME re-runs a failed platform script on later check-ins, so AgentExecutor.log can hold several runs of one
    /// policy before the tracker has read IME's result of the first — always on the agent's first pass (the agent starts
    /// mid-enrollment and reads all of AgentExecutor.log before IntuneManagementExtension.log), after a restart, and
    /// whenever a result line surfaces late. With one slot per policy the second executor start reused the first run's
    /// slot and its end block overwrote the first one: the first IME result went out with the LAST run's exit code and
    /// output (session eff1b413: the bootstrap script "Failed (exit 0)" with the stdout of its successful retry), every
    /// later result without an end block. These tests pin one run per executor start, each IME result paired with the
    /// run of its policy that started last before it, each run carrying its own end block.
    /// </summary>
    public sealed partial class ImeLogTrackerOverwriteRewindTests
    {
        private const string RetryPolicyId = "4c2d8e1a-7b3f-4a9e-8d21-6f0b5c3e9a74";
        private const string RetryUser = "00000000-0000-0000-0000-000000000000";

        private static string ImeEntryAt(string time, string message)
            => $"<![LOG[{message}]LOG]!><time=\"{time}\" date=\"9-10-2026\" component=\"IntuneManagementExtension\" context=\"\" type=\"1\" thread=\"12\" file=\"\">\r\n";

        /// <summary>One executor run's start block: banner, the platform argument line, paths, launch.</summary>
        private static string RunStart(string time, string pid, string policyId = RetryPolicyId, string userId = RetryUser)
        {
            var path = $@"{ImeDir}\Policies\Scripts\{userId}_{policyId}.ps1";
            var results = $@"{ImeDir}\Policies\Results\{userId}_{policyId}";
            return ExecutorBanner(time)
                + EntryAt(time, $"Adding argument powershell with value {path} to the named argument list.")
                + EntryAt(time, "Powershell option gets invoked")
                + EntryAt(time, path)
                + EntryAt(time, results + ".output")
                + EntryAt(time, results + ".error")
                + EntryAt(time, results + ".timeout")
                + ExecutorLaunch(time, path, pid);
        }

        /// <summary>
        /// One executor run's end block in AgentExecutor's order. "error from script =" logs the stderr buffer before the
        /// output readers finished (<paramref name="stderrSnapshot"/>); "write output done" logs the final texts, the
        /// error part being exactly the error file IME judges.
        /// </summary>
        private static string RunEnd(string time, int exitCode, string stdout, string stderr, string? stderrSnapshot = null)
            => EntryAt(time, $"Powershell exit code is {exitCode}")
            + EntryAt(time, $"length of out={stdout.Length}")
            + EntryAt(time, $"length of error={stderr.Length}")
            + EntryAt(time, "error from script =" + (stderrSnapshot ?? stderr))
            + EntryAt(time, exitCode == 0 ? "Powershell script is successfully executed." : "Powershell script is failed to execute")
            + EntryAt(time, $"write output done. output = {stdout}, error = {stderr}")
            + EntryAt(time, "Revert Wow64FsRedirection")
            + EntryAt(time, "Agent executor completed.");

        private static string ImeRunStart(string time, string policyId = RetryPolicyId, string userId = RetryUser)
            => ImeEntryAt(time, $@"Script file {ImeDir}\Policies\Scripts\{userId}_{policyId}.ps1 is generated.");

        private static string ImeRunResult(string time, string result, string policyId = RetryPolicyId, string userId = RetryUser)
            => ImeEntryAt(time, $"[PowerShell] User Id = {userId}, Policy id = {policyId}, policy result = {result}");

        private static void AppendImeLog(Harness h, string text)
            => File.AppendAllText(Path.Combine(Path.GetDirectoryName(h.LogPath)!, "IntuneManagementExtension.log"), text, new UTF8Encoding(false));

        /// <summary>Result | exit | stdout | stderr — readable in an assertion failure; "-" no exit code, "~" never read.</summary>
        private static string Shape(ScriptExecutionState s)
            => $"{s.Result}|{(s.ExitCode.HasValue ? s.ExitCode.Value.ToString() : "-")}|{s.Stdout ?? "~"}|{s.Stderr ?? "~"}";

        /// <summary>
        /// Two poll passes as the agent runs them (read the files, then the flushes), the second past the result hold:
        /// a result missing its end block goes out by then.
        /// </summary>
        private static async Task PassAndSettle(Harness h)
        {
            await h.Tracker.RunPollPassAsync(default);
            h.Now = h.Now.AddSeconds(6);
            await h.Tracker.RunPollPassAsync(default);
        }

        [Fact]
        public async Task First_pass_pairs_each_ime_result_with_the_end_block_of_its_own_run()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            // IME's first run and two retries on later check-ins, all written before the agent started.
            h.Append(RunStart("12:00:10.0000000", "1001") + RunEnd("12:00:12.0000000", 1, "", "Run A failed")
                + RunStart("12:10:10.0000000", "1002") + RunEnd("12:10:12.0000000", 1, "Run B output", "")
                + RunStart("12:20:10.0000000", "1003") + RunEnd("12:20:12.0000000", 0, "Run C output", ""));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:12.1000000", "Failed")
                + ImeRunStart("12:10:08.0000000") + ImeRunResult("12:10:12.1000000", "Success")
                + ImeRunStart("12:20:08.0000000") + ImeRunResult("12:20:12.1000000", "Success"));

            await PassAndSettle(h);

            Assert.Equal(new[] { "Failed|1||Run A failed", "Success|1|Run B output|", "Success|0|Run C output|" },
                h.Completed.Select(Shape).ToArray());
            Assert.All(h.Completed, s => Assert.Equal("ime_policy_result", s.ResultSource));
            Assert.True(h.Completed[0].StartedAtUtc < h.Completed[1].StartedAtUtc && h.Completed[1].StartedAtUtc < h.Completed[2].StartedAtUtc);
            Assert.Equal(3, h.Completed.Select(s => s.RunId).Distinct().Count());
        }

        [Fact]
        public async Task Retry_of_the_bootstrap_script_never_lends_its_success_output_to_the_failed_first_attempt()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "2001") + RunEnd("12:00:20.0000000", 1, "attempt 1: download failed", "Invoke-WebRequest : The remote name could not be resolved")
                + RunStart("12:10:10.0000000", "2002") + RunEnd("12:10:20.0000000", 0, "===== Bootstrap Completed Successfully =====", ""));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:20.1000000", "Failed")
                + ImeRunStart("12:10:08.0000000") + ImeRunResult("12:10:20.1000000", "Success"));

            await PassAndSettle(h);

            Assert.Equal(new[]
            {
                "Failed|1|attempt 1: download failed|Invoke-WebRequest : The remote name could not be resolved",
                "Success|0|===== Bootstrap Completed Successfully =====|",
            }, h.Completed.Select(Shape).ToArray());
        }

        [Fact]
        public async Task Run_killed_at_the_timeout_reports_failed_without_the_next_runs_end_block()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            // IME killed the first executor after 1800 s: its start block is all AgentExecutor.log holds of it.
            h.Append(RunStart("12:00:10.0000000", "3001")
                + RunStart("12:40:10.0000000", "3002") + RunEnd("12:40:12.0000000", 0, "Run B output", ""));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:30:10.5000000", "Failed")
                + ImeRunStart("12:40:08.0000000") + ImeRunResult("12:40:12.1000000", "Success"));

            await PassAndSettle(h);

            Assert.Equal(new[] { "Failed|-|~|~", "Success|0|Run B output|" }, h.Completed.Select(Shape).ToArray());
        }

        [Fact]
        public async Task Run_whose_result_never_surfaced_falls_back_on_imes_rule_before_the_next_run()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "4001") + RunEnd("12:00:12.0000000", 0, "Run A output", "Run A wrote an error")
                + RunStart("12:10:10.0000000", "4002") + RunEnd("12:10:12.0000000", 0, "Run B output", ""));
            // Run A's result line was lost; run B's is there.
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunStart("12:10:08.0000000") + ImeRunResult("12:10:12.1000000", "Success"));

            await PassAndSettle(h);

            Assert.Equal(new[] { "Failed|0|Run A output|Run A wrote an error", "Success|0|Run B output|" }, h.Completed.Select(Shape).ToArray());
            Assert.Equal(new[] { "agentexecutor_fallback", "ime_policy_result" }, h.Completed.Select(s => s.ResultSource).ToArray());
        }

        [Fact]
        public async Task Run_in_flight_survives_the_emit_of_the_run_before_it()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "5001") + RunEnd("12:00:12.0000000", 0, "Run A output", "")
                + RunStart("12:10:10.0000000", "5002"));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:12.1000000", "Success") + ImeRunStart("12:10:08.0000000"));
            await PassAndSettle(h);
            h.Now = h.Now.AddSeconds(30);
            await h.Pass();

            // Run B ends minutes later.
            h.Append(RunEnd("12:10:40.0000000", 0, "Run B output", ""));
            AppendImeLog(h, ImeRunResult("12:10:40.1000000", "Success"));
            await PassAndSettle(h);

            Assert.Equal(new[] { "Success|0|Run A output|", "Success|0|Run B output|" }, h.Completed.Select(Shape).ToArray());
        }

        [Fact]
        public async Task Shutdown_between_the_two_logs_flushes_each_run_with_its_own_end_block()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "6001") + RunEnd("12:00:12.0000000", 1, "", "Run A failed")
                + RunStart("12:10:10.0000000", "6002") + RunEnd("12:10:12.0000000", 0, "Run B output", ""));
            // The pass is cancelled after AgentExecutor.log; the shutdown flush follows.
            await h.Pass();
            h.Tracker.FlushPendingPlatformScriptResults(h.Now, force: true);

            h.Restart();
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:12.1000000", "Failed")
                + ImeRunStart("12:10:08.0000000") + ImeRunResult("12:10:12.1000000", "Success"));
            await PassAndSettle(h);

            // Each run went out with its own block and IME's verdict on it; the results read after the restart are
            // the same runs and change nothing.
            Assert.Equal(new[] { "Failed|1||Run A failed", "Success|0|Run B output|" }, h.Completed.Select(Shape).ToArray());
            Assert.All(h.Completed, s => Assert.Equal("agentexecutor_fallback", s.ResultSource));
        }

        [Fact]
        public async Task End_block_line_recovered_after_the_next_run_started_stays_with_its_own_run()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "7001"));
            var exitLineAt = h.Length;
            h.Append(RunEnd("12:00:12.0000000", 1, "", "Run A failed")
                + RunStart("12:10:10.0000000", "7002") + RunEnd("12:10:12.0000000", 0, "Run B output", ""));
            await h.Pass();

            // A stale writer rewrites run A's exit line in place: the ledger check re-reads it behind run B's start.
            h.Overwrite(exitLineAt, EntryAt("12:00:12.0000000", "Powershell exit code is 7"));
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddSeconds(2);
            await h.Pass();

            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:12.1000000", "Failed")
                + ImeRunStart("12:10:08.0000000") + ImeRunResult("12:10:12.1000000", "Success"));
            await PassAndSettle(h);

            Assert.Equal(new[] { "Failed|7||Run A failed", "Success|0|Run B output|" }, h.Completed.Select(Shape).ToArray());
        }

        [Fact]
        public async Task Run_without_result_or_end_block_is_dropped_once_a_later_run_has_its_result()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "9001")
                + RunStart("12:10:10.0000000", "9002") + RunEnd("12:10:12.0000000", 0, "Run B output", ""));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunStart("12:10:08.0000000") + ImeRunResult("12:10:12.1000000", "Success"));

            await PassAndSettle(h);

            Assert.Equal(new[] { "Success|0|Run B output|" }, h.Completed.Select(Shape).ToArray());
            Assert.Equal(0, h.Tracker.ParkedPlatformScriptCountForTest);
        }

        [Fact]
        public void Reading_the_same_executor_start_line_again_never_parks_its_run()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            var line = $@"Adding argument powershell with value {ImeDir}\Policies\Scripts\{RetryUser}_{RetryPolicyId}.ps1 to the named argument list.";
            var at = T0.AddMinutes(-30);

            h.Tracker.ProcessLogMessageForTest(line, at, "AgentExecutor.log");
            h.Tracker.ProcessLogMessageForTest(line, at, "AgentExecutor.log");
            Assert.Equal(0, h.Tracker.ParkedPlatformScriptCountForTest);

            h.Tracker.ProcessLogMessageForTest(line, at.AddMinutes(10), "AgentExecutor.log");
            Assert.Equal(1, h.Tracker.ParkedPlatformScriptCountForTest);
        }

        [Fact]
        public async Task Run_without_end_block_keeps_waiting_for_its_result_across_a_shutdown()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "9101") + RunStart("12:40:10.0000000", "9102"));
            await h.Pass();
            h.Tracker.FlushPendingPlatformScriptResults(h.Now, force: true);
            Assert.Empty(h.Completed);

            h.Restart();
            Assert.Equal(1, h.Tracker.ParkedPlatformScriptCountForTest);
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:30:10.5000000", "Failed"));
            await PassAndSettle(h);

            Assert.Equal(new[] { "Failed|-|~|~" }, h.Completed.Select(Shape).ToArray());
        }

        [Fact]
        public async Task Earlier_run_emitted_while_a_later_one_runs_never_takes_the_later_runs_register_entry()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "9201") + RunEnd("12:00:12.0000000", 1, "", "Run A failed")
                + RunStart("12:10:10.0000000", "9202"));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:12.1000000", "Failed") + ImeRunStart("12:10:08.0000000"));
            await PassAndSettle(h);
            Assert.Single(h.Completed);

            // A save of this key is the latest run's, and that one is still running.
            h.Tracker.ScriptRuns.NotePassCompleted(h.Now);
            Assert.Equal(PlatformRunClaim.Wait, h.Tracker.ScriptRuns.TryClaim(RetryUser, RetryPolicyId, h.Now.AddSeconds(-10), out _));

            h.Append(RunEnd("12:10:40.0000000", 0, "Run B output", ""));
            AppendImeLog(h, ImeRunResult("12:10:40.1000000", "Success"));
            await PassAndSettle(h);
            h.Tracker.ScriptRuns.NotePassCompleted(h.Now);

            Assert.Equal(PlatformRunClaim.Claimed, h.Tracker.ScriptRuns.TryClaim(RetryUser, RetryPolicyId, h.Now.AddSeconds(-10), out var run));
            Assert.Equal(h.Completed[1].RunId, run!.RunId);
            Assert.Equal("Run B output", run.Stdout);
        }

        [Fact]
        public async Task Run_of_another_user_keeps_its_own_register_entry_when_it_waited_for_its_result()
        {
            const string otherUser = "2b7e5c91-3d4a-4f80-b6e2-9a1c7d3f5e08";
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "9301") + RunEnd("12:00:12.0000000", 1, "", "Run A failed")
                + RunStart("12:10:10.0000000", "9302", userId: otherUser));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:12.1000000", "Failed")
                + ImeRunStart("12:10:08.0000000", userId: otherUser));
            await PassAndSettle(h);
            h.Tracker.ScriptRuns.NotePassCompleted(h.Now);

            Assert.Equal(PlatformRunClaim.Claimed, h.Tracker.ScriptRuns.TryClaim(RetryUser, RetryPolicyId, h.Now.AddSeconds(-10), out var run));
            Assert.Equal(h.Completed[0].RunId, run!.RunId);
            Assert.Equal(PlatformRunClaim.Wait, h.Tracker.ScriptRuns.TryClaim(otherUser, RetryPolicyId, h.Now.AddSeconds(-10), out _));
        }

        [Fact]
        public async Task Start_line_read_again_stays_a_late_line_after_an_earlier_run_of_another_user_went_out()
        {
            const string otherUser = "2b7e5c91-3d4a-4f80-b6e2-9a1c7d3f5e08";
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(RunStart("12:00:10.0000000", "9401") + RunEnd("12:00:12.0000000", 0, "Run A output", "")
                + RunStart("12:10:10.0000000", "9402", userId: otherUser) + RunEnd("12:10:12.0000000", 0, "Run B output", ""));
            // Run A's result surfaces after run B's (its line was hidden by a concurrent writer).
            AppendImeLog(h, ImeRunResult("12:10:12.1000000", "Success", userId: otherUser) + ImeRunResult("12:00:12.1000000", "Success"));
            await PassAndSettle(h);
            Assert.Equal(new[] { "Success|0|Run B output|", "Success|0|Run A output|" }, h.Completed.Select(Shape).ToArray());

            // Run B's executor start line read again (a rewind): a late line of the emitted run B, never a new run.
            var started = 0;
            h.Tracker.OnScriptStarted = _ => started++;
            h.Tracker.ProcessLogMessageForTest(
                $@"Adding argument powershell with value {ImeDir}\Policies\Scripts\{otherUser}_{RetryPolicyId}.ps1 to the named argument list.",
                h.Completed[0].StartedAtUtc, "AgentExecutor.log");
            Assert.Equal(0, started);
        }

        [Fact]
        public async Task Final_stderr_of_the_end_block_replaces_the_early_snapshot_even_without_stdout()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            // The snapshot was taken before the error reader finished; IME judged the full error file.
            h.Append(RunStart("12:00:10.0000000", "8001") + RunEnd("12:00:12.0000000", 0, "", "Remove-AppxPackage : Cannot process argument", stderrSnapshot: ""));
            AppendImeLog(h, ImeRunStart("12:00:08.0000000") + ImeRunResult("12:00:12.1000000", "Failed"));

            await PassAndSettle(h);

            Assert.Equal(new[] { "Failed|0||Remove-AppxPackage : Cannot process argument" }, h.Completed.Select(Shape).ToArray());
        }
    }
}
