#nullable enable
using AutopilotMonitor.DecisionCore.Engine;
using SharedConstants = AutopilotMonitor.Shared.Constants;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Smoke tests for the lifecycle-anchor allowlist (Plan §A — Edge-Triggered
    /// State Snapshots, 2026-05-03). The set is the single source of truth for
    /// "which events should carry a DecisionState snapshot"; these tests pin
    /// the membership so future drift (someone adding an EventType to the snapshot
    /// pipeline without adding it here, or removing one inadvertently) is caught.
    /// </summary>
    public sealed class LifecycleAnchorEventTypesTests
    {
        [Theory]
        [InlineData(nameof(SharedConstants.EventTypes.AgentStarted))]
        [InlineData(nameof(SharedConstants.EventTypes.EspPhaseChanged))]
        [InlineData(nameof(SharedConstants.EventTypes.NetworkStateChange))]
        [InlineData(nameof(SharedConstants.EventTypes.DesktopArrived))]
        [InlineData(nameof(SharedConstants.EventTypes.AadPlaceholderUserDetected))]
        [InlineData(nameof(SharedConstants.EventTypes.AadUserJoinedObserved))]
        [InlineData(nameof(SharedConstants.EventTypes.HybridLoginPending))]
        [InlineData(nameof(SharedConstants.EventTypes.AgentShuttingDown))]
        [InlineData(nameof(SharedConstants.EventTypes.SystemRebootDetected))]
        [InlineData(nameof(SharedConstants.EventTypes.PerformanceCollectorStopped))]
        [InlineData(nameof(SharedConstants.EventTypes.AgentMetricsCollectorStopped))]
        [InlineData(nameof(SharedConstants.EventTypes.PriorRunDiedWithState))]
        public void Contains_returns_true_for_every_anchor(string constantName)
        {
            // Resolve the const value via reflection — InlineData wants a constant expression
            // and we want the test name to read like the constant name.
            var value = (string)typeof(SharedConstants.EventTypes)
                .GetField(constantName)!
                .GetRawConstantValue()!;

            Assert.True(LifecycleAnchorEventTypes.Contains(value),
                $"Anchor '{constantName}' (value '{value}') is missing from the allowlist.");
        }

        [Theory]
        [InlineData(nameof(SharedConstants.EventTypes.EnrollmentComplete))]
        [InlineData(nameof(SharedConstants.EventTypes.EnrollmentFailed))]
        [InlineData(nameof(SharedConstants.EventTypes.AppInstallStart))]
        [InlineData(nameof(SharedConstants.EventTypes.AppInstallComplete))]
        [InlineData(nameof(SharedConstants.EventTypes.PerformanceSnapshot))]
        [InlineData(nameof(SharedConstants.EventTypes.DownloadProgress))]
        public void Contains_returns_false_for_non_anchor_event_types(string constantName)
        {
            // enrollment_complete / _failed already carry DecisionAuditTrail typedPayload
            // (would clobber). High-frequency events (App-Install, performance_snapshot,
            // download_progress) deliberately excluded — would dominate the Events table.
            var value = (string)typeof(SharedConstants.EventTypes)
                .GetField(constantName)!
                .GetRawConstantValue()!;

            Assert.False(LifecycleAnchorEventTypes.Contains(value),
                $"'{constantName}' (value '{value}') must NOT be on the allowlist.");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("totally_unknown_event_type")]
        public void Contains_returns_false_for_null_empty_or_unknown(string? eventType)
        {
            Assert.False(LifecycleAnchorEventTypes.Contains(eventType));
        }

        [Fact]
        public void Allowlist_count_matches_planned_anchor_count()
        {
            // Plan §A originally documented 13 anchors; the WG-resume cleanup (2026-05-04)
            // dropped the V2-only post-reseal real-user sign-in anchor — the Classic
            // aad_user_joined_observed event covers both Part-1 and the post-reseal join
            // now. Drift from 12 is a contract change that needs a paired plan update.
            Assert.Equal(12, LifecycleAnchorEventTypes.Count);
        }
    }
}
