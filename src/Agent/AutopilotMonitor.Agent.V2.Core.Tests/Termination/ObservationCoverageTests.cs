using System;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.Shared.Models;
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

        // --- DescribeLateStart: outcome-calibrated severity + phrasing (fleet 2026-07-28) ---

        private static void Describe(
            EnrollmentTerminationOutcome outcome, string oobeState,
            out EventSeverity severity, out string message, out string note)
            => ObservationCoverage.DescribeLateStart(
                outcome, oobeState, bootToStartSeconds: 34 * 60, uptimeSeconds: 30,
                out severity, out message, out note);

        [Fact]
        public void Failed_outcome_is_a_warning_with_the_hung_script_post_mortem_note()
        {
            Describe(EnrollmentTerminationOutcome.Failed, "in_progress",
                out var severity, out var message, out var note);

            Assert.Equal(EventSeverity.Warning, severity);
            Assert.Contains("low coverage of the enrollment window", message);
            Assert.Contains("post-mortem", note);
            Assert.Contains("hung ahead of the bootstrap", note);
        }

        [Fact]
        public void TimedOut_outcome_is_a_warning_like_a_failure()
        {
            Describe(EnrollmentTerminationOutcome.TimedOut, "in_progress",
                out var severity, out _, out var note);

            Assert.Equal(EventSeverity.Warning, severity);
            Assert.Contains("post-mortem", note);
        }

        [Fact]
        public void Succeeded_outcome_is_an_info_about_tail_only_coverage_without_alarm_framing()
        {
            // The 659c3a90 shape: healthy enrollment, bootstrap merely ran late in the ESP queue.
            Describe(EnrollmentTerminationOutcome.Succeeded, "in_progress",
                out var severity, out _, out var note);

            Assert.Equal(EventSeverity.Info, severity);
            Assert.Contains("succeeded", note);
            Assert.Contains("IME log replay", note);
            Assert.DoesNotContain("post-mortem", note);
            Assert.DoesNotContain("hung", note);
        }

        [Fact]
        public void Oobe_completed_at_start_phrases_a_pure_post_mortem()
        {
            Describe(EnrollmentTerminationOutcome.Failed, "completed",
                out _, out var message, out _);

            Assert.Contains("OOBE was already completed when the agent started", message);
        }

        [Fact]
        public void Oobe_in_progress_at_start_phrases_the_live_tail()
        {
            Describe(EnrollmentTerminationOutcome.Failed, "in_progress",
                out _, out var message, out _);

            Assert.Contains("OOBE was still in progress at agent start", message);
            Assert.Contains("final 30s live", message);
        }

        [Theory]
        [InlineData("not_started")]  // flips in after a mid-OOBE reboot — not interpretable
        [InlineData("unavailable")]  // WinRT contract absent / read failed
        [InlineData("unknown_3")]    // unexpected enum value
        public void Uninterpretable_oobe_states_add_no_claim_to_the_message(string oobeState)
        {
            Describe(EnrollmentTerminationOutcome.Failed, oobeState,
                out _, out var message, out _);

            Assert.DoesNotContain("OOBE", message);
            Assert.EndsWith("low coverage of the enrollment window.", message);
        }
    }
}
