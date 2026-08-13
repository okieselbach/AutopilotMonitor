using AutopilotMonitor.Functions.Functions.Metrics;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Window parsing for the fleet-context deep-link endpoint
/// (<see cref="RuleHitSessionsFunction"/>, GET metrics/rule-hit-sessions).
/// Default 14 days; clamped to 1..90 because RuleResults follow the session
/// retention cascade — a larger window could never widen the hit set.
/// </summary>
public class RuleHitSessionsFunctionTests
{
    [Theory]
    [InlineData(null, 14)]        // not supplied → default window
    [InlineData("", 14)]          // empty → default
    [InlineData("garbage", 14)]   // non-numeric → default
    [InlineData("14", 14)]
    [InlineData("1", 1)]
    [InlineData("0", 1)]          // below range → clamped up
    [InlineData("-5", 1)]
    [InlineData("90", 90)]
    [InlineData("365", 90)]       // above range → clamped down
    public void ParseDays_Defaults_And_Clamps(string? raw, int expected)
    {
        Assert.Equal(expected, RuleHitSessionsFunction.ParseDays(raw));
    }

    [Fact]
    public void MaxSessionIds_Matches_Repository_Default_Cap()
    {
        // The truncated flag compares against this constant; it must stay in sync
        // with the GetRuleHitSessionIdsAsync default cap (2000).
        Assert.Equal(2000, RuleHitSessionsFunction.MaxSessionIds);
    }
}
