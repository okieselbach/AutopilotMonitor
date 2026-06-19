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
    /// Regression cover for the AgentExecutor exit-code fallback: platform scripts that complete
    /// (exit code captured from AgentExecutor.log) but whose authoritative IME PS-SCRIPT-RESULT
    /// line never arrives before the deadline must still surface a completion event — otherwise a
    /// device that ran N platform scripts in quick succession surfaces only the one that happened
    /// to get its IME result logged in time (field bug, session d1da052d…).
    /// </summary>
    public sealed class ImeLogTrackerPlatformScriptFallbackTests
    {
        private static ImeLogTracker BuildTracker(TempDirectory tmp, out List<ScriptExecutionState> emitted)
        {
            var captured = new List<ScriptExecutionState>();
            emitted = captured;
            var tracker = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));
            tracker.OnScriptCompleted = s => captured.Add(s);
            return tracker;
        }

        [Fact]
        public void Fallback_emits_completion_when_ime_result_never_arrives()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var observedAt = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            tracker.SeedPendingPlatformScriptForTesting("dece354a", exitCode: 0, exitObservedAtUtc: observedAt, stdout: "Script start.\nScript end.");

            // Well past the grace window — should emit.
            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(20));

            var script = Assert.Single(emitted);
            Assert.Equal("dece354a", script.PolicyId);
            Assert.Equal("platform", script.ScriptType);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal("Success", script.Result);
            Assert.Equal("agentexecutor_fallback", script.ResultSource);
            Assert.Equal("Script start.\nScript end.", script.Stdout);
        }

        [Fact]
        public void Fallback_holds_until_grace_period_elapses()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var observedAt = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            tracker.SeedPendingPlatformScriptForTesting("60b43a2e", exitCode: 0, exitObservedAtUtc: observedAt);

            // Inside the grace window — IME might still log its result; do not emit yet.
            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(5));
            Assert.Empty(emitted);

            // Past the grace window — emit.
            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(20));
            Assert.Single(emitted);
        }

        [Fact]
        public void Force_flush_ignores_grace_period()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var observedAt = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            tracker.SeedPendingPlatformScriptForTesting("846cea22", exitCode: 0, exitObservedAtUtc: observedAt);

            // Shutdown flush right after exit — grace not elapsed, but force emits anyway.
            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(1), force: true);

            Assert.Single(emitted);
        }

        [Fact]
        public void Nonzero_exit_code_emits_failed_result()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var observedAt = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            tracker.SeedPendingPlatformScriptForTesting("bad5c0de", exitCode: 1, exitObservedAtUtc: observedAt);

            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(20));

            var script = Assert.Single(emitted);
            Assert.Equal(1, script.ExitCode);
            Assert.Equal("Failed", script.Result);
            Assert.Equal("agentexecutor_fallback", script.ResultSource);
        }

        [Fact]
        public void Script_without_exit_code_is_not_emitted()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);

            // Started but no exit code yet → still running, must not be emitted even on force flush.
            tracker.SeedPendingPlatformScriptForTesting("running", exitCode: null, exitObservedAtUtc: null);

            tracker.FlushStalePlatformScriptResults(DateTime.UtcNow, force: true);

            Assert.Empty(emitted);
        }

        [Fact]
        public void Real_ime_result_after_fallback_does_not_double_emit()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var observedAt = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            tracker.SeedPendingPlatformScriptForTesting("dece354a", exitCode: 0, exitObservedAtUtc: observedAt);
            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(20));
            Assert.Single(emitted);

            // IME finally logs its PS-SCRIPT-RESULT line — must NOT produce a second event.
            tracker.CompletePlatformScriptFromImeResultForTesting("dece354a", "Success");
            Assert.Single(emitted);
        }

        [Fact]
        public void Fallback_event_carries_script_exit_timestamp_for_emit()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var observedAt = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            tracker.SeedPendingPlatformScriptForTesting("dece354a", exitCode: 0, exitObservedAtUtc: observedAt);
            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(20));

            // The exit-observed timestamp must travel on the emitted state so the adapter can bind
            // the event to it instead of an unrelated "last matched" line parsed during the grace.
            var script = Assert.Single(emitted);
            Assert.Equal(observedAt, script.ExitObservedAtUtc);
            Assert.Equal("agentexecutor_fallback", script.ResultSource);
        }

        [Fact]
        public void Same_policy_can_emit_again_after_a_fresh_run()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var firstRun = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            // Run 1 of policy X — fallback emits.
            tracker.SeedPendingPlatformScriptForTesting("policyX", exitCode: 0, exitObservedAtUtc: firstRun);
            tracker.FlushStalePlatformScriptResults(firstRun.AddSeconds(20));
            Assert.Single(emitted);

            // Run 2 of the SAME policy later in the same agent lifetime (IME re-evaluation / retry)
            // must NOT be deduped away — the fresh start clears the prior emitted-marker.
            var secondRun = firstRun.AddMinutes(30);
            tracker.SeedPendingPlatformScriptForTesting("policyX", exitCode: 0, exitObservedAtUtc: secondRun);
            tracker.FlushStalePlatformScriptResults(secondRun.AddSeconds(20));
            Assert.Equal(2, emitted.Count);
        }

        [Fact]
        public void Ime_result_before_fallback_wins_and_fallback_is_noop()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);
            var observedAt = new DateTime(2026, 6, 19, 12, 59, 4, DateTimeKind.Utc);

            tracker.SeedPendingPlatformScriptForTesting("35ed39d9", exitCode: 0, exitObservedAtUtc: observedAt);

            // Authoritative result arrives in time (healthy path).
            tracker.CompletePlatformScriptFromImeResultForTesting("35ed39d9", "Success");
            var script = Assert.Single(emitted);
            Assert.Equal("ime_policy_result", script.ResultSource);

            // Later fallback pass must find nothing pending → no duplicate.
            tracker.FlushStalePlatformScriptResults(observedAt.AddSeconds(20));
            Assert.Single(emitted);
        }
    }
}
