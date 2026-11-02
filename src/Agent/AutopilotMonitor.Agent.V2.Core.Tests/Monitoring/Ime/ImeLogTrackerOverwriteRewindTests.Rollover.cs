using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// IME rolls a log with File.Move at 3 MB. The tracker used to take the archive for a new file and
    /// read it from 0: every app line played again, finished apps fell back to Installing and the
    /// subgraph line of an older app marked the running app as skipped (session 190fed00).
    /// </summary>
    public sealed partial class ImeLogTrackerOverwriteRewindTests
    {
        private const string FirstStamp = "20260910-193000";
        private const string SecondStamp = "20260910-193500";

        private static string Folder(Harness h) => Path.GetDirectoryName(h.LogPath)!;

        private static string ArchivePath(Harness h, string stamp = FirstStamp)
            => Path.Combine(Folder(h), $"{Path.GetFileNameWithoutExtension(h.LogName)}-{stamp}.log");

        private static string Roll(Harness h, string stamp = FirstStamp)
        {
            var archive = ArchivePath(h, stamp);
            File.Move(h.LogPath, archive);
            return archive;
        }

        private static List<ImeLogPattern> AppPatterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern
            {
                PatternId = "T-POLICIES", Category = "always", Enabled = true,
                Pattern = @"^policies discovered = (?<policies>\[.*\])", Action = "policiesDiscovered",
                Parameters = new Dictionary<string, string>(),
            },
            new ImeLogPattern
            {
                PatternId = "T-SUBGRAPH", Category = "always", Enabled = true,
                Pattern = @"^subgraph (?<id>[\w-]+)", Action = "cancelStuckAndSetCurrent",
                Parameters = new Dictionary<string, string>(),
            },
            new ImeLogPattern
            {
                PatternId = "T-INSTALLING", Category = "always", Enabled = true,
                Pattern = @"^installing (?<id>[\w-]+)", Action = "updateStateInstalling",
                Parameters = new Dictionary<string, string>(),
            },
            new ImeLogPattern
            {
                PatternId = "T-INSTALLED", Category = "always", Enabled = true,
                Pattern = @"^installed (?<id>[\w-]+)", Action = "updateStateInstalled",
                Parameters = new Dictionary<string, string>(),
            },
        };

        private const string AppPoliciesLine = @"policies discovered = [{""Id"":""app-1"",""Name"":""App One""},{""Id"":""app-2"",""Name"":""App Two""}]";

        private static string OneFinishedOneRunning() =>
            Entry(AppPoliciesLine) + Entry("subgraph app-1") + Entry("installing app-1") + Entry("installed app-1")
            + Entry("subgraph app-2") + Entry("installing app-2");

        [Theory]
        [InlineData("AppWorkload.log")]
        [InlineData("AppActionProcessor.log")]
        [InlineData("HealthScripts.log")]
        [InlineData("AgentExecutor.log")]
        [InlineData("IntuneManagementExtension.log")]
        public async Task Rolled_over_log_is_not_read_again(string logName)
        {
            using var h = new Harness(logName, MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3"));
            await h.Pass();

            Roll(h);
            h.Append(Entry("marker 4"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 4 }, h.Matched());

            h.Tracker.RequestOverwriteCheckForTest();
            h.Now = h.Now.AddSeconds(2);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 4 }, h.Matched());
            Assert.Equal(0, h.Health.OverwriteRewinds);
        }

        [Fact]
        public async Task Bytes_written_after_the_last_read_come_from_the_archive_once_and_first()
        {
            using var h = new Harness("AppWorkload.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2"));
            await h.Pass();

            h.Append(Entry("marker 3"));
            Roll(h);
            h.Append(Entry("marker 4"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 4 }, h.Matched());
        }

        [Fact]
        public async Task Line_held_back_at_the_end_of_the_file_is_finished_from_the_archive()
        {
            using var h = new Harness("AppWorkload.log", MarkerPatterns());
            var second = Entry("marker 2");
            h.Append(Entry("marker 1") + second.Substring(0, 10));
            await h.Pass();
            Assert.Equal(new[] { 1 }, h.Matched());

            h.Append(second.Substring(10));
            Roll(h);
            h.Append(Entry("marker 3"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3 }, h.Matched());
        }

        [Fact]
        public async Task Rollover_leaves_finished_and_running_apps_alone()
        {
            using var h = new Harness("AppWorkload.log", AppPatterns());
            var transitions = new List<string>();
            h.Tracker.OnAppStateChanged = (pkg, from, to) => transitions.Add($"{pkg.Id}:{from}->{to}");
            h.Append(OneFinishedOneRunning());
            await h.Pass();
            var before = transitions.ToList();

            Roll(h);
            h.Append(Entry("marker unrelated"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Equal(before, transitions);
            var states = h.Tracker.GetAllKnownPackageStates().ToDictionary(p => p.Id, p => p.InstallationState);
            Assert.Equal(AppInstallationState.Installed, states["app-1"]);
            Assert.Equal(AppInstallationState.Installing, states["app-2"]);
        }

        [Fact]
        public async Task Without_a_readable_identity_the_archive_is_read_again_as_before()
        {
            // The fallback, and the proof that the tests above catch the replay: this is what they saw before the fix.
            using var h = new Harness("AppWorkload.log", AppPatterns());
            h.Tracker.FileIdentityProvider = _ => null!;
            var transitions = new List<string>();
            h.Tracker.OnAppStateChanged = (pkg, from, to) => transitions.Add($"{pkg.Id}:{from}->{to}");
            h.Append(OneFinishedOneRunning());
            await h.Pass();

            Roll(h);
            h.Append(Entry("marker unrelated"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            Assert.Contains("app-2:Installing->Skipped", transitions);
            Assert.Contains("app-1:Installed->Installing", transitions);
        }

        [Fact]
        public async Task Live_file_that_sorts_before_its_archive_hands_over_its_bookmark()
        {
            // LogMaxHistory <= 1: the archive is _X.log, which sorts behind X.log. The pass reaches the
            // shrunken live file first, and the next rollover replaces the archive under the same name.
            using var h = new Harness("IntuneManagementExtension.log", MarkerPatterns());
            var archive = Path.Combine(Folder(h), "_IntuneManagementExtension.log");
            h.Append(Entry("marker 1") + Entry("marker 2") + Entry("marker 3") + Entry("marker 4"));
            await h.Pass();

            File.Move(h.LogPath, archive);
            h.Append(Entry("marker 5"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, h.Matched());

            h.Append(Entry("marker 6"));
            File.Delete(archive);
            File.Move(h.LogPath, archive);
            h.Append(Entry("marker 8"));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 8 }, h.Matched());
        }

        [Fact]
        public async Task Rollover_during_a_restart_with_a_longer_new_file_loses_and_repeats_nothing()
        {
            using var h = new Harness("AppWorkload.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2"));
            await h.Pass();

            h.Restart();
            h.Append(Entry("marker 3"));
            Roll(h);
            h.Append(Entry("marker 4") + Entry("marker 5") + Entry("marker 6") + Entry("marker 7"));
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, h.Matched());
        }

        [Fact]
        public async Task Two_rollovers_during_a_restart_with_the_bookmarked_file_deleted_lose_nothing()
        {
            using var h = new Harness("AppWorkload.log", MarkerPatterns());
            h.Append(Entry("marker 1") + Entry("marker 2"));
            await h.Pass();

            h.Restart();
            var first = Roll(h, FirstStamp);
            h.Append(Entry("marker 3"));
            Roll(h, SecondStamp);
            h.Append(Entry("marker 4") + Entry("marker 5") + Entry("marker 6"));
            File.Delete(first);
            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, h.Matched());
        }

        [Fact]
        public async Task Archives_found_at_the_first_start_are_read_from_the_beginning_in_order()
        {
            using var h = new Harness("AppWorkload.log", MarkerPatterns());
            File.WriteAllText(ArchivePath(h, FirstStamp), Entry("marker 1") + Entry("marker 2"));
            File.WriteAllText(ArchivePath(h, SecondStamp), Entry("marker 3"));
            h.Append(Entry("marker 4"));

            await h.Pass();

            Assert.Equal(new[] { 1, 2, 3, 4 }, h.Matched());
        }

        [Fact]
        public async Task Platform_script_in_flight_keeps_its_end_block_across_a_rollover()
        {
            // Reading the archive again used to restore the marker of the run in flight by accident.
            using var h = new Harness("AgentExecutor.log", ScriptPatterns());
            h.Append(Entry("ExecutorLog AgentExecutor gets invoked") + Entry(PlatformStartLine) + Entry("[Executor] created powershell with process id 12708"));
            await h.Pass();

            Roll(h);
            h.Append(Entry("Powershell exit code is 0") + Entry("write output done. output = Hello from E, error = ") + Entry("Agent executor completed."));
            h.Now = h.Now.AddMilliseconds(100);
            await h.Pass();

            h.Tracker.ProcessLogMessageForTest(ResultLine);
            var script = Assert.Single(h.Completed);
            Assert.Equal(PlatformId, script.PolicyId);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal("Hello from E", script.Stdout);
        }

        [Fact]
        public void Identity_of_a_real_file_survives_a_rename_and_differs_from_the_next_file()
        {
            using var h = new Harness("AppWorkload.log", MarkerPatterns());
            h.Append(Entry("marker 1"));
            var before = ImeLogFileIdentity.TryRead(h.LogPath);
            Assert.NotNull(before);

            var archive = Roll(h);
            h.Append(Entry("marker 2"));

            Assert.Equal(before, ImeLogFileIdentity.TryRead(archive));
            Assert.NotEqual(before, ImeLogFileIdentity.TryRead(h.LogPath));
            Assert.Null(ImeLogFileIdentity.TryRead(archive + ".missing"));
        }
    }
}
