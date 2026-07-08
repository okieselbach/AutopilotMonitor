using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

public class CpeUriNormalizerTests
{
    [Theory]
    // %2b / %2B (url-encoded '+') and a bare '+' all become the CPE-quoted \+\+.
    [InlineData("cpe:2.3:a:microsoft:visual_c%2b%2b", @"cpe:2.3:a:microsoft:visual_c\+\+")]
    [InlineData("cpe:2.3:a:microsoft:visual_c%2B%2B", @"cpe:2.3:a:microsoft:visual_c\+\+")]
    [InlineData("cpe:2.3:a:microsoft:visual_c++", @"cpe:2.3:a:microsoft:visual_c\+\+")]
    // Already-quoted form is idempotent.
    [InlineData(@"cpe:2.3:a:microsoft:visual_c\+\+", @"cpe:2.3:a:microsoft:visual_c\+\+")]
    // No special chars → passthrough.
    [InlineData("cpe:2.3:a:adobe:acrobat_dc", "cpe:2.3:a:adobe:acrobat_dc")]
    // Other %-sequences (%20 space, %2F slash, …) must NOT be rewritten — only %2B/%2b.
    [InlineData("cpe:2.3:a:vendor:foo%20bar", "cpe:2.3:a:vendor:foo%20bar")]
    // '*' is the CPE 2.3 ANY wildcard and must remain unquoted.
    [InlineData("cpe:2.3:a:microsoft:windows:*:*", "cpe:2.3:a:microsoft:windows:*:*")]
    // Null / empty → empty string.
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Normalize_quotes_plus_and_leaves_everything_else(string? input, string expected)
    {
        Assert.Equal(expected, CpeUriNormalizer.Normalize(input));
    }
}
