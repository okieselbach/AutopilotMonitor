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
}
