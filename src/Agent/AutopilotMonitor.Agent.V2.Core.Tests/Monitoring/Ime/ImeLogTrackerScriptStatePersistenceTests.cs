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
    /// H1 (delta review 2026-07-02): the platform-script emission markers and pending buffer must
    /// survive an agent restart. Field scenario: the shutdown pass force-flushes a fallback
    /// completion, the agent restarts (stall restarts run up to ~88× per session), IME writes its
    /// authoritative PS-SCRIPT-RESULT line after the flush — the restarted tracker parses that
    /// line fresh (it lies beyond the persisted file position) and, without the persisted marker,
    /// emitted a duplicate script_completed. Also covers the stale-result guard (L1) and the
    /// restart-safe script_timeout_suspected claim (L10).
    /// </summary>
    public sealed class ImeLogTrackerScriptStatePersistenceTests
    {
        private static ImeLogTracker BuildTracker(TempDirectory tmp, out List<ScriptExecutionState> emitted)
        {
            var captured = new List<ScriptExecutionState>();
            emitted = captured;
            var tracker = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info),
                stateDirectory: tmp.Path);
            tracker.OnScriptCompleted = s => captured.Add(s);
            return tracker;
        }

        [Fact]
        public void Fallback_marker_survives_restart_and_dedupes_late_ime_result()
        {
            using var tmp = new TempDirectory();
            var observedAt = new DateTime(2026, 7, 3, 9, 0, 0, DateTimeKind.Utc);

            // Run 1: exit code seen, shutdown force-flush emits the fallback completion.
            var tracker1 = BuildTracker(tmp, out var emitted1);
            tracker1.SeedPendingPlatformScriptForTesting("policyA", exitCode: 0, exitObservedAtUtc: observedAt);
            tracker1.FlushStalePlatformScriptResults(observedAt.AddSeconds(1), force: true);
            Assert.Single(emitted1);
            tracker1.SaveStateForTest();

            // Restart: IME's PS-SCRIPT-RESULT line for the SAME execution arrives now.
            var tracker2 = BuildTracker(tmp, out var emitted2);
            tracker2.LoadStateForTest();
            tracker2.CompletePlatformScriptFromImeResultForTesting("policyA", "Success");

            Assert.Empty(emitted2); // was a duplicate before the fix
        }

        [Fact]
        public void Pending_script_survives_restart_and_flushes_after_grace()
        {
            using var tmp = new TempDirectory();
            var observedAt = new DateTime(2026, 7, 3, 9, 0, 0, DateTimeKind.Utc);

            // Exit code observed but neither flushed nor resolved before the agent died.
            var tracker1 = BuildTracker(tmp, out var emitted1);
            tracker1.SeedPendingPlatformScriptForTesting("policyB", exitCode: 3, exitObservedAtUtc: observedAt);
            tracker1.SaveStateForTest();
            Assert.Empty(emitted1);

            // Restarted tracker continues the grace window instead of dropping the script.
            var tracker2 = BuildTracker(tmp, out var emitted2);
            tracker2.LoadStateForTest();
            tracker2.FlushStalePlatformScriptResults(observedAt.AddSeconds(20));

            var script = Assert.Single(emitted2);
            Assert.Equal("policyB", script.PolicyId);
            Assert.Equal("Failed", script.Result);
            Assert.Equal("agentexecutor_fallback", script.ResultSource);
        }

        [Fact]
        public void Fresh_start_after_restart_still_clears_restored_marker()
        {
            using var tmp = new TempDirectory();
            var observedAt = new DateTime(2026, 7, 3, 9, 0, 0, DateTimeKind.Utc);

            var tracker1 = BuildTracker(tmp, out _);
            tracker1.SeedPendingPlatformScriptForTesting("policyC", exitCode: 0, exitObservedAtUtc: observedAt);
            tracker1.FlushStalePlatformScriptResults(observedAt.AddSeconds(1), force: true);
            tracker1.SaveStateForTest();

            // Restart, then a genuine RE-RUN of the same policy (fresh start clears the marker).
            var tracker2 = BuildTracker(tmp, out var emitted2);
            tracker2.LoadStateForTest();
            tracker2.SeedPendingPlatformScriptForTesting("policyC", exitCode: 0, exitObservedAtUtc: observedAt.AddMinutes(10));
            tracker2.CompletePlatformScriptFromImeResultForTesting("policyC", "Success", observedAt.AddMinutes(11));

            var script = Assert.Single(emitted2);
            Assert.Equal("ime_policy_result", script.ResultSource);
        }

        [Fact]
        public void Old_state_file_without_script_fields_loads_clean()
        {
            using var tmp = new TempDirectory();

            // Persist a state file from "before the fix" (fields null).
            var persistence = new ImeTrackerStatePersistence(tmp.Path, new AgentLogger(tmp.Path, AgentLogLevel.Info));
            persistence.Save(new ImeTrackerStateData { CurrentPhaseOrder = 2 });

            var tracker = BuildTracker(tmp, out var emitted);
            tracker.LoadStateForTest(); // must not throw

            // Degrades to pre-fix behavior: no markers restored, normal emission works.
            tracker.CompletePlatformScriptFromImeResultForTesting("policyD", "Success");
            Assert.Single(emitted);
        }

        [Fact]
        public void Stale_result_older_than_current_run_start_is_dropped()
        {
            using var tmp = new TempDirectory();
            var run2Start = new DateTime(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);

            var tracker = BuildTracker(tmp, out var emitted);
            // Run 2 is live (fresh slot with its start stamped)…
            tracker.SeedPendingPlatformScriptForTesting("policyE", exitCode: null, exitObservedAtUtc: null, startedAtUtc: run2Start);

            // …when run 1's late PS-SCRIPT-RESULT (older than run 2's start) finally arrives.
            tracker.CompletePlatformScriptFromImeResultForTesting("policyE", "Failed", run2Start.AddMinutes(-5));
            Assert.Empty(emitted);

            // Run 2's own result (newer than its start) still emits normally.
            tracker.CompletePlatformScriptFromImeResultForTesting("policyE", "Success", run2Start.AddMinutes(2));
            var script = Assert.Single(emitted);
            Assert.Equal("Success", script.Result);
            Assert.Equal(run2Start, script.StartedAtUtc);
        }

        [Fact]
        public void Script_timeout_claim_is_one_shot_and_survives_restart()
        {
            using var tmp = new TempDirectory();

            var tracker1 = BuildTracker(tmp, out _);
            Assert.True(tracker1.TryClaimScriptTimeoutSuspected("policyF"));
            Assert.False(tracker1.TryClaimScriptTimeoutSuspected("policyF")); // in-lifetime dedup
            tracker1.SaveStateForTest();

            var tracker2 = BuildTracker(tmp, out _);
            tracker2.LoadStateForTest();
            Assert.False(tracker2.TryClaimScriptTimeoutSuspected("policyF")); // cross-restart dedup
            Assert.True(tracker2.TryClaimScriptTimeoutSuspected("policyG"));  // other policies unaffected
        }
    }
}
