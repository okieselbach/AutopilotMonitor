#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// The Shell-Core ESP-exit replay is RESTART RECOVERY: it re-reads observations the agent could
    /// not make because no agent process was running. Two invariants matter and both are pinned
    /// here, because getting either wrong is invisible until a real enrollment pays for it:
    /// <list type="bullet">
    ///   <item>On a first run there is no gap to recover — the replay must stay OFF so the happy
    ///     path behaves exactly as it did before the recovery existed. Replaying a 62407 the
    ///     pre-fix agent never saw would push an extra EspExiting into the reducer.</item>
    ///   <item>After a reboot_kill the gap spans the whole downtime, which the 5-minute default
    ///     cannot cover.</item>
    /// </list>
    /// </summary>
    public sealed class EspExitBackfillLookbackPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 19, 8, 43, 16, DateTimeKind.Utc);

        private static int Resolve(string? exitType, DateTime? bootUtc = null)
            => DefaultComponentFactory.ResolveEspExitBackfillLookbackMinutes(exitType, bootUtc, Now);

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
        public void First_run_stays_off_even_with_a_boot_timestamp()
        {
            // Defence in depth: LastBootUtc is only populated for hard_kill/reboot_kill, but the
            // exit type — not the presence of a timestamp — is what decides.
            Assert.Equal(0, Resolve("first_run", Now.AddHours(-3)));
        }

        [Theory]
        [InlineData("clean")]
        [InlineData("hard_kill")]
        [InlineData("exception_crash")]
        public void An_in_place_restart_keeps_the_short_default(string exitType)
        {
            // The process comes back within seconds, so five minutes covers the gap comfortably.
            Assert.Equal(ShellCoreTracker.BackfillLookbackMinutes, Resolve(exitType));
        }

        [Fact]
        public void Reboot_kill_widens_the_window_to_the_downtime()
        {
            // Boot 40 min ago: everything since that boot, plus a minute of slack for the
            // granularity of the event-log timestamps.
            Assert.Equal(41, Resolve("reboot_kill", Now.AddMinutes(-40)));
        }

        [Fact]
        public void Reboot_kill_without_a_boot_timestamp_falls_back_to_the_default()
        {
            // GetLastBootTimeFromEventLog returns null on a reduced-privilege run.
            Assert.Equal(ShellCoreTracker.BackfillLookbackMinutes, Resolve("reboot_kill"));
        }

        [Fact]
        public void A_downtime_shorter_than_the_default_does_not_shrink_the_window()
        {
            Assert.Equal(ShellCoreTracker.BackfillLookbackMinutes, Resolve("reboot_kill", Now.AddMinutes(-2)));
        }

        [Fact]
        public void A_boot_timestamp_in_the_future_falls_back_to_the_default()
        {
            // Clock skew across the reboot must not produce a negative window.
            Assert.Equal(ShellCoreTracker.BackfillLookbackMinutes, Resolve("reboot_kill", Now.AddMinutes(5)));
        }

        [Fact]
        public void A_very_long_downtime_is_clamped_by_the_tracker()
        {
            // The policy may propose more than the cap; ClampLookbackMinutes is the backstop, so
            // the two together can never produce an unbounded event-log query.
            var proposed = Resolve("reboot_kill", Now.AddHours(-20));
            Assert.True(proposed > ShellCoreTracker.BackfillLookbackMaxMinutes);
            Assert.Equal(ShellCoreTracker.BackfillLookbackMaxMinutes,
                ShellCoreTracker.ClampLookbackMinutes(proposed));
        }
    }
}
