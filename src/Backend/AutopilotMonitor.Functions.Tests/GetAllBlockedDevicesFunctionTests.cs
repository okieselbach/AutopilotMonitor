using AutopilotMonitor.Functions.Functions.Admin;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the tenantId-filter classification in <see cref="GetAllBlockedDevicesFunction"/>.
/// <para>
/// The HTTP entry point is intentionally not tested here (mocking <c>HttpRequestData</c> +
/// the middleware chain is more setup than the test is worth — same rationale as
/// <see cref="DeviceBlockFunctionTests"/>). The regression this guards against: a supplied
/// <c>?tenantId=</c> used to be ignored entirely, silently widening the result to a
/// cross-tenant view, and an invalid GUID was never rejected.
/// </para>
/// </summary>
public class GetAllBlockedDevicesFunctionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ParseTenantFilter_MissingOrBlank_YieldsAll(string? raw)
    {
        var (kind, tenantId) = GetAllBlockedDevicesFunction.ParseTenantFilter(raw);

        Assert.Equal(GetAllBlockedDevicesFunction.TenantFilterKind.All, kind);
        Assert.Null(tenantId);
    }

    [Theory]
    [InlineData("806f61c3-1978-4e5c-8fd7-a571cb0fe6bc", "806f61c3-1978-4e5c-8fd7-a571cb0fe6bc")]
    [InlineData("  806f61c3-1978-4e5c-8fd7-a571cb0fe6bc  ", "806f61c3-1978-4e5c-8fd7-a571cb0fe6bc")]
    public void ParseTenantFilter_ValidGuid_YieldsScopedTrimmed(string raw, string expected)
    {
        var (kind, tenantId) = GetAllBlockedDevicesFunction.ParseTenantFilter(raw);

        Assert.Equal(GetAllBlockedDevicesFunction.TenantFilterKind.Scoped, kind);
        Assert.Equal(expected, tenantId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("099")]
    [InlineData("806f61c3-1978-4e5c-8fd7")]
    [InlineData("' OR 1=1 --")]
    public void ParseTenantFilter_NonGuid_YieldsInvalid(string raw)
    {
        var (kind, tenantId) = GetAllBlockedDevicesFunction.ParseTenantFilter(raw);

        Assert.Equal(GetAllBlockedDevicesFunction.TenantFilterKind.Invalid, kind);
        Assert.Null(tenantId);
    }
}
