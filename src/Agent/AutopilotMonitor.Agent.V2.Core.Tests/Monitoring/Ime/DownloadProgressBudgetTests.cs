using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Logging;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// D-444: the session's download_progress total and stage ride in the tracker state, so a restart
    /// (or WhiteGlove Part 2) neither resets a throttled session to 3 s nor announces a stage twice.
    /// </summary>
    public sealed class DownloadProgressBudgetTests
    {
        [Fact]
        public void Total_and_stage_survive_a_restart_with_the_tracker_state()
        {
            using var tmp = new TempDirectory();
            var stateDir = Path.Combine(tmp.Path, "state");
            var logger = new AgentLogger(Path.Combine(tmp.Path, "agent"), AgentLogLevel.Info);

            using (var tracker = new ImeLogTracker(tmp.Path, new List<ImeLogPattern>(), logger, stateDirectory: stateDir))
            {
                for (var i = 0; i < 401; i++) tracker.DownloadProgressBudget.RecordEvent();
                Assert.True(tracker.DownloadProgressBudget.TryAdvanceStage(1));
                tracker.SaveStateForTest();
            }

            using var restarted = new ImeLogTracker(tmp.Path, new List<ImeLogPattern>(), logger, stateDirectory: stateDir);
            restarted.LoadStateForTest();

            Assert.Equal(401, restarted.DownloadProgressBudget.EventsSent);
            Assert.Equal(1, restarted.DownloadProgressBudget.Stage);
            Assert.False(restarted.DownloadProgressBudget.TryAdvanceStage(1));
        }

        [Fact]
        public void A_missing_or_broken_persisted_budget_starts_at_zero()
        {
            var budget = new DownloadProgressBudget();
            budget.Restore(null);
            Assert.Equal(0, budget.EventsSent);

            budget.Restore(new DownloadProgressBudgetState { EventsSent = -5, Stage = -1 });
            Assert.Equal(0, budget.EventsSent);
            Assert.Equal(0, budget.Stage);
        }

        [Fact]
        public void Each_change_marks_the_state_dirty_once()
        {
            var budget = new DownloadProgressBudget();
            Assert.False(budget.ConsumeDirty());

            budget.RecordEvent();
            Assert.True(budget.ConsumeDirty());
            Assert.False(budget.ConsumeDirty());

            Assert.False(budget.TryAdvanceStage(0));
            Assert.False(budget.ConsumeDirty());
            Assert.True(budget.TryAdvanceStage(2));
            Assert.True(budget.ConsumeDirty());
        }
    }
}
