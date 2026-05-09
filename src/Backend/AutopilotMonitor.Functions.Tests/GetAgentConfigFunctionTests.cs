using AutopilotMonitor.Functions.Functions.Config;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="GetAgentConfigFunction.ParseAgentMajor"/> — the pure decision logic that picks
/// the per-line hash-oracle field set based on the X-Agent-Version header value.
/// </summary>
public class GetAgentConfigFunctionTests
{
    [Theory]
    // V2 agents (major == 2) — route to V2 hash oracle.
    [InlineData("2.0.114", 2)]
    [InlineData("2.0.0", 2)]
    [InlineData("2.1.3", 2)]
    [InlineData("2.0.114+06bbf13dbeedc", 2)]  // SemVer build-metadata suffix
    // V1 agents (major == 1) — V1 hash oracle.
    [InlineData("1.0.1041", 1)]
    [InlineData("1.5.0", 1)]
    [InlineData("0.9.0", 0)]                  // pre-1.0 agents (none in field, but parser is honest)
    // Future V3+ — parser supports it; backend GetAgentLine returns empty for unwired majors.
    [InlineData("3.0.0", 3)]
    // Backward compat — missing/unparsable headers default to V1 so very old agents keep working.
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("not-a-version", 1)]
    [InlineData("vNEXT", 1)]
    public void ParseAgentMajor_ExtractsMajor(string? agentVersion, int expectedMajor)
    {
        var actual = GetAgentConfigFunction.ParseAgentMajor(agentVersion);
        Assert.Equal(expectedMajor, actual);
    }
}
