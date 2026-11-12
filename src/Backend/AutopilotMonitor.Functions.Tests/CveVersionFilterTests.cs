using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CveVersionFilter"/> judges an installed version only by ranges that belong to the
/// product itself, fit the software edition its mapping asks for and count versions the way the
/// installed one does. Each rule here removes a class of CVEs a product was reported for without
/// having them. The platform and plain range rules are in <c>VulnerabilityCorrelationServiceTests</c>.
/// </summary>
public class CveVersionFilterTests
{
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string FirefoxCpe = "cpe:2.3:a:mozilla:firefox";

    private static CachedVersionRange Below(string criteria, string? fixedIn = null) =>
        new() { Criteria = criteria, VersionEndExcluding = fixedIn };

    private static CachedCve Cve(string id, params CachedVersionRange[] ranges) => new()
    {
        CveId = id,
        CvssScore = 8.8,
        CvssSeverity = "HIGH",
        Description = "Synthetic description.",
        PublishedDate = "2026-06-01T00:00:00.000",
        AffectedVersions = ranges.ToList(),
    };

    // -----------------------------------------------------------------------
    // Ranges of another product (rows written before the projection dropped them)
    // -----------------------------------------------------------------------

    [Fact]
    public void RangeOfAnotherProductInTheSameCve_DoesNotJudgeTheInstalledVersion()
    {
        var cve = Cve("CVE-2024-3661",
            Below("cpe:2.3:a:contoso:widget:-:*:*:*:*:*:*:*"),
            Below("cpe:2.3:a:fabrikam:secure_client:*:*:*:*:*:*:*:*", "24.06.1"));

        var result = CveVersionFilter.Filter(new[] { cve }, WidgetCpe, "3.2.0");

        Assert.Empty(result.Vulnerabilities);
        Assert.Equal(0, result.IncomparableCves);
    }

    [Theory]
    [InlineData("cpe:2.3:a:adobe:acrobat_reader_dc:*:*:*:*:*:*:*:*")] // shares the prefix, is another product
    [InlineData("cpe:2.3:a:fabrikam:acrobat:*:*:*:*:*:*:*:*")]         // same product name, another vendor
    [InlineData("cpe:2.3:o:microsoft:windows:*:*:*:*:*:*:*:*")]
    public void ProductIsComparedByVendorAndProduct_NotByPrefix(string foreignCriteria)
    {
        var cve = Cve("CVE-2025-0101", Below(foreignCriteria, "99.0"));

        Assert.Empty(CveVersionFilter.Filter(new[] { cve }, "cpe:2.3:a:adobe:acrobat", "24.0").Vulnerabilities);
    }

    // -----------------------------------------------------------------------
    // Software editions
    // -----------------------------------------------------------------------

    /// <summary>Shaped like the public CVE-2023-4863 for Firefox: one fix for the release channel, one for ESR.</summary>
    private static readonly CachedCve FirefoxAndItsEsr = Cve("CVE-2023-4863",
        Below("cpe:2.3:a:mozilla:firefox:*:*:*:*:-:*:*:*", "117.0.1"),
        Below("cpe:2.3:a:mozilla:firefox:*:*:*:*:esr:*:*:*", "115.2.1"));

    [Theory]
    // Firefox, mapped with "never the ESR ranges"
    [InlineData("116.0", null, true, true)]
    [InlineData("117.0.1", null, true, false)]
    // Firefox ESR, mapped with "only the ESR ranges"
    [InlineData("115.2.0", "esr", false, true)]
    [InlineData("115.3.0", "esr", false, false)]
    // Without an edition on the mapping the fixed ESR build is judged by the release-channel
    // range: the false positive the edition setting removes.
    [InlineData("115.3.0", null, false, true)]
    public void Edition_DecidesWhichRangesCount(string installed, string? edition, bool excludeEsr, bool affected)
    {
        var result = CveVersionFilter.Filter(
            new[] { FirefoxAndItsEsr }, FirefoxCpe, installed, edition,
            excludeEsr ? new[] { "esr" } : null);

        Assert.Equal(affected, result.Vulnerabilities.Count == 1);
    }

    [Fact]
    public void WithoutEditionOnTheMapping_EveryEditionCounts()
    {
        const string readerCpe = "cpe:2.3:a:adobe:acrobat_reader_dc";
        var cve = Cve("CVE-2025-0102", Below("cpe:2.3:a:adobe:acrobat_reader_dc:*:*:*:*:continuous:*:*:*", "24.002.20759"));

        Assert.Single(CveVersionFilter.Filter(new[] { cve }, readerCpe, "23.008.20470").Vulnerabilities);
        Assert.Empty(CveVersionFilter.Filter(new[] { cve }, readerCpe, "23.008.20470", edition: "classic").Vulnerabilities);
        Assert.Empty(CveVersionFilter.Filter(new[] { cve }, readerCpe, "23.008.20470", excludeEditions: new[] { "continuous" }).Vulnerabilities);
    }

    // -----------------------------------------------------------------------
    // Version scheme
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "2022.30050")] // a range that counts in years
    [InlineData("cpe:2.3:a:contoso:widget:2022.30050:*:*:*:*:*:*:*", null)] // one exact version in years
    public void RangeInAnotherVersionScheme_IsNotJudgedButCounted(string criteria, string? fixedIn)
    {
        var cve = Cve("CVE-2025-0103", Below(criteria, fixedIn));

        var result = CveVersionFilter.Filter(new[] { cve }, WidgetCpe, "26.198");

        // 26.198 is "below" every year-based version, so comparing would report each such CVE.
        Assert.Empty(result.Vulnerabilities);
        Assert.Equal(1, result.IncomparableCves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CveMatchingThroughAComparableRange_IsReportedAndNotCountedAsIncomparable(bool comparableRangeFirst)
    {
        var inYears = Below("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "2022.30050");
        var inReleases = Below("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "27.0");
        var cve = comparableRangeFirst ? Cve("CVE-2025-0104", inReleases, inYears) : Cve("CVE-2025-0104", inYears, inReleases);

        var result = CveVersionFilter.Filter(new[] { cve }, WidgetCpe, "26.198");

        Assert.Equal("CVE-2025-0104", Assert.Single(result.Vulnerabilities)["cveId"]);
        Assert.Equal(0, result.IncomparableCves);
    }

    [Fact]
    public void IncomparableCves_CountsCvesNotRanges()
    {
        var twoRanges = Cve("CVE-2025-0105",
            Below("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "2022.30050"),
            Below("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "2023.10000"));
        var fixedLongAgo = Cve("CVE-2025-0106", Below("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "20.0"));

        var result = CveVersionFilter.Filter(new[] { twoRanges, fixedLongAgo }, WidgetCpe, "26.198");

        Assert.Empty(result.Vulnerabilities);
        Assert.Equal(1, result.IncomparableCves);
    }

    // -----------------------------------------------------------------------
    // Platform named in the description
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("The flaw can be triggered in certain scenarios when a crafted file is opened.", false)] // "ios" inside "scenarios"
    [InlineData("A local attacker can gain elevated permissions in the agent on Linux and macOS.", true)]
    [InlineData("A local attacker can gain elevated permissions in the agent on Linux and Windows.", false)]
    [InlineData("FortiClientLinux may allow an attacker to execute code via a crafted website.", false)] // a product name, not the platform word
    public void IsDescriptionNonWindowsOnly_MatchesWholeWords(string description, bool nonWindowsOnly)
    {
        Assert.Equal(nonWindowsOnly, CveVersionFilter.IsDescriptionNonWindowsOnly(description));
    }

    [Fact]
    public void DescriptionThatOnlyContainsAPlatformNameInsideAWord_KeepsTheCve()
    {
        var cve = Cve("CVE-2025-0107", Below("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "4.0"));
        cve.Description = "Memory corruption in certain scenarios allows code execution.";

        Assert.Single(CveVersionFilter.Filter(new[] { cve }, WidgetCpe, "3.2.0").Vulnerabilities);
    }
}
