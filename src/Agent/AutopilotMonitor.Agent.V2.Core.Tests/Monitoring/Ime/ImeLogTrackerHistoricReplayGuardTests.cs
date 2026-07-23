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
    /// Tracker-level historic-replay guard (session eaf3d8c4 part 2): source lines > 24 h older
    /// than now are content from a previous enrollment whose IME log survived on disk. The guard
    /// in <c>HandlePatternMatch</c> skips app-mutating actions for such lines so replayed apps
    /// never enter <c>_packageStates</c> / phase snapshots / persistence — keeping
    /// app_tracking_summary, culprit lists and final-status clean. Script actions pass through
    /// (the adapter suppresses their emissions), and SimulationMode bypasses the guard entirely.
    /// </summary>
    public sealed class ImeLogTrackerHistoricReplayGuardTests
    {
        private static readonly DateTime Now = new DateTime(2026, 7, 23, 15, 42, 0, DateTimeKind.Utc);

        private static List<ImeLogPattern> TestPatterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern
            {
                PatternId = "T-POLICIES", Category = "always", Enabled = true,
                Pattern = @"policies discovered = (?<policies>\[.*\])",
                Action = "policiesDiscovered",
                Parameters = new Dictionary<string, string>(),
            },
            new ImeLogPattern
            {
                PatternId = "T-INSTALLED", Category = "always", Enabled = true,
                Pattern = @"app (?<id>[\w-]+) installed",
                Action = "updateStateInstalled",
                Parameters = new Dictionary<string, string>(),
            },
            new ImeLogPattern
            {
                PatternId = "T-ESP-PHASE", Category = "always", Enabled = true,
                Pattern = @"In EspPhase: (?<espPhase>\w+)",
                Action = "espPhaseDetected",
                Parameters = new Dictionary<string, string>(),
            },
            new ImeLogPattern
            {
                PatternId = "T-SCRIPT-START", Category = "always", Enabled = true,
                Pattern = @"script (?<id>[\w-]+) started",
                Action = "scriptStarted",
                Parameters = new Dictionary<string, string> { ["scriptType"] = "platform" },
            },
        };

        private static ImeLogTracker BuildTracker(TempDirectory tmp)
        {
            var tracker = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: TestPatterns(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));
            tracker.UtcNowProvider = () => Now;
            return tracker;
        }

        private const string PoliciesLine = @"policies discovered = [{""Id"":""app-1"",""Name"":""App One""}]";

        [Fact]
        public void Stale_policies_line_does_not_populate_package_states()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.ProcessLogMessageForTest(PoliciesLine, Now.AddDays(-7));

            Assert.Empty(tracker.GetAllKnownPackageStates());
        }

        [Fact]
        public void Fresh_relog_of_same_policies_is_tracked_after_stale_skip()
        {
            // IME re-logs the full policy JSON on every app-policy check-in — skipping the
            // stale copy cannot starve the current enrollment.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.ProcessLogMessageForTest(PoliciesLine, Now.AddDays(-7));
            tracker.ProcessLogMessageForTest(PoliciesLine, Now.AddMinutes(-2));

            var all = tracker.GetAllKnownPackageStates();
            var pkg = Assert.Single(all);
            Assert.Equal("app-1", pkg.Id);
        }

        [Fact]
        public void Stale_state_update_against_fresh_app_is_skipped()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);
            var stateChanges = 0;
            tracker.OnAppStateChanged = (_, __, ___) => stateChanges++;

            tracker.ProcessLogMessageForTest(PoliciesLine, Now.AddMinutes(-5));   // fresh discovery
            tracker.ProcessLogMessageForTest("app app-1 installed", Now.AddDays(-7)); // stale mutation

            Assert.Equal(0, stateChanges);
            var pkg = Assert.Single(tracker.GetAllKnownPackageStates());
            Assert.NotEqual(AppInstallationState.Installed, pkg.InstallationState);
        }

        [Fact]
        public void Stale_esp_phase_line_does_not_block_fresh_phase_detection()
        {
            // A stale "AccountSetup" would advance _currentPhaseOrder and make the fresh
            // enrollment's DeviceSetup bounce as "backward" — the guard must skip it.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);
            var phases = new List<string>();
            tracker.OnEspPhaseChanged = p => phases.Add(p);

            tracker.ProcessLogMessageForTest("In EspPhase: AccountSetup", Now.AddDays(-7));
            tracker.ProcessLogMessageForTest("In EspPhase: DeviceSetup", Now.AddMinutes(-1));

            Assert.Equal(new[] { "DeviceSetup" }, phases);
        }

        [Fact]
        public void Stale_script_line_still_reaches_script_handlers()
        {
            // Script tracker state is harmless for stale lines (adapter suppresses the
            // emissions; the stale-slot hardening covers leftovers) — the guard must not
            // swallow script actions.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);
            var started = new List<ScriptStartedInfo>();
            tracker.OnScriptStarted = i => started.Add(i);

            tracker.ProcessLogMessageForTest("script aaaa1111-0000-0000-0000-000000000000 started", Now.AddDays(-7));

            Assert.Single(started);
        }

        [Fact]
        public void SimulationMode_bypasses_the_guard()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);
            tracker.SimulationMode = true;

            tracker.ProcessLogMessageForTest(PoliciesLine, Now.AddDays(-7));

            Assert.Single(tracker.GetAllKnownPackageStates());
        }

        [Fact]
        public void Lines_without_timestamp_are_not_treated_as_stale()
        {
            // Non-CMTrace lines carry no source timestamp (entry == null) — never stale.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.ProcessLogMessageForTest(PoliciesLine, sourceTimestampUtc: null);

            Assert.Single(tracker.GetAllKnownPackageStates());
        }
    }
}
