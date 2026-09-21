using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// IME re-runs health-script policies on its own schedule for as long as the device is up.
    /// A session that waited days for its user reported every hourly run of every policy
    /// (field: 9 policies × 94 runs in one session). The gate emits the first run and every
    /// changed result, counts the rest, and keeps the start/result pairing intact that the
    /// running indicators rely on.
    /// </summary>
    public sealed class RecurringScriptGateTests
    {
        private const string Policy = "11111111-2222-3333-4444-555555555555";
        private static readonly DateTime T0 = new DateTime(2026, 9, 16, 20, 0, 0, DateTimeKind.Utc);

        private static ScriptExecutionState Early(string compliance, int exit = 0, string? stderr = null) =>
            new ScriptExecutionState
            {
                PolicyId = Policy, ScriptType = "remediation", ScriptPart = "detection",
                ComplianceResult = compliance, ExitCode = exit, Stderr = stderr, DurationBasis = "script_runtime",
            };

        private static ScriptExecutionState Consolidated(string compliance, int exit = 0, int status = 1) =>
            new ScriptExecutionState
            {
                PolicyId = Policy, ScriptType = "remediation", ScriptPart = "detection",
                ComplianceResult = compliance, ExitCode = exit, RemediationStatus = status,
                DurationBasis = "cycle_including_reporting_latency",
            };

        [Fact]
        public void First_run_emits_start_and_result()
        {
            var gate = new RecurringScriptGate();

            Assert.True(gate.OnStart(Policy, T0, "6", "HS-SCRIPT-START"));
            Assert.True(gate.OnResult(Early("True"), T0.AddSeconds(5), out var released));
            Assert.Null(released);
        }

        [Fact]
        public void Repeated_result_is_counted_and_its_start_stays_held()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "HS-SCRIPT-START");
            gate.OnResult(Early("True"), T0.AddSeconds(5), out _);

            Assert.False(gate.OnStart(Policy, T0.AddHours(1), "6", "HS-SCRIPT-START"));
            Assert.False(gate.OnResult(Early("True"), T0.AddHours(1).AddSeconds(5), out var released));
            Assert.Null(released);

            var summary = Assert.Single(gate.TakeSummaries());
            Assert.Equal(2, summary.RunsObserved);
            Assert.Equal(1, summary.RunsCollapsed);
            Assert.Equal(1, summary.CollapsedResults);
            Assert.Equal(T0.AddHours(1).AddSeconds(5), summary.LastCollapsedAtUtc);
        }

        [Fact]
        public void Changed_result_releases_the_held_start_with_its_own_timestamp()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "HS-SCRIPT-START");
            gate.OnResult(Early("True"), T0.AddSeconds(5), out _);

            var secondStart = T0.AddHours(1);
            gate.OnStart(Policy, secondStart, "6", "HS-SCRIPT-START");
            Assert.True(gate.OnResult(Early("False", exit: 1), secondStart.AddSeconds(5), out var released));

            Assert.NotNull(released);
            Assert.Equal(Policy, released.PolicyId);
            Assert.Equal(secondStart, released.SourceTimestampUtc);
            Assert.Equal("HS-SCRIPT-START", released.PatternId);
            Assert.Equal("6", released.PolicyType);
            Assert.Empty(gate.TakeSummaries());
        }

        [Fact]
        public void New_stderr_counts_as_a_changed_result()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "p");
            gate.OnResult(Early("True"), T0, out _);

            gate.OnStart(Policy, T0.AddHours(1), "6", "p");
            Assert.True(gate.OnResult(Early("True", stderr: "Get-ChildItem : path not found"), T0.AddHours(1), out _));
        }

        [Fact]
        public void Early_signal_and_consolidated_result_do_not_overwrite_each_other()
        {
            // Both describe the detection phase, but only the consolidated one knows
            // RemediationStatus. Sharing one signature slot would make every run look changed.
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "p");
            Assert.True(gate.OnResult(Early("True"), T0, out _));
            Assert.True(gate.OnResult(Consolidated("True"), T0.AddMinutes(1), out _));

            gate.OnStart(Policy, T0.AddHours(1), "6", "p");
            Assert.False(gate.OnResult(Early("True"), T0.AddHours(1), out _));
            Assert.False(gate.OnResult(Consolidated("True"), T0.AddHours(1).AddMinutes(1), out _));
        }

        [Fact]
        public void Held_start_without_result_is_released_only_after_the_grace()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "p");
            gate.OnResult(Early("True"), T0, out _);
            var heldAt = T0.AddHours(1);
            gate.OnStart(Policy, heldAt, "6", "p");

            Assert.Empty(gate.ReleaseStartsWithoutResult(heldAt.AddMinutes(29), TimeSpan.FromMinutes(30)));

            var released = Assert.Single(gate.ReleaseStartsWithoutResult(heldAt.AddMinutes(31), TimeSpan.FromMinutes(30)));
            Assert.Equal(heldAt, released.SourceTimestampUtc);
            Assert.Empty(gate.ReleaseStartsWithoutResult(heldAt.AddHours(2), TimeSpan.FromMinutes(30)));
        }

        [Fact]
        public void Held_start_whose_result_was_collapsed_is_never_released_as_unfinished()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "p");
            gate.OnResult(Early("True"), T0, out _);
            gate.OnStart(Policy, T0.AddHours(1), "6", "p");
            gate.OnResult(Early("True"), T0.AddHours(1), out _);

            Assert.Empty(gate.ReleaseStartsWithoutResult(T0.AddDays(1), TimeSpan.FromMinutes(30)));
        }

        [Fact]
        public void Run_superseded_by_the_next_start_counts_as_without_result()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "p");
            gate.OnResult(Early("True"), T0, out _);
            gate.OnStart(Policy, T0.AddHours(1), "6", "p");   // no result
            gate.OnStart(Policy, T0.AddHours(2), "6", "p");
            gate.OnResult(Early("True"), T0.AddHours(2), out _);

            var summary = Assert.Single(gate.TakeSummaries());
            Assert.Equal(3, summary.RunsObserved);
            Assert.Equal(1, summary.RunsWithoutResult);
            Assert.Equal(1, summary.RunsCollapsed);
        }

        [Fact]
        public void Summary_reports_growth_only()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "p");
            gate.OnResult(Early("True"), T0, out _);
            gate.OnStart(Policy, T0.AddHours(1), "6", "p");
            gate.OnResult(Early("True"), T0.AddHours(1), out _);

            Assert.Single(gate.TakeSummaries());
            Assert.Empty(gate.TakeSummaries());

            gate.OnStart(Policy, T0.AddHours(2), "6", "p");
            gate.OnResult(Early("True"), T0.AddHours(2), out _);
            Assert.Equal(2, Assert.Single(gate.TakeSummaries()).RunsCollapsed);
        }

        [Fact]
        public void State_round_trips_through_json()
        {
            var gate = new RecurringScriptGate();
            gate.OnStart(Policy, T0, "6", "p");
            gate.OnResult(Early("True"), T0, out _);
            gate.OnStart(Policy, T0.AddHours(1), "6", "p");

            var json = JsonConvert.SerializeObject(gate.ToPersisted());
            var restored = new RecurringScriptGate();
            restored.Restore(JsonConvert.DeserializeObject<Dictionary<string, RecurringScriptPolicyState>>(json));

            // The restored gate still knows the last report and still holds the second start.
            Assert.True(restored.OnResult(Early("False", exit: 1), T0.AddHours(1).AddSeconds(5), out var released));
            Assert.Equal(T0.AddHours(1), released.SourceTimestampUtc);
        }

        // ── Through the tracker ───────────────────────────────────────────────────

        private sealed class Rig
        {
            public readonly List<ScriptStartedInfo> Started = new List<ScriptStartedInfo>();
            public readonly List<ScriptExecutionState> Completed = new List<ScriptExecutionState>();
            public readonly List<ScriptRecurrenceSummary> Summaries = new List<ScriptRecurrenceSummary>();
            public readonly ImeLogTracker Tracker;

            public Rig(TempDirectory tmp)
            {
                Tracker = new ImeLogTracker(
                    logFolder: tmp.Path,
                    patterns: new List<ImeLogPattern>(),
                    logger: new AgentLogger(tmp.Path, AgentLogLevel.Info),
                    stateDirectory: tmp.Path);
                Tracker.OnScriptStarted = Started.Add;
                Tracker.OnScriptCompleted = Completed.Add;
                Tracker.OnScriptRecurrenceSummary = Summaries.Add;
            }

            public void Run(DateTime startedAtUtc, string compliance)
            {
                Tracker.HandleHealthScriptStartForTest(Policy, startedAtUtc);
                Tracker.HandleHealthScriptDetectionResultForTest(Policy, compliance, "pre", exitCode: compliance == "True" ? 0 : 1);
            }
        }

        [Fact]
        public void Tracker_collapses_repeats_across_a_restart_and_reports_them_on_stop()
        {
            using var tmp = new TempDirectory();

            var first = new Rig(tmp);
            first.Run(T0, "True");
            first.Run(T0.AddHours(1), "True");
            Assert.Single(first.Started);
            Assert.Single(first.Completed);
            first.Tracker.SaveStateForTest();

            // Restart (reboot kill, WhiteGlove Part 2): the last report and the counter are back.
            var second = new Rig(tmp);
            second.Tracker.LoadStateForTest();
            second.Run(T0.AddHours(2), "True");
            Assert.Empty(second.Started);
            Assert.Empty(second.Completed);

            second.Run(T0.AddHours(3), "False");
            var start = Assert.Single(second.Started);
            Assert.Equal(T0.AddHours(3), start.SourceTimestampUtc);
            Assert.Equal("False", Assert.Single(second.Completed).ComplianceResult);

            second.Tracker.FlushRecurringScripts(T0.AddHours(4), shuttingDown: true);
            var summary = Assert.Single(second.Summaries);
            Assert.Equal(4, summary.RunsObserved);
            Assert.Equal(2, summary.RunsCollapsed);
        }

        [Fact]
        public void Tracker_leaves_platform_scripts_alone()
        {
            using var tmp = new TempDirectory();
            var rig = new Rig(tmp);
            var observedAt = T0;

            for (var run = 0; run < 2; run++)
            {
                rig.Tracker.SeedPendingPlatformScriptForTesting("platformA", exitCode: 0, exitObservedAtUtc: observedAt.AddMinutes(run * 10));
                rig.Tracker.CompletePlatformScriptFromImeResultForTesting("platformA", "Success", observedAt.AddMinutes(run * 10 + 1));
            }

            Assert.Equal(2, rig.Completed.Count(s => s.ScriptType == "platform"));
            rig.Tracker.FlushRecurringScripts(T0.AddDays(1), shuttingDown: true);
            Assert.Empty(rig.Summaries);
        }
    }
}
