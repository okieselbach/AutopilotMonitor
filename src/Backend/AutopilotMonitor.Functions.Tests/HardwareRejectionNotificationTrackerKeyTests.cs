using AutopilotMonitor.Functions.DataAccess.TableStorage;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="TableHardwareRejectionNotificationTracker.BuildRowKey"/>.
///
/// CORRECTNESS GUARD: The RowKey is the dedup identity for "has this tenant already been
/// notified about this model?". A change in casing or whitespace must NOT produce a second
/// bell notification. Without case-insensitive trim, "Lenovo X1" and "lenovo x1" would
/// produce two distinct rows and two notifications — exactly what the user explicitly
/// rejected ("einmal pro model").
/// </summary>
public class HardwareRejectionNotificationTrackerKeyTests
{
    [Fact]
    public void BuildRowKey_LowercasesAndJoinsWithPipe()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey("Lenovo", "ThinkPad X1");
        Assert.Equal("lenovo|thinkpad x1", key);
    }

    [Fact]
    public void BuildRowKey_TrimsLeadingAndTrailingWhitespace()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey("  Dell  ", "  Latitude 5520 ");
        Assert.Equal("dell|latitude 5520", key);
    }

    [Theory]
    [InlineData("Lenovo", "ThinkPad X1", "lenovo", "thinkpad x1")]
    [InlineData("LENOVO", "THINKPAD X1", "lenovo", "thinkpad x1")]
    [InlineData("LeNoVo", "ThInKpAd X1", "lenovo", "thinkpad x1")]
    public void BuildRowKey_IsCaseInsensitive(string mfrA, string mdlA, string mfrB, string mdlB)
    {
        var keyA = TableHardwareRejectionNotificationTracker.BuildRowKey(mfrA, mdlA);
        var keyB = TableHardwareRejectionNotificationTracker.BuildRowKey(mfrB, mdlB);
        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void BuildRowKey_DistinctModels_ProduceDistinctKeys()
    {
        var keyX1 = TableHardwareRejectionNotificationTracker.BuildRowKey("Lenovo", "ThinkPad X1");
        var keyX13 = TableHardwareRejectionNotificationTracker.BuildRowKey("Lenovo", "ThinkPad X13");
        Assert.NotEqual(keyX1, keyX13);
    }

    [Fact]
    public void BuildRowKey_NullInputs_ReturnPipeDelimiterOnly()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey(null!, null!);
        Assert.Equal("|", key);
    }

    [Fact]
    public void BuildRowKey_EmptyInputs_ReturnPipeDelimiterOnly()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey("", "");
        Assert.Equal("|", key);
    }

    // =========================================================================
    // TPM PSS row keys (same table, "tpmpss|" prefix — dedup per serial)
    // =========================================================================

    [Fact]
    public void BuildTpmPssRowKey_LowercasesTrimsAndPrefixes()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildTpmPssRowKey("  S4SQ8685 ");
        Assert.Equal("tpmpss|s4sq8685", key);
    }

    [Theory]
    [InlineData("S4SQ8685", "s4sq8685")]
    [InlineData("s4Sq8685", "S4SQ8685")]
    public void BuildTpmPssRowKey_IsCaseInsensitive(string serialA, string serialB)
    {
        Assert.Equal(
            TableHardwareRejectionNotificationTracker.BuildTpmPssRowKey(serialA),
            TableHardwareRejectionNotificationTracker.BuildTpmPssRowKey(serialB));
    }

    [Fact]
    public void BuildTpmPssRowKey_DoesNotCollideWithHardwareKeyOfSameText()
    {
        // A hardware key is "{mfr}|{model}"; the TPM key's fixed "tpmpss|" prefix keeps the
        // two key spaces disjoint within the shared table.
        var hardwareKey = TableHardwareRejectionNotificationTracker.BuildRowKey("Lenovo", "S4SQ8685");
        var tpmKey = TableHardwareRejectionNotificationTracker.BuildTpmPssRowKey("S4SQ8685");
        Assert.NotEqual(hardwareKey, tpmKey);
    }
}
