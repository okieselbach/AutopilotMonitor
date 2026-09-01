using AutopilotMonitor.Functions.DataAccess.TableStorage;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// BlockedDevices rows must be keyed case-insensitively: the enforcement cache
/// (BlockedDeviceService) and Autopilot inventory validation both compare serials
/// OrdinalIgnoreCase, so a case-variant serial must resolve to the same storage row.
/// </summary>
public class TableDeviceSecurityRepositoryKeyTests
{
    [Theory]
    [InlineData("ABC123", "ABC123")]
    [InlineData("abc123", "ABC123")]
    [InlineData("  aBc123 ", "ABC123")]
    [InlineData(null, "")]
    public void CanonicalizeSerial_TrimsAndUpperCases(string? input, string expected)
        => Assert.Equal(expected, TableDeviceSecurityRepository.CanonicalizeSerial(input));

    [Theory]
    [InlineData("ABC123", "abc123")]
    [InlineData("ABC123", "Abc123 ")]
    [InlineData("PF/1#2", "pf/1#2")]
    public void DeviceRowKey_IsIdenticalForCaseVariants(string a, string b)
        => Assert.Equal(TableDeviceSecurityRepository.DeviceRowKey(a), TableDeviceSecurityRepository.DeviceRowKey(b));

    [Fact]
    public void DeviceRowKey_EscapesReservedCharacters()
        => Assert.Equal("PF%2F1%232", TableDeviceSecurityRepository.DeviceRowKey("pf/1#2"));

    [Fact]
    public void LegacyDeviceRowKey_IsNull_WhenAlreadyCanonical()
        => Assert.Null(TableDeviceSecurityRepository.LegacyDeviceRowKey("ABC123"));

    [Fact]
    public void LegacyDeviceRowKey_IsVerbatimKey_WhenCaseDiffers()
        => Assert.Equal("abc123", TableDeviceSecurityRepository.LegacyDeviceRowKey("abc123"));

    // --- Identity alias rows ---

    [Theory]
    [InlineData("0F8FAD5B-D9CB-469F-A165-70867728950E")]
    [InlineData(" 0f8fad5b-d9cb-469f-a165-70867728950e ")]
    [InlineData("{0f8fad5b-d9cb-469f-a165-70867728950e}")]
    public void IdentityRowKey_NormalizesGuidForm(string input)
        => Assert.Equal("id:0f8fad5b-d9cb-469f-a165-70867728950e", TableDeviceSecurityRepository.IdentityRowKey(input));

    [Fact]
    public void IdentityRowKey_NeverCollidesWithASerialKey()
    {
        // A serial that happens to look like an alias key is escaped (':' → %3A) and upper-cased.
        var serialKey = TableDeviceSecurityRepository.DeviceRowKey("id:0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.False(TableDeviceSecurityRepository.IsIdentityRowKey(serialKey));
        Assert.True(TableDeviceSecurityRepository.IsIdentityRowKey("id:0f8fad5b-d9cb-469f-a165-70867728950e"));
        Assert.False(TableDeviceSecurityRepository.IsIdentityRowKey(null));
    }

    [Fact]
    public void ParseAliasDeviceIds_DedupesAndDropsNonGuids()
    {
        var parsed = TableDeviceSecurityRepository.ParseAliasDeviceIds(
            "0F8FAD5B-D9CB-469F-A165-70867728950E,junk,,0f8fad5b-d9cb-469f-a165-70867728950e, 7c9e6679-7425-40de-944b-e07fc1f90ae7");
        Assert.Equal(new[] { "0f8fad5b-d9cb-469f-a165-70867728950e", "7c9e6679-7425-40de-944b-e07fc1f90ae7" }, parsed);
    }

    [Fact]
    public void MergeAliasDeviceIds_KeepsStoredFirst_AppendsNew_IgnoresDuplicates()
    {
        var merged = TableDeviceSecurityRepository.MergeAliasDeviceIds(
            new List<string> { "0f8fad5b-d9cb-469f-a165-70867728950e" },
            new[] { "0F8FAD5B-D9CB-469F-A165-70867728950E", "7c9e6679-7425-40de-944b-e07fc1f90ae7", "not-a-guid" });
        Assert.Equal(new[] { "0f8fad5b-d9cb-469f-a165-70867728950e", "7c9e6679-7425-40de-944b-e07fc1f90ae7" }, merged);
    }
}
