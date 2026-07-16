using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.DeviceInfo;

/// <summary>
/// Pins the "no Autopilot profile" detection that drives the autopilot_profile_missing
/// warning event. Evidence-based: only an explicit ProfileAvailable=0 counts as missing —
/// an absent value is no evidence either way and must NOT trigger the event (a device
/// with a real profile has ProfileAvailable=1; older builds may not write the value).
/// Payload sample below mirrors the real-world stub Windows caches when the ZTD service
/// returns no profile (session 423b5360: empty ZeroTouchConfig, no tenant domain).
/// </summary>
public class DeviceInfoCollectorAutopilotProfileMissingTests
{
    // Real-world AutopilotPolicyCache stub of a device without an Autopilot profile:
    // PolicyJsonCache contains CloudAssignedAadServerData as an escaped JSON string
    // whose ZeroTouchConfig has an empty CloudAssignedTenantDomain.
    private const string PolicyJsonCacheWithoutProfile =
        "{\r\n  \"AutopilotCreationDate\": \"2026-07-16T13:48:31Z\",\r\n  \"AutopilotUpdateTimeout\": 1800000,\r\n  \"AutopilotMode\": 0,\r\n  \"AutopilotCorrelationVector\": \"W1D16N3gSUORhUV2.0\",\r\n  \"CloudAssignedAadServerData\": \"{\\u0022ZeroTouchConfig\\u0022:{\\u0022CloudAssignedTenantDomain\\u0022:\\u0022\\u0022,\\u0022CloudAssignedTenantUpn\\u0022:\\u0022\\u0022,\\u0022ForcedEnrollment\\u0022:0}}\"\r\n}";

    private const string PolicyJsonCacheWithProfile =
        "{\"AutopilotMode\": 0, \"CloudAssignedAadServerData\": \"{\\u0022ZeroTouchConfig\\u0022:{\\u0022CloudAssignedTenantDomain\\u0022:\\u0022contoso.com\\u0022,\\u0022ForcedEnrollment\\u0022:1}}\"}";

    [Fact]
    public void IsAutopilotProfileMissing_ProfileAvailableZero_ReturnsTrue()
    {
        var data = new Dictionary<string, object>
        {
            { "ProfileAvailable", "0" },
            { "PolicyJsonCache", PolicyJsonCacheWithoutProfile }
        };

        Assert.True(DeviceInfoCollector.IsAutopilotProfileMissing(data));
    }

    [Fact]
    public void IsAutopilotProfileMissing_ProfileAvailableOne_ReturnsFalse()
    {
        var data = new Dictionary<string, object> { { "ProfileAvailable", "1" } };

        Assert.False(DeviceInfoCollector.IsAutopilotProfileMissing(data));
    }

    [Fact]
    public void IsAutopilotProfileMissing_ValueAbsent_ReturnsFalse()
    {
        // No ProfileAvailable value = no evidence — must not claim the profile is missing.
        var data = new Dictionary<string, object> { { "PolicyJsonCache", PolicyJsonCacheWithProfile } };

        Assert.False(DeviceInfoCollector.IsAutopilotProfileMissing(data));
    }

    [Fact]
    public void IsAutopilotProfileMissing_NullData_ReturnsFalse()
    {
        Assert.False(DeviceInfoCollector.IsAutopilotProfileMissing(null));
    }

    [Fact]
    public void TryExtractZeroTouchTenantDomain_EmbeddedInPolicyJsonCache_EmptyDomain()
    {
        var data = new Dictionary<string, object> { { "PolicyJsonCache", PolicyJsonCacheWithoutProfile } };

        Assert.True(DeviceInfoCollector.TryExtractZeroTouchTenantDomain(data, out var domain));
        Assert.Equal(string.Empty, domain);
    }

    [Fact]
    public void TryExtractZeroTouchTenantDomain_EmbeddedInPolicyJsonCache_RealDomain()
    {
        var data = new Dictionary<string, object> { { "PolicyJsonCache", PolicyJsonCacheWithProfile } };

        Assert.True(DeviceInfoCollector.TryExtractZeroTouchTenantDomain(data, out var domain));
        Assert.Equal("contoso.com", domain);
    }

    [Fact]
    public void TryExtractZeroTouchTenantDomain_TopLevelServerData_Wins()
    {
        // When the registry exposes CloudAssignedAadServerData as its own value it is
        // preferred over the PolicyJsonCache-embedded copy.
        var data = new Dictionary<string, object>
        {
            { "CloudAssignedAadServerData", "{\"ZeroTouchConfig\":{\"CloudAssignedTenantDomain\":\"fabrikam.com\"}}" },
            { "PolicyJsonCache", PolicyJsonCacheWithProfile }
        };

        Assert.True(DeviceInfoCollector.TryExtractZeroTouchTenantDomain(data, out var domain));
        Assert.Equal("fabrikam.com", domain);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"CloudAssignedAadServerData\": 42}")]          // wrong type
    [InlineData("{\"SomethingElse\": true}")]                     // property absent
    public void TryExtractZeroTouchTenantDomain_UnusablePolicyJsonCache_ReturnsFalse(string policyJsonCache)
    {
        var data = new Dictionary<string, object> { { "PolicyJsonCache", policyJsonCache } };

        Assert.False(DeviceInfoCollector.TryExtractZeroTouchTenantDomain(data, out var domain));
        Assert.Null(domain);
    }

    [Fact]
    public void TryExtractZeroTouchTenantDomain_NullData_ReturnsFalse()
    {
        Assert.False(DeviceInfoCollector.TryExtractZeroTouchTenantDomain(null, out var domain));
        Assert.Null(domain);
    }
}
