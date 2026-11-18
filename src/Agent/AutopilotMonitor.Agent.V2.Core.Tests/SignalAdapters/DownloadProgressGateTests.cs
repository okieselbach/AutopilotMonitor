#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// D-444: which download progress samples become download_progress events — one stream per
    /// download, 3 s for almost every session, slower stages for long downloads, a live first minute
    /// for every download.
    /// </summary>
    public sealed class DownloadProgressGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        private const string App = "app-1";
        private const long Total = 1_000_000_000;

        private static (DownloadProgressGate Gate, DownloadProgressBudget Budget, VirtualClock Clock) Make(int eventsSent = 0)
        {
            var budget = new DownloadProgressBudget();
            if (eventsSent > 0)
                budget.Restore(new DownloadProgressBudgetState { EventsSent = eventsSent, Stage = DownloadProgressGate.StageFor(eventsSent) });
            var clock = new VirtualClock(T0);
            return (new DownloadProgressGate(budget, clock), budget, clock);
        }

        private static DownloadProgressVerdict EmitIfAllowed(DownloadProgressGate gate, DownloadProgressSource source, long bytes, DateTime at)
        {
            var verdict = gate.Evaluate(App, source, bytes, Total, at);
            if (verdict == DownloadProgressVerdict.Emit) gate.RecordEmitted(App, bytes, Total, at);
            return verdict;
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(399, 0)]
        [InlineData(400, 1)]
        [InlineData(639, 1)]
        [InlineData(640, 2)]
        [InlineData(759, 2)]
        [InlineData(760, 3)]
        [InlineData(100_000, 3)]
        public void Stage_follows_the_session_total_at_its_thresholds(int eventsSent, int expectedStage)
        {
            Assert.Equal(expectedStage, DownloadProgressGate.StageFor(eventsSent));
        }

        [Fact]
        public void No_interval_reaches_the_first_stall_probe()
        {
            // Progress counts as activity; a slower stage must not let the stall probe fire mid-download.
            var firstProbe = TimeSpan.FromMinutes(new AutopilotMonitor.Shared.Models.CollectorConfiguration().StallProbeThresholdsMinutes[0]);
            foreach (var (_, intervalSeconds) in DownloadProgressGate.Stages)
                Assert.True(TimeSpan.FromSeconds(intervalSeconds) < firstProbe);
            Assert.True(DownloadProgressGate.LiveStart < firstProbe);
        }

        [Fact]
        public void Interval_is_3_10_30_60_seconds_and_clamped()
        {
            Assert.Equal(3, DownloadProgressGate.IntervalSecondsFor(-1));
            Assert.Equal(3, DownloadProgressGate.IntervalSecondsFor(0));
            Assert.Equal(10, DownloadProgressGate.IntervalSecondsFor(1));
            Assert.Equal(30, DownloadProgressGate.IntervalSecondsFor(2));
            Assert.Equal(60, DownloadProgressGate.IntervalSecondsFor(3));
            Assert.Equal(60, DownloadProgressGate.IntervalSecondsFor(9));
        }

        [Fact]
        public void Stage_0_sends_every_3_second_DO_sample_like_before()
        {
            var (gate, _, _) = Make();
            for (var i = 0; i < 40; i++)
            {
                var verdict = EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, (i + 1) * 1_048_576L, T0.AddSeconds(i * 3));
                Assert.Equal(DownloadProgressVerdict.Emit, verdict);
            }
        }

        [Fact]
        public void First_sample_is_sent_and_an_unchanged_repeat_never()
        {
            var (gate, _, _) = Make();
            Assert.Equal(DownloadProgressVerdict.Emit, EmitIfAllowed(gate, DownloadProgressSource.Ime, 0, T0));
            Assert.Equal(DownloadProgressVerdict.Drop, gate.Evaluate(App, DownloadProgressSource.Ime, 0, Total, T0.AddMinutes(5)));
        }

        [Fact]
        public void A_sample_inside_the_interval_is_held()
        {
            var (gate, _, _) = Make();
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 1_000, T0);
            Assert.Equal(DownloadProgressVerdict.Hold, gate.Evaluate(App, DownloadProgressSource.DeliveryOptimization, 2_000, Total, T0.AddSeconds(1)));
            Assert.Equal(DownloadProgressVerdict.Emit, gate.Evaluate(App, DownloadProgressSource.DeliveryOptimization, 3_000, Total, T0.AddSeconds(2.6)));
        }

        [Fact]
        public void Completion_is_sent_even_inside_the_interval()
        {
            var (gate, _, _) = Make(eventsSent: 760);
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 1_000, T0.AddMinutes(-10));
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 2_000, T0);
            Assert.Equal(DownloadProgressVerdict.Emit, gate.Evaluate(App, DownloadProgressSource.DeliveryOptimization, Total, Total, T0.AddSeconds(1)));
        }

        [Fact]
        public void IME_lines_are_dropped_while_DO_reports_the_app()
        {
            var (gate, _, clock) = Make();
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 1_000, clock.UtcNow);

            clock.Advance(TimeSpan.FromSeconds(9));
            Assert.Equal(DownloadProgressVerdict.Drop, gate.Evaluate(App, DownloadProgressSource.Ime, 5_000, Total, clock.UtcNow));
        }

        [Fact]
        public void IME_lines_take_over_when_DO_goes_quiet()
        {
            var (gate, _, clock) = Make();
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 1_000, clock.UtcNow);

            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal(DownloadProgressVerdict.Emit, gate.Evaluate(App, DownloadProgressSource.Ime, 5_000, Total, clock.UtcNow));
        }

        [Fact]
        public void DO_preference_is_per_app()
        {
            var (gate, _, clock) = Make();
            gate.Evaluate("app-a", DownloadProgressSource.DeliveryOptimization, 1_000, Total, clock.UtcNow);
            Assert.Equal(DownloadProgressVerdict.Emit, gate.Evaluate("app-b", DownloadProgressSource.Ime, 1_000, Total, clock.UtcNow));
        }

        [Fact]
        public void A_throttled_stage_applies_after_the_live_start()
        {
            var (gate, _, _) = Make(eventsSent: 400);
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 1_000, T0);                 // first byte at T0
            var t = T0.AddSeconds(61);
            Assert.Equal(DownloadProgressVerdict.Emit, EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 2_000, t));
            Assert.Equal(DownloadProgressVerdict.Hold, gate.Evaluate(App, DownloadProgressSource.DeliveryOptimization, 3_000, Total, t.AddSeconds(3)));
            Assert.Equal(DownloadProgressVerdict.Emit, gate.Evaluate(App, DownloadProgressSource.DeliveryOptimization, 3_000, Total, t.AddSeconds(10)));
        }

        [Fact]
        public void Every_download_reports_its_first_minute_every_3_seconds_even_in_the_slowest_stage()
        {
            var (gate, _, _) = Make(eventsSent: 760);
            var sent = 0;
            for (var i = 0; i <= 30; i++)
            {
                if (EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, (i + 1) * 1_000L, T0.AddSeconds(i * 3)) == DownloadProgressVerdict.Emit)
                    sent++;
            }
            // 0..57 s: every sample (20); 60..90 s: one per 60 s at most.
            Assert.Equal(20, sent);
        }

        [Fact]
        public void The_live_start_counts_from_the_first_byte_not_from_the_queued_0_percent_line()
        {
            var (gate, _, clock) = Make(eventsSent: 760);
            EmitIfAllowed(gate, DownloadProgressSource.Ime, 0, T0);                                       // queued, no byte yet
            var firstByte = T0.AddSeconds(63);
            clock.SetUtcNow(firstByte);
            Assert.Equal(DownloadProgressVerdict.Emit, EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 3_000_000, firstByte));
            Assert.Equal(DownloadProgressVerdict.Emit, EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 6_000_000, firstByte.AddSeconds(3)));
        }

        [Fact]
        public void A_clock_step_backwards_does_not_mute_the_app()
        {
            var (gate, _, _) = Make(eventsSent: 760);
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 1_000, T0.AddHours(7));
            Assert.Equal(DownloadProgressVerdict.Emit, gate.Evaluate(App, DownloadProgressSource.DeliveryOptimization, 2_000, Total, T0));
        }

        [Fact]
        public void Recording_returns_each_stage_once_when_the_session_crosses_it()
        {
            var (gate, budget, _) = Make(eventsSent: 398);
            Assert.Null(gate.RecordSessionEvent());                     // 399
            Assert.Equal(1, gate.RecordSessionEvent());                 // 400
            Assert.Null(gate.RecordSessionEvent());                     // 401
            for (var i = 402; i < 640; i++) Assert.Null(gate.RecordSessionEvent());
            Assert.Equal(2, gate.RecordSessionEvent());                 // 640
            Assert.Equal(640, budget.EventsSent);
        }

        [Fact]
        public void A_restored_stage_is_not_announced_again()
        {
            var (gate, _, _) = Make(eventsSent: 700);
            for (var i = 701; i < 760; i++) Assert.Null(gate.RecordSessionEvent());
            Assert.Equal(3, gate.RecordSessionEvent());                 // 760: next stage only
        }

        [Fact]
        public void A_new_download_of_the_same_app_starts_fresh_after_EndDownload()
        {
            var (gate, _, _) = Make(eventsSent: 760);
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 1_000, T0.AddMinutes(-30));
            EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 5_000, T0);
            gate.EndDownload(App);

            Assert.Equal(DownloadProgressVerdict.Emit, EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 5_000, T0.AddSeconds(1)));
            Assert.Equal(DownloadProgressVerdict.Emit, EmitIfAllowed(gate, DownloadProgressSource.DeliveryOptimization, 9_000, T0.AddSeconds(4)));
        }
    }
}
