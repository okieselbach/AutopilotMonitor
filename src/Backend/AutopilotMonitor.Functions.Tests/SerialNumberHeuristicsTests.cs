using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Guards the registration supersede pass (misclassification audit 2026-07-16): OEM
/// placeholder serials would cross-match unrelated devices, so they must never be treated
/// as a device identity.
/// </summary>
public class SerialNumberHeuristicsTests
{
    [Theory]
    [InlineData("PF4DF1EN")]
    [InlineData("5CG5504Y5L")]
    [InlineData("  PF4DF1EN  ")] // trimmed
    [InlineData("VMware-56 4d 8a")]
    public void RealSerials_AreUsable(string serial)
    {
        Assert.True(SerialNumberHeuristics.IsUsableSerialNumber(serial));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("abc")] // too short to identify a device
    [InlineData("None")]
    [InlineData("UNKNOWN")]
    [InlineData("Default string")]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("System Serial Number")]
    [InlineData("Not Specified")]
    [InlineData("n/a")]
    public void PlaceholderOrEmptySerials_AreNotUsable(string? serial)
    {
        Assert.False(SerialNumberHeuristics.IsUsableSerialNumber(serial));
    }
}
