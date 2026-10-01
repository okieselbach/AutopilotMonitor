using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// <see cref="GatherRuleSessionState"/> and <see cref="GatherRuleStatePersistence"/>: what is
    /// written, what a restart restores, what it refuses, and when the file is (not) written.
    /// </summary>
    public sealed class GatherRuleSessionStateTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly TempDirectory _stateDir = new TempDirectory();
        private readonly AgentLogger _logger;

        public GatherRuleSessionStateTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose()
        {
            _tmp.Dispose();
            _stateDir.Dispose();
        }

        private string StateFile => Path.Combine(_stateDir.Path, GatherRuleStatePersistence.FileName);

        private GatherRuleSessionState NewState(string sessionId = "session-a") =>
            new GatherRuleSessionState(sessionId, new GatherRuleStatePersistence(_stateDir.Path, _logger), _logger);

        private static LogFileHead Head(string text) => LogFileHead.FromBytes(Encoding.UTF8.GetBytes(text));

        // ── restore ─────────────────────────────────────────────────────────

        [Fact]
        public void Positions_and_emitted_hashes_survive_a_restart_of_the_same_session()
        {
            var first = NewState();
            first.CommitLogPosition("RULE-1", @"C:\Logs\a.log", 120, 7, Head("2026-09-28 a.log start"), heldTailStart: -1);
            first.CommitEmittedHash("RULE-2", "hash-value");

            var second = NewState();

            var position = second.PeekLogPosition("RULE-1", @"C:\Logs\a.log");
            Assert.NotNull(position);
            Assert.Equal(120, position!.Position);
            Assert.Equal(7, position.LineNumber);
            Assert.True(position.FromPreviousRun);
            Assert.Equal("hash-value", second.GetEmittedHashes()["RULE-2"]);
            Assert.StartsWith("restored 1 logparser position(s) for 1 rule(s) and 1 on_change value(s)", second.RestoreSummary);
        }

        [Fact]
        public void State_of_another_session_is_ignored()
        {
            NewState("session-a").CommitLogPosition("RULE-1", @"C:\Logs\a.log", 120, 7, Head("start"), -1);

            var rotated = NewState("session-b");

            Assert.Null(rotated.PeekLogPosition("RULE-1", @"C:\Logs\a.log"));
            Assert.Contains("saved for session session-a, current session is session-b", rotated.RestoreSummary);
        }

        [Fact]
        public void Missing_file_is_a_first_run_and_corrupt_file_a_fresh_start()
        {
            Assert.StartsWith("no saved state", NewState().RestoreSummary);

            File.WriteAllText(StateFile, "not json {{{");
            var afterCorruption = NewState();

            Assert.StartsWith("saved state discarded", afterCorruption.RestoreSummary);
            Assert.Empty(afterCorruption.GetEmittedHashes());
        }

        [Fact]
        public void Tampered_entries_are_dropped_and_the_rest_restored()
        {
            var data = new GatherRuleStateData
            {
                SessionId = "session-a",
                SavedAtUtc = DateTime.UtcNow,
                LogPositions = new List<GatherLogPositionData>
                {
                    new GatherLogPositionData { RuleId = "OK", Path = @"C:\a.log", Position = 10, LineNumber = 1, HeadLength = 10, HeadHash = "h" },
                    new GatherLogPositionData { RuleId = "NEG", Path = @"C:\b.log", Position = -5, HeadLength = 10, HeadHash = "h" },
                    new GatherLogPositionData { RuleId = "", Path = @"C:\c.log", Position = 1, HeadLength = 1, HeadHash = "h" },
                    new GatherLogPositionData { RuleId = "BIGHEAD", Path = @"C:\d.log", Position = 1, HeadLength = 5000, HeadHash = "h" },
                    new GatherLogPositionData { RuleId = "NOHEAD", Path = @"C:\e.log", Position = 50, HeadLength = 0 },
                },
                OnChangeHashes = new Dictionary<string, string> { ["GOOD"] = "x", ["EMPTY"] = "" },
            };
            File.WriteAllText(StateFile, JsonConvert.SerializeObject(data));

            var state = NewState();

            Assert.NotNull(state.PeekLogPosition("OK", @"C:\a.log"));
            Assert.Null(state.PeekLogPosition("NEG", @"C:\b.log"));
            Assert.Null(state.PeekLogPosition("BIGHEAD", @"C:\d.log"));
            Assert.Null(state.PeekLogPosition("NOHEAD", @"C:\e.log"));
            Assert.Equal(new[] { "GOOD" }, state.GetEmittedHashes().Keys.ToArray());
            Assert.Contains("5 invalid entries dropped", state.RestoreSummary);
        }

        [Fact]
        public void A_path_no_real_file_can_have_is_dropped_and_never_stops_the_executor()
        {
            // Path APIs throw on these characters under .NET Framework; a throw here would keep
            // every collector host down at every restart of the session.
            var data = new GatherRuleStateData
            {
                SessionId = "session-a",
                LogPositions = new List<GatherLogPositionData>
                {
                    new GatherLogPositionData { RuleId = "BAD", Path = "C:\\Logs\\x.|og", Position = 1, HeadLength = 1, HeadHash = "h" },
                    new GatherLogPositionData { RuleId = "WILD", Path = "C:\\Logs\\*.log", Position = 1, HeadLength = 1, HeadHash = "h" },
                    new GatherLogPositionData { RuleId = "OK", Path = "C:\\Logs\\ok.log", Position = 1, HeadLength = 1, HeadHash = "h" },
                },
            };
            File.WriteAllText(StateFile, JsonConvert.SerializeObject(data));

            var state = NewState();

            Assert.NotNull(state.PeekLogPosition("OK", "C:\\Logs\\ok.log"));
            Assert.Null(state.PeekLogPosition("BAD", "C:\\Logs\\x.|og"));
            Assert.Contains("2 invalid entries dropped", state.RestoreSummary);
        }

        // ── where reading starts ────────────────────────────────────────────

        [Fact]
        public void Reading_continues_while_the_file_head_still_matches()
        {
            var state = NewState();
            state.CommitLogPosition("R", @"C:\a.log", 30, 3, Head("2026-09-28 first line\n"), -1);

            var start = GatherRuleSessionState.ResolveStart(state.PeekLogPosition("R", @"C:\a.log"), 200,
                Head("2026-09-28 first line\nsecond line\nthird"), out var how);

            Assert.Equal(30, start);
            Assert.Equal(LogReadStart.Continue, how);
        }

        [Fact]
        public void A_shrunk_file_and_a_replaced_file_are_read_from_the_beginning()
        {
            var state = NewState();
            state.CommitLogPosition("R", @"C:\a.log", 30, 3, Head("2026-09-28 first line\n"), -1);
            var known = state.PeekLogPosition("R", @"C:\a.log");

            Assert.Equal(0, GatherRuleSessionState.ResolveStart(known, 20, Head("2026-09-28 fir"), out var shrunk));
            Assert.Equal(LogReadStart.Shrunk, shrunk);

            // Replaced and already longer than the old position: only the head tells.
            Assert.Equal(0, GatherRuleSessionState.ResolveStart(known, 500, Head("2026-09-30 a new log entirely, much longer"), out var replaced));
            Assert.Equal(LogReadStart.Replaced, replaced);

            // Shorter than the recorded head although not shorter than the position.
            state.CommitLogPosition("S", @"C:\b.log", 5, 1, Head("0123456789abcdef"), -1);
            Assert.Equal(0, GatherRuleSessionState.ResolveStart(state.PeekLogPosition("S", @"C:\b.log"), 8, Head("01234567"), out var cut));
            Assert.Equal(LogReadStart.Replaced, cut);
        }

        [Fact]
        public void Positions_are_kept_per_rule_for_the_same_file()
        {
            var state = NewState();
            state.CommitLogPosition("RULE-A", @"C:\shared.log", 100, 10, Head("x"), -1);

            Assert.Null(state.PeekLogPosition("RULE-B", @"C:\shared.log"));
            Assert.NotNull(state.PeekLogPosition("RULE-A", @"C:\SHARED.LOG")); // paths compare case-insensitively
        }

        // ── when the file is written ────────────────────────────────────────

        [Fact]
        public void An_unchanged_commit_does_not_write_the_file()
        {
            var state = NewState();
            state.CommitLogPosition("R", @"C:\a.log", 10, 1, Head("head"), -1);
            File.Delete(StateFile);

            state.CommitLogPosition("R", @"C:\a.log", 10, 1, Head("head"), -1);
            state.CommitEmittedHash("H", "v");
            File.Delete(StateFile);
            state.CommitEmittedHash("H", "v");

            Assert.False(File.Exists(StateFile));
        }

        [Fact]
        public void A_failed_save_is_retried_at_the_next_commit()
        {
            var blocked = Path.Combine(_stateDir.Path, "blocked");
            File.WriteAllText(blocked, "a file where the state directory should be");
            var state = new GatherRuleSessionState("session-a", new GatherRuleStatePersistence(blocked, _logger), _logger);

            state.CommitEmittedHash("H", "v");   // Directory.CreateDirectory fails
            File.Delete(blocked);
            state.CommitEmittedHash("H", "v");   // unchanged, but the earlier save is still owed

            Assert.True(File.Exists(Path.Combine(blocked, GatherRuleStatePersistence.FileName)));
        }

        [Fact]
        public void A_closed_state_no_longer_writes()
        {
            var state = NewState();
            state.Close();

            state.CommitEmittedHash("H", "v");

            Assert.False(File.Exists(StateFile));
        }

        [Fact]
        public void Without_persistence_nothing_is_written()
        {
            var state = new GatherRuleSessionState("session-a", null, _logger);

            state.CommitLogPosition("R", @"C:\a.log", 10, 1, Head("head"), -1);

            Assert.StartsWith("persistence off", state.RestoreSummary);
            Assert.Empty(Directory.GetFiles(_stateDir.Path));
        }

        // ── bounds and concurrency ──────────────────────────────────────────

        [Fact]
        public void A_rule_forgets_its_least_recently_touched_file_beyond_the_cap()
        {
            var state = NewState();
            for (var i = 0; i < GatherRuleSessionState.MaxLogFilesPerRule; i++)
                state.CommitLogPosition("R", $@"C:\logs\{i}.log", 1, 1, Head("h"), -1);
            state.PeekLogPosition("R", @"C:\logs\0.log");   // touched again — survives

            state.CommitLogPosition("R", @"C:\logs\new.log", 1, 1, Head("h"), -1);

            Assert.NotNull(state.PeekLogPosition("R", @"C:\logs\0.log"));
            Assert.Null(state.PeekLogPosition("R", @"C:\logs\1.log"));
            Assert.NotNull(state.PeekLogPosition("R", @"C:\logs\new.log"));
        }

        [Fact]
        public void Parallel_commits_leave_a_consistent_file()
        {
            var state = NewState();

            Parallel.For(0, 8, worker =>
            {
                for (var i = 0; i < 50; i++)
                {
                    state.CommitLogPosition($"RULE-{worker}", $@"C:\logs\{worker}.log", i + 1, i, Head("h"), -1);
                    state.PeekLogPosition($"RULE-{worker}", $@"C:\logs\{worker}.log");
                    state.CommitEmittedHash($"RULE-{worker}", "hash-" + i);
                }
            });

            var restored = NewState();
            for (var worker = 0; worker < 8; worker++)
            {
                Assert.Equal(50, restored.PeekLogPosition($"RULE-{worker}", $@"C:\logs\{worker}.log")!.Position);
                Assert.Equal("hash-49", restored.GetEmittedHashes()[$"RULE-{worker}"]);
            }
        }
    }
}
