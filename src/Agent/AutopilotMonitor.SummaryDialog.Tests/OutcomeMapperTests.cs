#nullable enable
using AutopilotMonitor.SummaryDialog.Models;
using Xunit;

namespace AutopilotMonitor.SummaryDialog.Tests
{
    /// <summary>
    /// Schema-version dispatch + outcome-string mapping. Pure-logic helpers extracted from
    /// the WPF MainWindow so the V1↔V2 schema branching is unit-testable without WPF.
    /// </summary>
    public sealed class OutcomeMapperTests
    {
        // ============================================================ IsV2Schema dispatch

        [Fact]
        public void IsV2Schema_returns_false_for_null()
        {
            Assert.False(OutcomeMapper.IsV2Schema(null!));
        }

        [Fact]
        public void IsV2Schema_returns_false_when_field_missing_or_zero()
        {
            // V1 agents wrote no schemaVersion field. Newtonsoft deserialises that as 0.
            var v1 = new FinalStatus { SchemaVersion = 0, Outcome = "completed" };
            Assert.False(OutcomeMapper.IsV2Schema(v1));
        }

        [Fact]
        public void IsV2Schema_returns_false_for_explicit_v1_value()
        {
            Assert.False(OutcomeMapper.IsV2Schema(new FinalStatus { SchemaVersion = 1 }));
        }

        [Fact]
        public void IsV2Schema_returns_true_for_v2_value()
        {
            Assert.True(OutcomeMapper.IsV2Schema(new FinalStatus { SchemaVersion = 2 }));
        }

        [Fact]
        public void IsV2Schema_returns_true_for_future_higher_versions()
        {
            // Forward-compat: a future schema 3 should still hit the V2 renderer (which
            // handles the additive fields gracefully). Better than crashing or silently
            // falling back to V1 rendering, which would lose the granular outcome states.
            Assert.True(OutcomeMapper.IsV2Schema(new FinalStatus { SchemaVersion = 3 }));
        }

        // ============================================================ V1 outcome mapping

        [Theory]
        [InlineData("completed", OutcomeKind.Success)]
        [InlineData("COMPLETED", OutcomeKind.Success)]   // V1 dialog used case-insensitive match
        [InlineData("failed", OutcomeKind.Failure)]
        [InlineData("anything-else", OutcomeKind.Failure)]
        [InlineData("", OutcomeKind.Failure)]
        public void Map_v1_collapses_outcome_to_success_or_failure(string outcome, OutcomeKind expected)
        {
            var status = new FinalStatus { SchemaVersion = 0, Outcome = outcome };
            Assert.Equal(expected, OutcomeMapper.Map(status));
        }

        // ============================================================ V2 outcome mapping

        [Theory]
        [InlineData("succeeded", OutcomeKind.Success)]
        [InlineData("Succeeded", OutcomeKind.Success)]
        [InlineData("completed", OutcomeKind.Success)]   // defensive: accept V1 string under V2 schema
        [InlineData("whiteglove_part1", OutcomeKind.PreProvisioningComplete)]
        [InlineData("timed_out", OutcomeKind.TimedOut)]
        [InlineData("failed", OutcomeKind.Failure)]
        [InlineData("unknown", OutcomeKind.Unknown)]
        [InlineData("not_a_real_outcome", OutcomeKind.Unknown)]
        [InlineData("", OutcomeKind.Unknown)]
        public void Map_v2_resolves_five_kinds(string outcome, OutcomeKind expected)
        {
            var status = new FinalStatus { SchemaVersion = 2, Outcome = outcome };
            Assert.Equal(expected, OutcomeMapper.Map(status));
        }

        [Fact]
        public void Map_returns_unknown_for_null_status()
        {
            Assert.Equal(OutcomeKind.Unknown, OutcomeMapper.Map(null!));
        }

        // ============================================================ Header / banner text

        [Theory]
        [InlineData(OutcomeKind.Success, "Enrollment Completed Successfully")]
        [InlineData(OutcomeKind.PreProvisioningComplete, "Pre-Provisioning Complete")]
        [InlineData(OutcomeKind.TimedOut, "Enrollment Timed Out")]
        [InlineData(OutcomeKind.Failure, "Enrollment Failed")]
        [InlineData(OutcomeKind.Unknown, "Enrollment Status Unknown")]
        public void HeaderText_is_user_visible_per_state(OutcomeKind kind, string expected)
        {
            Assert.Equal(expected, OutcomeMapper.HeaderText(kind));
        }

        [Fact]
        public void DefaultBannerText_empty_for_success()
        {
            // Success has no banner — clean UI when everything worked.
            Assert.Equal(string.Empty, OutcomeMapper.DefaultBannerText(OutcomeKind.Success));
        }

        [Theory]
        [InlineData(OutcomeKind.PreProvisioningComplete)]
        [InlineData(OutcomeKind.TimedOut)]
        [InlineData(OutcomeKind.Failure)]
        [InlineData(OutcomeKind.Unknown)]
        public void DefaultBannerText_non_empty_for_actionable_states(OutcomeKind kind)
        {
            // Non-success states must have a fallback banner so the user always sees
            // something when the agent didn't supply a specific failureReason.
            Assert.False(string.IsNullOrWhiteSpace(OutcomeMapper.DefaultBannerText(kind)));
        }

        // ============================================================ Duration formatting

        [Theory]
        [InlineData(0, "")]
        [InlineData(-5, "")]
        [InlineData(45, "45s")]
        [InlineData(60, "1m 0s")]
        [InlineData(90, "1m 30s")]
        [InlineData(3599, "59m 59s")]
        [InlineData(3600, "1h 0m")]
        [InlineData(7200, "2h 0m")]
        [InlineData(7305, "2h 1m")]
        public void FormatDuration_picks_compact_unit_for_app_row(double seconds, string expected)
        {
            Assert.Equal(expected, OutcomeMapper.FormatDuration(seconds));
        }
    }
}
