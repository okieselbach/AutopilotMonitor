using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Customer-supplied attachment file names go verbatim into ZIP entry names of the
/// session-report archive. The sanitizer must strip path components so a crafted name
/// cannot produce traversal-shaped entries that an extraction tool would write outside
/// the target directory.
/// </summary>
public class SessionReportZipEntryNameTests
{
    [Theory]
    [InlineData("agent.log", "agent.log")]
    [InlineData("agent_20260729.log", "agent_20260729.log")]
    [InlineData("logs/agent.log", "agent.log")]
    [InlineData("..\\..\\evil.exe", "evil.exe")]
    [InlineData("../../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\System32\\bad.dll", "bad.dll")]
    [InlineData("  spaced.log  ", "spaced.log")]
    public void SanitizeZipEntryName_StripsPathComponents(string input, string expected)
    {
        Assert.Equal(expected, SessionReportService.SanitizeZipEntryName(input, "fallback.log"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("logs/")]
    [InlineData("a/../")]
    public void SanitizeZipEntryName_FallsBackWhenNothingUsable(string? input)
    {
        Assert.Equal("fallback.log", SessionReportService.SanitizeZipEntryName(input, "fallback.log"));
    }
}
