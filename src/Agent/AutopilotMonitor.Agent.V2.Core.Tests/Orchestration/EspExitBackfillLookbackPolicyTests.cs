#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// The Shell-Core ESP-exit replay is RESTART RECOVERY: it re-reads observations the agent could
    /// not make because no agent process was running. The window is therefore measured against the
    /// previous run's last known liveness, not guessed from a constant.
    /// <para>
    /// Why a constant is not enough (Codex review P2): the scheduled task carries a BootTrigger
    /// only, with no restart-on-failure. A crashed agent does not come back until the NEXT BOOT —
    /// possibly hours later, long after Windows wrote the final ESP exit. Five minutes would lose
    /// it every time.
    /// </para>
    /// </summary>
    public sealed class EspExitBackfillLookbackPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 19, 8, 43, 16, DateTimeKind.Utc);
        private const int Default = ShellCoreTracker.BackfillLookbackMinutes;

        private static int Resolve(string? exitType, DateTime? lastAlive = null, DateTime? bootUtc = null)
            => DefaultComponentFactory.ResolveEspExitBackfillLookbackMinutes(exitType, bootUtc, lastAlive, Now);

        // ---- no previous run => nothing to recover ---------------------------------------

        [Theory]
        [InlineData("first_run")]
        [InlineData("FIRST_RUN")]
        [InlineData("")]
        [InlineData(null)]
        public void No_previous_run_means_no_replay(string? exitType)
        {
            Assert.Equal(0, Resolve(exitType));
        }

        [Fact]
        public void First_run_stays_off_even_with_timestamps_available()
        {
            // The exit type decides, not the presence of a timestamp.
            Assert.Equal(0, Resolve("first_run", lastAlive: Now.AddHours(-3), bootUtc: Now.AddHours(-3)));
        }

        // ---- the default is a floor, never a ceiling --------------------------------------

        [Theory]
        [InlineData("clean")]
        [InlineData("hard_kill")]
        [InlineData("exception_crash")]
        [InlineData("reboot_kill")]
        public void Without_any_timestamp_the_default_applies(string exitType)
        {
            Assert.Equal(Default, Resolve(exitType));
        }

        [Fact]
        public void A_restart_that_returned_within_seconds_keeps_the_default()
        {
            Assert.Equal(Default, Resolve("clean", lastAlive: Now.AddSeconds(-20)));
        }

        // ---- the crash case P2 is about ---------------------------------------------------

        [Fact]
        public void A_crash_recovered_only_at_the_next_boot_gets_the_whole_gap()
        {
            // BootTrigger-only task: the agent was gone for three hours while the ESP finished.
            Assert.Equal(181, Resolve("exception_crash", lastAlive: Now.AddHours(-3)));
        }

        [Fact]
        public void Snapshot_mtime_carries_the_window_when_no_boot_time_exists()
        {
            // LastBootUtc is only populated for hard_kill / reboot_kill — exception_crash and
            // clean have nothing else to go on.
            Assert.Equal(46, Resolve("clean", lastAlive: Now.AddMinutes(-45)));
        }

        // ---- reboot: both inputs available, wider wins ------------------------------------

        [Fact]
        public void The_wider_of_the_two_inputs_wins()
        {
            // Snapshot last written 40 min ago, boot 12 min ago — the gap started at the snapshot.
            Assert.Equal(41, Resolve("reboot_kill", lastAlive: Now.AddMinutes(-40), bootUtc: Now.AddMinutes(-12)));
        }

        [Fact]
        public void Boot_time_alone_still_covers_a_reboot_without_a_snapshot()
        {
            Assert.Equal(41, Resolve("reboot_kill", bootUtc: Now.AddMinutes(-40)));
        }

        // ---- robustness -------------------------------------------------------------------

        [Fact]
        public void A_timestamp_in_the_future_is_ignored()
        {
            // Clock skew across a reboot must never produce a negative window.
            Assert.Equal(Default, Resolve("reboot_kill", lastAlive: Now.AddMinutes(5), bootUtc: Now.AddMinutes(9)));
        }

        [Fact]
        public void A_future_timestamp_does_not_suppress_a_valid_one()
        {
            Assert.Equal(31, Resolve("reboot_kill", lastAlive: Now.AddMinutes(9), bootUtc: Now.AddMinutes(-30)));
        }

        [Fact]
        public void A_very_long_gap_is_clamped_by_the_tracker()
        {
            // The policy may propose more than the cap; ClampLookbackMinutes is the backstop, so
            // the two together can never produce an unbounded event-log query.
            var proposed = Resolve("exception_crash", lastAlive: Now.AddHours(-20));
            Assert.True(proposed > ShellCoreTracker.BackfillLookbackMaxMinutes);
            Assert.Equal(ShellCoreTracker.BackfillLookbackMaxMinutes,
                ShellCoreTracker.ClampLookbackMinutes(proposed));
        }

        // ---- the snapshot probe itself ----------------------------------------------------

        [Fact]
        public void Missing_snapshot_reads_as_null_not_as_an_exception()
        {
            using var tmp = new Harness.TempDirectory();
            Assert.Null(DefaultComponentFactory.ReadPreviousRunLastAliveUtc(tmp.Path));
            Assert.Null(DefaultComponentFactory.ReadPreviousRunLastAliveUtc(string.Empty));
        }

        [Fact]
        public void An_existing_snapshot_reports_its_write_time()
        {
            using var tmp = new Harness.TempDirectory();
            var path = System.IO.Path.Combine(tmp.Path, "snapshot.json");
            System.IO.File.WriteAllText(path, "{}");

            var read = DefaultComponentFactory.ReadPreviousRunLastAliveUtc(tmp.Path);

            Assert.NotNull(read);
            Assert.Equal(DateTimeKind.Utc, read!.Value.Kind);
            Assert.True((DateTime.UtcNow - read.Value).Duration() < TimeSpan.FromMinutes(5));
        }
    }
}
