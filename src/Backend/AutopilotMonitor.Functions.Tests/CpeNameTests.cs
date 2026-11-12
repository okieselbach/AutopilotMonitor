using AutopilotMonitor.Functions.Services.Vulnerability;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeName"/> is the one parser for CPE 2.3 strings: product identity for grouping,
/// the version-less form mappings are keyed by, and the edition and platform fields the version
/// filter reads.
/// </summary>
public class CpeNameTests
{
    [Theory]
    [InlineData("cpe:2.3:a:7-zip:7-zip:24.08", "7-zip:7-zip")]
    [InlineData("cpe:2.3:a:7-zip:7-zip:", "7-zip:7-zip")]                                   // finding without a version
    [InlineData("cpe:2.3:a:Microsoft:Teams:1.6.00.4472", "microsoft:teams")]
    [InlineData("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*:1.2.3", "contoso:widget")]         // custom mapping typed in full
    [InlineData(@"cpe:2.3:a:microsoft:visual_c\+\+:14.0", @"microsoft:visual_c\+\+")]       // CPE escapes stay part of the key
    [InlineData("cpe:2.3:a:*:*:1.0", "")]
    [InlineData("cpe:2.3:a", "")]
    [InlineData("not a cpe:at:all:really:no", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ProductKeyOf_IsLowerCaseVendorAndProduct(string? cpe, string expected)
        => Assert.Equal(expected, CpeName.ProductKeyOf(cpe));

    [Theory]
    [InlineData("cpe:2.3:a:google:chrome", "cpe:2.3:a:google:chrome")]
    [InlineData("cpe:2.3:a:Google:Chrome:120.0.1:*:*:*:*:*:*:*", "cpe:2.3:a:google:chrome")]
    [InlineData("  cpe:2.3:a:mozilla:firefox:*:*:*:*:esr:*:*:*  ", "cpe:2.3:a:mozilla:firefox")]
    [InlineData("typed by hand", "typed by hand")]
    [InlineData(null, "")]
    public void ToProductUri_DropsEverythingAfterTheProduct(string? cpe, string expected)
        => Assert.Equal(expected, CpeName.ToProductUri(cpe));

    [Fact]
    public void TryParse_ReadsEditionAndPlatformFields()
    {
        Assert.True(CpeName.TryParse("cpe:2.3:a:mozilla:firefox:115.2.1:*:*:*:esr:android:arm64:*", out var name));

        Assert.Equal("a", name.Part);
        Assert.Equal("mozilla", name.Vendor);
        Assert.Equal("firefox", name.Product);
        Assert.Equal("115.2.1", name.Version);
        Assert.Equal("esr", name.SwEdition);
        Assert.Equal("android", name.TargetSw);
        Assert.Equal("arm64", name.TargetHw);
    }

    [Fact]
    public void TryParse_FillsMissingFieldsWithTheWildcard()
    {
        Assert.True(CpeName.TryParse("cpe:2.3:a:google:chrome", out var name));

        Assert.Equal("*", name.Version);
        Assert.Equal("*", name.SwEdition);
        Assert.Equal("*", name.TargetSw);
    }

    [Fact]
    public void TryParse_KeepsAnEscapedColonInsideItsField()
    {
        Assert.True(CpeName.TryParse(@"cpe:2.3:a:contoso:suite\:pro:2.0:*:*:*:*:*:*:*", out var name));

        Assert.Equal(@"suite\:pro", name.Product);
        Assert.Equal("2.0", name.Version);
    }

    [Fact]
    public void IsSameProduct_ComparesVendorAndProductNotThePrefix()
    {
        Assert.True(CpeName.TryParse("cpe:2.3:a:adobe:acrobat", out var acrobat));
        Assert.True(CpeName.TryParse("cpe:2.3:a:adobe:acrobat_reader_dc:23.006.20320:*:*:*:continuous:*:*:*", out var reader));
        Assert.True(CpeName.TryParse("cpe:2.3:a:Adobe:Acrobat:20.0:*:*:*:classic:*:*:*", out var acrobatClassic));

        Assert.False(acrobat.IsSameProduct(reader));
        Assert.True(acrobat.IsSameProduct(acrobatClassic));
    }
}
