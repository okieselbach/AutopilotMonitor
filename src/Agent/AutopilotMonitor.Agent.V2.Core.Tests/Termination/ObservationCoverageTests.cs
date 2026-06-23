using System;
using AutopilotMonitor.Agent.V2.Core.Termination;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Termination
{
    /// <summary>
    /// Gate for the "low observation coverage" assessment shared by the <c>agent_late_start</c>
    /// event and final-status.json. Late start AND short uptime is the signature of the agent
    /// arriving after the enrollment had already decided (the c3e0124c late-bootstrap case).
    /// </summary>
    public sealed class ObservationCoverageTests
    {
        private static readonly DateTime Boot = new DateTime(2026, 6, 22, 20, 21, 0, DateTimeKind.Utc);

        [Fact]
        public void Late_start_and_short_uptime_is_low_coverage()
        {
            // Agent started 34 min after boot, lived 30 s — the f148976f shape.
            var start = Boot.AddMinutes(34);
            var terminated = start.AddSeconds(30);

            var low = ObservationCoverage.IsLowObservationCoverage(
                start, terminated, Boot, out var bootToStart, out var uptime);

            Assert.True(low);
            Assert.Equal(34 * 60, bootToStart, 0);
            Assert.Equal(30, uptime, 0);
        }

        [Fact]
        public void Late_start_but_long_uptime_is_not_low_coverage()
        {
            // Started late but then observed for 40 min — coverage recovered.
            var start = Boot.AddMinutes(34);
            var terminated = start.AddMinutes(40);

            var low = ObservationCoverage.IsLowObservationCoverage(
                start, terminated, Boot, out _, out _);

            Assert.False(low);
        }

        [Fact]
        public void Prompt_start_and_short_uptime_is_not_low_coverage()
        {
            // A genuinely fast enrollment that the agent observed from the start.
            var start = Boot.AddMinutes(2);
            var terminated = start.AddSeconds(30);

            var low = ObservationCoverage.IsLowObservationCoverage(
                start, terminated, Boot, out var bootToStart, out _);

            Assert.False(low);
            Assert.Equal(2 * 60, bootToStart, 0);
        }

        [Fact]
        public void Out_params_are_clamped_at_zero_for_inverted_inputs()
        {
            // Defensive: a boot anchor after agent start (clock weirdness) must not yield negatives.
            var start = Boot;
            var terminated = Boot.AddSeconds(10);
            var bootAfterStart = Boot.AddMinutes(5);

            var low = ObservationCoverage.IsLowObservationCoverage(
                start, terminated, bootAfterStart, out var bootToStart, out var uptime);

            Assert.False(low);
            Assert.Equal(0, bootToStart, 0);
            Assert.Equal(10, uptime, 0);
        }
    }
}
