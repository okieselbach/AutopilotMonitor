using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Session 756970cf: IME launched a Win32 detection executor and a platform-script executor 15 ms apart, and both
    /// opened AgentExecutor.log at the same end-of-file offset. The platform executor's shorter start block overwrote
    /// the detection executor's, whose identity line the tracker never saw; the detection's end block (exit 1 and its
    /// stdout), written at its own position behind its own start block, then sat behind the PLATFORM start block, and
    /// "the nearest preceding marker owns the line" handed another script's output and exit code to the platform
    /// script. These tests pin the writer boundary: an entry that is not exactly one CMTrace record is where one
    /// writer's bytes end and another's begin, and nothing behind it belongs to the invocation in front of it.
    /// </summary>
    public sealed partial class ImeLogTrackerOverwriteRewindTests
    {
        private const string RaceUserId = "00000000-0000-0000-0000-000000000000";
        private const string RacePlatformId = "1d6f0a52-3c4e-4b7a-9e21-5f8c7d2b9a10";
        private const string RaceDetectionAppId = "8b2e4c71-6a9d-4f03-b5e8-2c7a1d9f4e36";
        private const string RaceFillerAppId = "5e1c2b7d-0a4f-4c8e-9d36-7b2f1e8a4c50";
        private const string ImeDir = @"C:\Program Files (x86)\Microsoft Intune Management Extension";
        private const string DetectionStdout = "VERBOSE: === Sample Suite Detection Started ===\r\nWARNING: === DETECTION FAILED: Sample Suite is not installed ===";
        private const string PlatformStdout = "=== bootstrap loader started ===\r\nInstaller finished (exit code 0).";

        private static string EntryAt(string time, string message)
            => $"<![LOG[{message}]LOG]!><time=\"{time}\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n";

        /// <summary>The four lines every AgentExecutor invocation starts with — identical length for every executor.</summary>
        private static string ExecutorBanner(string time)
            => EntryAt(time, "ExecutorLog AgentExecutor gets invoked")
            + EntryAt(time, "Creating command line parser, name delimiter is - and value separator is  .")
            + EntryAt(time, "Getting Ordered Parameters")
            + EntryAt(time, "Parsing Ordered Parameters.");

        private static string ExecutorLaunch(string time, string scriptPath, string pid)
            => EntryAt(time, "Prepare to run Powershell Script ..")
            + EntryAt(time, $"cmd line for running powershell is -NoProfile -executionPolicy bypass -file  \"{scriptPath}\" ")
            + EntryAt(time, "runAs32BitOn64 = False, so Disable Wow64FsRedirection")
            + EntryAt(time, @"PowerShell path is C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")
            + EntryAt(time, $"[Executor] created powershell with process id {pid}");

        private static string PlatformStartBlock(string time, string policyId, string pid)
        {
            var path = $@"{ImeDir}\Policies\Scripts\{RaceUserId}_{policyId}.ps1";
            var results = $@"{ImeDir}\Policies\Results\{RaceUserId}_{policyId}";
            return ExecutorBanner(time)
                + EntryAt(time, $"Adding argument powershell with value {path} to the named argument list.")
                + EntryAt(time, "Powershell option gets invoked")
                + EntryAt(time, path)
                + EntryAt(time, results + ".output")
                + EntryAt(time, results + ".error")
                + EntryAt(time, results + ".timeout")
                + ExecutorLaunch(time, path, pid);
        }

        private static string DetectionStartBlock(string time, string appId, string pid)
        {
            var path = $@"{ImeDir}\Content\DetectionScripts\{appId}_2.ps1";
            return ExecutorBanner(time)
                + EntryAt(time, $"Adding argument powershellDetection with value {path} to the named argument list.")
                + EntryAt(time, "PowershellDetection option gets invoked")
                + EntryAt(time, path)
                + EntryAt(time, path + "quotedResultFilePath.txt")
                + EntryAt(time, path + "quotedErrorFilePath.txt")
                + EntryAt(time, path + "quotedTimeoutFilePath.txt")
                + EntryAt(time, path + "quotedExitCodeFilePath.txt")
                + ExecutorLaunch(time, path, pid);
        }

        private static string ExecutorEndBlock(string time, int exitCode, string stdout)
            => EntryAt(time, $"Powershell exit code is {exitCode}")
            + EntryAt(time, $"length of out={stdout.Length}")
            + EntryAt(time, "length of error=0")
            + EntryAt(time, "error from script =")
            + EntryAt(time, exitCode == 0 ? "Powershell script is successfully executed." : "Powershell script is failed to execute")
            + EntryAt(time, $"write output done. output = {stdout}, error = ")
            + EntryAt(time, "Revert Wow64FsRedirection")
            + EntryAt(time, "Agent executor completed.");

        private static string PlatformResultLine(string policyId)
            => $"[PowerShell] User Id = {RaceUserId}, Policy id = {policyId}, policy result = Success";

        /// <summary>The file offsets at which the entries of an ASCII <paramref name="block"/> start when it is written at <paramref name="at"/>.</summary>
        private static HashSet<long> EntryStarts(string block, long at)
        {
            Assert.True(block.All(c => c < 128), "offset arithmetic assumes ASCII test content");
            var starts = new HashSet<long>();
            for (var i = block.IndexOf("<![LOG[", StringComparison.Ordinal); i >= 0; i = block.IndexOf("<![LOG[", i + 1, StringComparison.Ordinal))
                starts.Add(at + i);
            return starts;
        }

        [Fact]
        public async Task Executor_that_opened_at_the_same_offset_never_lends_its_end_block_to_the_platform_script()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());

            // An earlier, finished detection run: the race starts mid-file like in the field log.
            h.Append(DetectionStartBlock("12:00:00.0000000", RaceFillerAppId, "1548") + ExecutorEndBlock("12:00:00.5000000", 0, "detected"));
            await h.Pass();
            var x = h.Length;

            // Detection executor C and platform executor D open the file at the same end x. C writes first; the tracker
            // reads C's banner, then D writes its own start block over C's.
            var cStart = DetectionStartBlock("12:00:01.0000000", RaceDetectionAppId, "6416");
            var dStart = PlatformStartBlock("12:00:01.2000000", RacePlatformId, "2524");
            var cHead = cStart.Substring(0, cStart.IndexOf("<![LOG[Getting Ordered Parameters", StringComparison.Ordinal));
            h.Append(cHead);
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            h.Append(cStart.Substring(cHead.Length));
            h.Overwrite(x, dStart);

            // The field geometry: D's start block is shorter and ends inside one of C's lines, so the tail of that line
            // sits between D's start block and C's remaining bytes.
            Assert.True(dStart.Length < cStart.Length);
            Assert.DoesNotContain(x + dStart.Length, EntryStarts(cStart, x));
            h.Now = h.Now.AddMilliseconds(150);
            await h.Pass();
            Assert.True(h.Health.OverwriteRewinds >= 1);

            // C ends: its end block lands at its own position, right behind its own (overwritten) start block.
            Assert.Equal(x + cStart.Length, h.Length);
            var cEnd = ExecutorEndBlock("12:00:07.0000000", 1, DetectionStdout);
            h.Append(cEnd);
            h.Now = h.Now.AddSeconds(6);
            await h.Pass();

            // IME's result for D reaches the tracker before D's own end block: the completion must wait for it.
            h.Tracker.ProcessLogMessageForTest(PlatformResultLine(RacePlatformId), sourceFileName: "IntuneManagementExtension.log");
            Assert.Empty(h.Completed);

            // D ends: its end block lands at ITS position, over the fragment and C's bytes; C's last line survives.
            var dEnd = ExecutorEndBlock("12:00:09.5000000", 0, PlatformStdout);
            Assert.True(dEnd.Length < cStart.Length - dStart.Length + cEnd.Length);
            h.Overwrite(x + dStart.Length, dEnd);
            h.Now = h.Now.AddMilliseconds(130);
            await h.Pass();
            h.Tracker.FlushPendingPlatformScriptResults(h.Now);

            var script = Assert.Single(h.Completed);
            Assert.Equal(RacePlatformId, script.PolicyId);
            Assert.Equal("Success", script.Result);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal(PlatformStdout.Replace("\r\n", "\n"), script.Stdout);
            Assert.DoesNotContain(h.Completed, s => (s.Stdout ?? string.Empty).Contains("Detection"));
        }

        /// <summary>
        /// The mirror image: a foreign executor whose stream position lies inside the platform start block (it opened
        /// at the same end with a shorter start block) writes its end block there. Its first line merges with the head of
        /// a platform line — cut inside the message or inside the trailer, which the parser would otherwise read up to
        /// the foreign trailer — and its output line follows without any marker in between.
        /// </summary>
        [Theory]
        [InlineData(15)]
        [InlineData(-30)]
        public async Task Foreign_end_block_merged_into_the_platform_start_block_never_reaches_the_platform_script(int cut)
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(DetectionStartBlock("12:00:00.0000000", RaceFillerAppId, "1548") + ExecutorEndBlock("12:00:00.5000000", 0, "detected"));
            await h.Pass();
            var x = h.Length;

            var dStart = PlatformStartBlock("12:00:01.0000000", RacePlatformId, "2524");
            h.Append(dStart);
            await h.Pass();

            // The foreign end block starts inside the line after D's argument line.
            var lineAfterArgument = x + dStart.IndexOf("<![LOG[Powershell option gets invoked", StringComparison.Ordinal);
            var lineLength = EntryAt("12:00:01.0000000", "Powershell option gets invoked").Length;
            var at = lineAfterArgument + (cut > 0 ? cut : lineLength + cut);
            var foreignEnd = ExecutorEndBlock("12:00:03.0000000", 1, "foreign output");
            Assert.True(at + foreignEnd.Length < x + dStart.Length, "the foreign block must end inside D's start block");
            h.Overwrite(at, foreignEnd);
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            h.Tracker.ProcessLogMessageForTest(PlatformResultLine(RacePlatformId), sourceFileName: "IntuneManagementExtension.log");
            h.Now = h.Now.Add(ImeLogTracker.PlatformScriptEndBlockGrace);
            h.Tracker.FlushPendingPlatformScriptResults(h.Now);

            // No owner is provable behind the cut: the completion goes out without end-block data, never with the foreign one.
            var script = Assert.Single(h.Completed);
            Assert.Equal("Success", script.Result);
            Assert.Null(script.ExitCode);
            Assert.Null(script.Stdout);
        }

        [Fact]
        public async Task Entry_read_unchanged_after_a_rewind_keeps_its_platform_marker()
        {
            const string platformId = "2b7d9e41-8c3a-4f60-a5d2-9e1b7c4f3a08";
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());

            // Detection W waits for its script; detection X runs completely behind it; platform F starts behind X.
            var wStart = DetectionStartBlock("12:00:00.0000000", RaceFillerAppId, "1111");
            var xRun = DetectionStartBlock("12:00:00.2000000", RaceDetectionAppId, "2222") + ExecutorEndBlock("12:00:00.4000000", 0, "x detected");
            h.Append(wStart + xRun + PlatformStartBlock("12:00:01.0000000", platformId, "3333"));
            await h.Pass();

            // W ends at its own position, over the head of X's run; F's start block keeps its bytes and offset.
            var wEnd = ExecutorEndBlock("12:00:02.0000000", 0, "w detected");
            Assert.True(wEnd.Length < xRun.Length);
            h.Overwrite(wStart.Length, wEnd);
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(1, h.Health.OverwriteRewinds);

            // F's end block lands behind its start block: still F's, because F's marker survived with its bytes.
            h.Append(ExecutorEndBlock("12:00:05.0000000", 7, "Hello from F"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            h.Tracker.ProcessLogMessageForTest(PlatformResultLine(platformId), sourceFileName: "IntuneManagementExtension.log");

            var script = Assert.Single(h.Completed);
            Assert.Equal(platformId, script.PolicyId);
            Assert.Equal(7, script.ExitCode);
            Assert.Equal("Hello from F", script.Stdout);
        }

        [Fact]
        public async Task Writer_boundary_read_unchanged_after_a_rewind_still_closes_the_platform_invocation()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());

            // Detection Q waits for its script while detection R runs completely behind it.
            var qStart = DetectionStartBlock("12:00:00.0000000", RaceFillerAppId, "1111");
            var rRun = DetectionStartBlock("12:00:00.2000000", "6c3f8a20-1d5e-4b97-8f4a-0e2d6b9c7a13", "2222") + ExecutorEndBlock("12:00:00.4000000", 0, "r detected");
            h.Append(qStart + rRun);
            await h.Pass();
            var x = h.Length;

            // The race of the field case behind them: the boundary closes D.
            var cStart = DetectionStartBlock("12:00:01.0000000", RaceDetectionAppId, "6416");
            var dStart = PlatformStartBlock("12:00:01.2000000", RacePlatformId, "2524");
            h.Append(cStart);
            h.Overwrite(x, dStart);
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            // Q ends at its own position, over R's run: the rewind starts in front of the race, whose bytes read unchanged.
            var qEnd = ExecutorEndBlock("12:00:02.0000000", 0, "q detected");
            Assert.True(qEnd.Length < rRun.Length);
            h.Overwrite(qStart.Length, qEnd);
            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            // C's end block behind the boundary stays unowned; D's own end block completes D.
            var cEnd = ExecutorEndBlock("12:00:07.0000000", 1, DetectionStdout);
            h.Append(cEnd);
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            h.Tracker.ProcessLogMessageForTest(PlatformResultLine(RacePlatformId), sourceFileName: "IntuneManagementExtension.log");
            Assert.Empty(h.Completed);

            h.Overwrite(x + dStart.Length, ExecutorEndBlock("12:00:09.5000000", 0, PlatformStdout));
            h.Now = h.Now.AddMilliseconds(130);
            await h.Pass();
            h.Tracker.FlushPendingPlatformScriptResults(h.Now);

            var script = Assert.Single(h.Completed);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal(PlatformStdout.Replace("\r\n", "\n"), script.Stdout);
        }

        [Fact]
        public async Task Raw_text_in_the_executor_log_is_never_matched_and_closes_the_platform_invocation()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(PlatformStartBlock("12:00:00.0000000", RacePlatformId, "2524"));
            await h.Pass();

            // The tail of a cut line that happens to read like an exit line, then a foreign end block.
            h.Append("Powershell exit code is 5\r\n" + ExecutorEndBlock("12:00:02.0000000", 6, "foreign output"));
            // A fragment right at the bookmark stops the first pass for a check; the second one reads it.
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            h.Tracker.ProcessLogMessageForTest(PlatformResultLine(RacePlatformId), sourceFileName: "IntuneManagementExtension.log");
            h.Now = h.Now.Add(ImeLogTracker.PlatformScriptEndBlockGrace);
            h.Tracker.FlushPendingPlatformScriptResults(h.Now);

            var script = Assert.Single(h.Completed);
            Assert.Null(script.ExitCode);
            Assert.Null(script.Stdout);
        }

        [Fact]
        public async Task Fragment_in_the_ime_log_does_not_close_the_platform_run_context()
        {
            using var h = new Harness("IntuneManagementExtension.log", ScriptPatterns());
            // In the IME log the service writes the platform marker and the context line itself; the fragment in
            // between is the tail of an executor's telemetry line and separates nothing.
            h.Append(Entry(@"Script file C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\" + RaceUserId + "_" + RacePlatformId + ".ps1 is generated.", "17")
                + "ing mananger...]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n"
                + Entry("Launch powershell executor in user session", "17"));
            await h.Pass();

            h.Tracker.ProcessLogMessageForTest(PlatformResultLine(RacePlatformId));
            h.Now = h.Now.Add(ImeLogTracker.PlatformScriptEndBlockGrace);
            h.Tracker.FlushPendingPlatformScriptResults(h.Now);

            var script = Assert.Single(h.Completed);
            Assert.Equal("User", script.RunContext);
        }

        [Fact]
        public async Task Cut_multi_line_output_adds_one_boundary_not_one_per_line()
        {
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(PlatformStartBlock("12:00:00.0000000", RacePlatformId, "2524"));
            await h.Pass();
            var markersBefore = h.Tracker.InvocationMarkerCountForTest("AgentExecutor.log");

            var lines = string.Concat(Enumerable.Range(1, 600).Select(i => $"orphaned output line {i}\r\n"));
            h.Append(lines);
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(markersBefore + 1, h.Tracker.InvocationMarkerCountForTest("AgentExecutor.log"));
        }

        [Theory]
        [InlineData("<![LOG[Powershell exit code is 0]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">", true)]
        [InlineData("<![LOG[write output done. output = line one\nline two, error = ]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">", true)]
        [InlineData("\uFEFF<![LOG[ExecutorLog AgentExecutor gets invoked]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">", true)]
        [InlineData("t code is 3]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">", false)]
        [InlineData("", false)]
        [InlineData("<![LOG[Powe<![LOG[Powershell exit code is 1]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">", false)]
        [InlineData("<![LOG[Powershell option gets invoked]LOG]!><time=\"12:00:00.0000000\" da<![LOG[Powershell exit code is 1]LOG]!><time=\"12:00:01.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">", false)]
        [InlineData("<![LOG[write output done. output = line one\nline<![LOG[Agent executor completed.]LOG]!><time=\"12:00:00.0000000\" date=\"9-10-2026\" component=\"AgentExecutor\" context=\"\" type=\"1\" thread=\"1\" file=\"\">", false)]
        public void Single_cmtrace_record_is_told_apart_from_the_cut_of_two_writers(string text, bool expected)
        {
            Assert.Equal(expected, ImeLogTracker.IsSingleCmTraceRecord(text));
        }

        [Theory]
        [InlineData("AgentExecutor.log", true)]
        [InlineData("AgentExecutor-20261003-233348.log", true)]
        [InlineData("IntuneManagementExtension.log", false)]
        [InlineData("IntuneManagementExtension-20261003-233348.log", false)]
        [InlineData("HealthScripts.log", false)]
        [InlineData("AppWorkload.log", false)]
        public void Writer_boundaries_apply_to_the_executor_log_live_and_rotated(string fileName, bool expected)
        {
            Assert.Equal(expected, ImeLogTracker.IsWriterBoundaryLogFile(fileName));
        }
    }
}
