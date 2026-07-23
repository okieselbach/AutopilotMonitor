#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Smoke tests for the WinRT <c>SystemSetupInfo.OutOfBoxExperienceState</c> reader.
    /// The actual state on the test machine is unknown (CI runners are post-OOBE; Server
    /// SKUs may lack the UniversalApiContract entirely), so only the fail-soft contract
    /// is pinned: never throw, always return a member of the known value set.
    /// </summary>
    public sealed class OobeStateReaderTests
    {
        [Fact]
        public void Read_never_throws_and_returns_known_value()
        {
            var value = OobeStateReader.Read();

            Assert.False(string.IsNullOrEmpty(value));
            Assert.True(
                value == "not_started"
                || value == "in_progress"
                || value == "completed"
                || value == OobeStateReader.Unavailable
                || value.StartsWith("unknown_"),
                $"unexpected reader value '{value}'");
        }

        [Fact]
        public void Read_is_stable_across_repeated_calls()
        {
            // Exercises the cached-PropertyInfo path (second call skips resolution).
            var first = OobeStateReader.Read();
            var second = OobeStateReader.Read();

            Assert.Equal(first, second);
        }
    }
}
