using System;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.State
{
    /// <summary>
    /// Basic invariants for <see cref="ClassifierOutcomes"/>. Codex follow-up #5.
    /// </summary>
    public sealed class ClassifierOutcomesTests
    {
        [Fact]
        public void Empty_bothHypothesesAreUnknown()
        {
            var o = ClassifierOutcomes.Empty;

            Assert.Equal(HypothesisLevel.Unknown, o.WhiteGloveSealing.Level);
            Assert.Equal(HypothesisLevel.Unknown, o.DeviceOnlyDeployment.Level);
        }

        [Fact]
        public void WithWhiteGloveSealing_setsFirstSlotOnly()
        {
            var h = Hypothesis.UnknownInstance.With(
                level: HypothesisLevel.Confirmed,
                reason: "classifier_confirmed",
                score: 80,
                lastUpdatedUtc: DateTime.UtcNow);

            var outcomes = ClassifierOutcomes.Empty.WithWhiteGloveSealing(h);

            Assert.Equal(HypothesisLevel.Confirmed, outcomes.WhiteGloveSealing.Level);
            Assert.Equal(HypothesisLevel.Unknown, outcomes.DeviceOnlyDeployment.Level);
        }

        [Fact]
        public void WithDeviceOnlyDeployment_preservesOtherSlots()
        {
            var wg = Hypothesis.UnknownInstance.With(level: HypothesisLevel.Weak, score: 40, lastUpdatedUtc: DateTime.UtcNow);
            var outcomes = ClassifierOutcomes.Empty
                .WithWhiteGloveSealing(wg)
                .WithDeviceOnlyDeployment(Hypothesis.UnknownInstance.With(level: HypothesisLevel.Confirmed, score: 100, lastUpdatedUtc: DateTime.UtcNow));

            Assert.Equal(HypothesisLevel.Weak, outcomes.WhiteGloveSealing.Level);
            Assert.Equal(HypothesisLevel.Confirmed, outcomes.DeviceOnlyDeployment.Level);
        }

        [Fact]
        public void Constructor_rejectsNullHypothesis()
        {
            Assert.Throws<ArgumentNullException>(() => new ClassifierOutcomes(
                whiteGloveSealing: null!,
                deviceOnlyDeployment: Hypothesis.UnknownInstance));
        }

        [Fact]
        public void With_nullValue_throws()
        {
            Assert.Throws<ArgumentNullException>(() => ClassifierOutcomes.Empty.WithWhiteGloveSealing(null!));
            Assert.Throws<ArgumentNullException>(() => ClassifierOutcomes.Empty.WithDeviceOnlyDeployment(null!));
        }
    }
}
