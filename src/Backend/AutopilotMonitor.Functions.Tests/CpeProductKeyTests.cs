using AutopilotMonitor.Functions.Services.Vulnerability;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeMatchScorer.ProductKey"/> turns a finding's CPE ("base CPE:installed version")
/// into the product identity the exposure summary groups by.
/// </summary>
public class CpeProductKeyTests
{
    [Theory]
    [InlineData("cpe:2.3:a:7-zip:7-zip:24.08", "7-zip:7-zip")]
    [InlineData("cpe:2.3:a:7-zip:7-zip:", "7-zip:7-zip")]                                   // finding without a version
    [InlineData("cpe:2.3:a:Microsoft:Teams:1.6.00.4472", "microsoft:teams")]
    [InlineData("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*:1.2.3", "contoso:widget")]         // custom mapping typed in full
    [InlineData(@"cpe:2.3:a:microsoft:visual_c\+\+:14.0", @"microsoft:visual_c\+\+")]       // CPE escapes stay part of the key
    [InlineData("cpe:2.3:a:*:*:1.0", "")]
    [InlineData("cpe:2.3:a", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ProductKey_IsLowerCaseVendorAndProduct(string? cpe, string expected)
        => Assert.Equal(expected, CpeMatchScorer.ProductKey(cpe));
}
