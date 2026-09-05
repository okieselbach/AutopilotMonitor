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
    [Fact]
    public void MaxSessionIds_Matches_Repository_Default_Cap()
    {
        // The truncated flag compares against this constant; it must stay in sync
        // with the GetRuleHitSessionIdsAsync default cap (2000).
        Assert.Equal(2000, RuleHitSessionsFunction.MaxSessionIds);
    }
}
