using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// The IME result waits for its executor end block (session e7f3c910): PS-SCRIPT-RESULT can
    /// reach the tracker before the AgentExecutor.log end block of the same run — an overwritten
    /// block is recovered one pass after the result that triggers the ledger check, the
    /// AgentExecutor.log read of a pass may precede the exit line by milliseconds, or the poll
    /// loop stalled. Emitting at once lost the exit code and, when the late start line reopened
    /// the slot, produced a fallback duplicate 15 s later.
    /// </summary>
    public sealed class ImeLogTrackerResultHoldTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 10, 22, 13, 35, DateTimeKind.Utc);

        private sealed class Harness : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public ImeLogTracker Tracker { get; private set; }
            public DateTime Now { get; set; } = T0;
            public List<ScriptExecutionState> Completed { get; } = new List<ScriptExecutionState>();
            public List<ScriptStartedInfo> Started { get; } = new List<ScriptStartedInfo>();

            public Harness() { Tracker = Build(); }

            private ImeLogTracker Build()
            {
                var t = new ImeLogTracker(_tmp.Path, new List<ImeLogPattern>(), new AgentLogger(_tmp.Path, AgentLogLevel.Info), stateDirectory: _tmp.Path);
                t.UtcNowProvider = () => Now;
                t.OnScriptCompleted = s => Completed.Add(s);
                t.OnScriptStarted = s => Started.Add(s);
                return t;
            }

            /// <summary>Agent restart over the same state directory.</summary>
            public void Restart()
            {
                Tracker.SaveStateForTest();
                Tracker.Dispose();
                Tracker = Build();
                Tracker.LoadStateForTest();
            }

            /// <summary>A start line (IME "Script file … is generated" or the executor's argument line) with its source timestamp.</summary>
            public void Start(string id, DateTime lineTs)
            {
                Tracker.LastMatchedLogTimestamp = lineTs;
                Tracker.NextTestEntry();
                Tracker.HandlePlatformScriptStarted(id);
            }

            public void Result(string id, DateTime lineTs, string result = "Success")
                => Tracker.CompletePlatformScriptFromImeResultForTesting(id, result, lineTs);

            public void Exit(string id, DateTime lineTs, int exitCode = 0)
                => Tracker.RecordPlatformScriptExitCodeForTesting(id, exitCode, lineTs);

            /// <summary>The end-of-pass flush at the harness clock.</summary>
            public void Flush(bool force = false) => Tracker.FlushPendingPlatformScriptResults(Now, force);

            public void Dispose()
            {
                Tracker.Dispose();
                _tmp.Dispose();
            }
        }

        [Fact]
        public void Result_before_the_end_block_is_held_and_emits_once_with_the_exit_code()
        {
            using var h = new Harness();
            h.Start("376ee51a", T0);
            h.Now = T0.AddSeconds(2);
            h.Result("376ee51a", T0.AddSeconds(2));
            Assert.Empty(h.Completed);
            h.Now = T0.AddSeconds(2.1);
            h.Flush();
            Assert.Empty(h.Completed);

            // The end block surfaces on the next pass (ledger rewind or plain read lag).
            h.Exit("376ee51a", T0.AddSeconds(1.99));
            h.Now = T0.AddSeconds(2.2);
            h.Flush();

            var script = Assert.Single(h.Completed);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal("Success", script.Result);
            Assert.Equal("ime_policy_result", script.ResultSource);
            Assert.Equal(T0.AddSeconds(2), script.ResultObservedAtUtc);
            Assert.Equal("PS-SCRIPT-RESULT", script.ResultPatternId);
            Assert.Equal(T0, script.StartedAtUtc);

            // Nothing fires twice: later passes, a repeated result line, the 15 s fallback window.
            h.Now = T0.AddSeconds(30);
            h.Flush();
            h.Result("376ee51a", T0.AddSeconds(2));
            Assert.Single(h.Completed);
        }

        [Fact]
        public void Result_with_the_end_block_already_read_emits_at_once()
        {
            using var h = new Harness();
            h.Start("847da54e", T0);
            h.Exit("847da54e", T0.AddSeconds(2.3));
            h.Now = T0.AddSeconds(2.31);
            h.Result("847da54e", T0.AddSeconds(2.31));

            var script = Assert.Single(h.Completed);
            Assert.Equal(0, script.ExitCode);
            Assert.Null(script.ResultHeldSinceUtc);
            Assert.Equal(T0.AddSeconds(2.31), script.ResultObservedAtUtc);
        }

        [Fact]
        public void Held_result_emits_without_exit_code_after_the_grace()
        {
            using var h = new Harness();
            h.Start("784fcaf5", T0);
            h.Now = T0.AddSeconds(2);
            h.Result("784fcaf5", T0.AddSeconds(2));

            h.Now = T0.AddSeconds(2).Add(ImeLogTracker.PlatformScriptEndBlockGrace).AddMilliseconds(-100);
            h.Flush();
            Assert.Empty(h.Completed);

            h.Now = T0.AddSeconds(2).Add(ImeLogTracker.PlatformScriptEndBlockGrace);
            h.Flush();
            var script = Assert.Single(h.Completed);
            Assert.Null(script.ExitCode);
            Assert.Equal("Success", script.Result);
            Assert.Equal("ime_policy_result", script.ResultSource);

            // An exit line surfacing after the emit is dropped — no second event, no fallback.
            h.Exit("784fcaf5", T0.AddSeconds(1.99));
            h.Now = T0.AddSeconds(40);
            h.Flush();
            Assert.Single(h.Completed);
        }

        [Fact]
        public void Shutdown_flush_emits_a_held_result_at_once()
        {
            using var h = new Harness();
            h.Start("a52e4f47", T0);
            h.Now = T0.AddSeconds(2);
            h.Result("a52e4f47", T0.AddSeconds(2), "Failed");
            Assert.Empty(h.Completed);

            h.Flush(force: true);
            var script = Assert.Single(h.Completed);
            Assert.Equal("Failed", script.Result);
            Assert.Null(script.ExitCode);
        }

        [Fact]
        public void Late_start_line_of_an_emitted_run_opens_no_new_run()
        {
            // Session e7f3c910, 376ee51a: the result was emitted, THEN the executor's start and
            // exit lines of the same run were read (a 2 s poll stall) — the start opened a second
            // slot and the exit code became a fallback duplicate 15 s later.
            using var h = new Harness();
            h.Start("376ee51a", T0);
            h.Exit("376ee51a", T0.AddSeconds(1.99));
            h.Now = T0.AddSeconds(2);
            h.Result("376ee51a", T0.AddSeconds(2));
            Assert.Single(h.Completed);
            Assert.Single(h.Started);

            h.Start("376ee51a", T0.AddMilliseconds(300));
            Assert.Single(h.Started);
            h.Exit("376ee51a", T0.AddSeconds(1.99));
            h.Now = T0.AddSeconds(30);
            h.Flush();
            Assert.Single(h.Completed);
        }

        [Fact]
        public void Start_line_newer_than_the_emitted_result_is_the_next_run()
        {
            using var h = new Harness();
            h.Start("338d9434", T0);
            h.Exit("338d9434", T0.AddSeconds(1));
            h.Now = T0.AddSeconds(1);
            h.Result("338d9434", T0.AddSeconds(1));
            Assert.Single(h.Completed);

            // IME re-runs the policy after the user signed in.
            h.Start("338d9434", T0.AddMinutes(7));
            Assert.Equal(2, h.Started.Count);
            h.Exit("338d9434", T0.AddMinutes(7).AddSeconds(3));
            h.Now = T0.AddMinutes(7).AddSeconds(3);
            h.Result("338d9434", T0.AddMinutes(7).AddSeconds(3));
            Assert.Equal(2, h.Completed.Count);
            Assert.Equal(T0.AddMinutes(7), h.Completed[1].StartedAtUtc);
        }

        [Fact]
        public void Newer_start_line_closes_a_held_result_and_starts_the_next_run()
        {
            using var h = new Harness();
            h.Start("c81b8053", T0);
            h.Now = T0.AddSeconds(2);
            h.Result("c81b8053", T0.AddSeconds(2));
            Assert.Empty(h.Completed);

            // The end block never surfaced before the policy ran again.
            h.Start("c81b8053", T0.AddSeconds(4));
            var first = Assert.Single(h.Completed);
            Assert.Null(first.ExitCode);
            Assert.Equal(T0, first.StartedAtUtc);
            Assert.Equal(2, h.Started.Count);

            h.Exit("c81b8053", T0.AddSeconds(6));
            h.Now = T0.AddSeconds(6);
            h.Result("c81b8053", T0.AddSeconds(6));
            Assert.Equal(2, h.Completed.Count);
            Assert.Equal(0, h.Completed[1].ExitCode);
            Assert.Equal(T0.AddSeconds(4), h.Completed[1].StartedAtUtc);
        }

        [Fact]
        public void Result_without_a_start_takes_the_recovered_start_line_and_end_block()
        {
            // Both the start block and the end block were hidden when the result arrived; the
            // ledger rewind the result triggers brings both back on the next pass.
            using var h = new Harness();
            h.Now = T0.AddSeconds(2);
            h.Result("d39eeef3", T0.AddSeconds(2));
            Assert.Empty(h.Completed);

            h.Start("d39eeef3", T0);
            Assert.Empty(h.Started);
            h.Exit("d39eeef3", T0.AddSeconds(1.99));
            h.Now = T0.AddSeconds(2.2);
            h.Flush();

            var script = Assert.Single(h.Completed);
            Assert.Equal(T0, script.StartedAtUtc);
            Assert.Equal(0, script.ExitCode);
        }

        [Fact]
        public void Held_result_survives_a_restart()
        {
            using var h = new Harness();
            h.Start("832e27bc", T0);
            h.Now = T0.AddSeconds(2);
            h.Result("832e27bc", T0.AddSeconds(2));
            h.Restart();
            Assert.Empty(h.Completed);

            h.Exit("832e27bc", T0.AddSeconds(1.99));
            h.Now = T0.AddSeconds(2.5);
            h.Flush();
            var script = Assert.Single(h.Completed);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal(T0.AddSeconds(2), script.ResultObservedAtUtc);
            Assert.Equal("PS-SCRIPT-RESULT", script.ResultPatternId);
        }

        [Fact]
        public void Emitted_marker_keeps_the_run_timestamp_across_a_restart()
        {
            using var h = new Harness();
            h.Start("89b3cbcd", T0);
            h.Exit("89b3cbcd", T0.AddSeconds(1));
            h.Now = T0.AddSeconds(1);
            h.Result("89b3cbcd", T0.AddSeconds(1));
            h.Restart();

            h.Start("89b3cbcd", T0.AddMilliseconds(500)); // late line of the emitted run
            Assert.Single(h.Started);
            h.Start("89b3cbcd", T0.AddMinutes(5));        // the next run
            Assert.Equal(2, h.Started.Count);
        }

        [Fact]
        public void Marker_list_from_an_older_state_file_still_dedups()
        {
            using var tmp = new TempDirectory();
            var persistence = new ImeTrackerStatePersistence(tmp.Path, new AgentLogger(tmp.Path, AgentLogLevel.Info));
            persistence.Save(new ImeTrackerStateData { PlatformScriptResultEmitted = new List<string> { "policyZ" } });

            var completed = new List<ScriptExecutionState>();
            var started = new List<ScriptStartedInfo>();
            var tracker = new ImeLogTracker(tmp.Path, new List<ImeLogPattern>(), new AgentLogger(tmp.Path, AgentLogLevel.Info), stateDirectory: tmp.Path);
            tracker.OnScriptCompleted = s => completed.Add(s);
            tracker.OnScriptStarted = s => started.Add(s);
            tracker.LoadStateForTest();

            tracker.CompletePlatformScriptFromImeResultForTesting("policyZ", "Success", T0);
            Assert.Empty(completed);

            // Without a timestamp on the marker every start line is a new run, as before.
            tracker.LastMatchedLogTimestamp = T0;
            tracker.HandlePlatformScriptStarted("policyZ");
            Assert.Single(started);
        }
    }
}
