using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.State
{
    public sealed class SessionStageExtensionsTests
    {
        [Theory]
        [InlineData(SessionStage.Completed, true)]
        [InlineData(SessionStage.Failed, true)]
        [InlineData(SessionStage.WhiteGloveSealed, true)]
        [InlineData(SessionStage.Unknown, false)]
        [InlineData(SessionStage.SessionStarted, false)]
        [InlineData(SessionStage.AwaitingHello, false)]
        [InlineData(SessionStage.EspAccountSetup, false)]
        [InlineData(SessionStage.WhiteGloveCandidate, false)]
        public void IsTerminal_classifies_stages_correctly(SessionStage stage, bool expected)
        {
            Assert.Equal(expected, stage.IsTerminal());
        }

        [Theory]
        [InlineData(SessionStage.WhiteGloveSealed, true)]
        [InlineData(SessionStage.Completed, false)]
        [InlineData(SessionStage.Failed, false)]
        [InlineData(SessionStage.WhiteGloveCandidate, false)]
        public void IsPauseBeforePart2_only_true_for_WhiteGloveSealed(SessionStage stage, bool expected)
        {
            Assert.Equal(expected, stage.IsPauseBeforePart2());
        }
    }
}
