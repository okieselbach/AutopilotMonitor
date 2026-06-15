using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry
{
    /// <summary>
    /// MON-B10 — covers the state-change-only transition logic behind the one-shot
    /// <c>disk_space_low</c> warning. The warning must fire exactly once on crossing below 2 GB,
    /// stay silent while the drive remains low (no heartbeat), and only re-arm after free space
    /// recovers past the 3 GB hysteresis mark.
    /// </summary>
    public class PerformanceCollectorDiskLowTests
    {
        [Fact]
        public void AboveThreshold_DoesNotWarn_AndStaysDisarmed()
        {
            bool warned = false;
            Assert.False(PerformanceCollector.EvaluateDiskLowTransition(50.0, ref warned));
            Assert.False(warned);
        }

        [Fact]
        public void CrossingBelowThreshold_WarnsOnce_AndLatches()
        {
            bool warned = false;
            Assert.True(PerformanceCollector.EvaluateDiskLowTransition(1.9, ref warned));
            Assert.True(warned);
        }

        [Fact]
        public void StillLowAfterWarning_DoesNotReWarn()
        {
            bool warned = true; // already warned on a previous low read
            Assert.False(PerformanceCollector.EvaluateDiskLowTransition(1.5, ref warned));
            Assert.True(warned);
        }

        [Fact]
        public void RecoveryWithinHysteresisBand_StaysLatched()
        {
            // Between 2 GB (threshold) and 3 GB (recovery): not low enough to re-warn,
            // not recovered enough to re-arm. Must remain latched and silent.
            bool warned = true;
            Assert.False(PerformanceCollector.EvaluateDiskLowTransition(2.5, ref warned));
            Assert.True(warned);
        }

        [Fact]
        public void RecoveryPastHysteresisMark_ReArms()
        {
            bool warned = true;
            Assert.False(PerformanceCollector.EvaluateDiskLowTransition(3.0, ref warned));
            Assert.False(warned);
        }

        [Fact]
        public void DropBelowAfterReArm_WarnsAgain()
        {
            bool warned = false;

            // First drop → warns and latches.
            Assert.True(PerformanceCollector.EvaluateDiskLowTransition(1.0, ref warned));
            Assert.True(warned);

            // Recovers past the hysteresis mark → re-arms.
            Assert.False(PerformanceCollector.EvaluateDiskLowTransition(4.0, ref warned));
            Assert.False(warned);

            // Drops again → warns a second time.
            Assert.True(PerformanceCollector.EvaluateDiskLowTransition(1.8, ref warned));
            Assert.True(warned);
        }

        [Fact]
        public void ExactlyAtThreshold_IsNotLow()
        {
            // 2.0 GB is the boundary; the predicate is strict "below", so it must not warn.
            bool warned = false;
            Assert.False(PerformanceCollector.EvaluateDiskLowTransition(2.0, ref warned));
            Assert.False(warned);
        }
    }
}
