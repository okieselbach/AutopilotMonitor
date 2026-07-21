using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

public class LogSanitizerTests
{
    [Fact]
    public void Clean_WithCrlf_StripsNewlines()
    {
        // The core log-forging vector: a value that injects a fake log line.
        var forged = "/api/sessions\r\n[Auth Middleware] FAKE: admin granted";
        var cleaned = LogSanitizer.Clean(forged);
        Assert.Equal("/api/sessions[Auth Middleware] FAKE: admin granted", cleaned);
        Assert.DoesNotContain('\r', cleaned!);
        Assert.DoesNotContain('\n', cleaned!);
    }

    [Fact]
    public void Clean_WithBareLineFeedOrCarriageReturn_StripsBoth()
    {
        Assert.Equal("ab", LogSanitizer.Clean("a\nb"));
        Assert.Equal("ab", LogSanitizer.Clean("a\rb"));
    }

    [Fact]
    public void Clean_WithOtherControlChars_StripsThem()
    {
        // Tab (9), NUL (0) and ESC (27, terminal escape) are all removed. Built
        // from char codes so the source file carries no literal control bytes.
        var input = $"a{(char)9}b{(char)0}c{(char)27}de";
        Assert.Equal("abcde", LogSanitizer.Clean(input));
    }

    [Theory]
    [InlineData("/api/sessions/abc-123/events")]
    [InlineData("user@contoso.com")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData("GET")]
    public void Clean_WithLegitimateValue_ReturnsUnchanged(string value)
    {
        // Real paths, UPNs, GUIDs and methods never contain control chars, so
        // tracing fidelity is fully preserved.
        Assert.Equal(value, LogSanitizer.Clean(value));
    }

    [Fact]
    public void Clean_WithNull_ReturnsNull()
    {
        Assert.Null(LogSanitizer.Clean(null));
    }

    [Fact]
    public void Clean_WithEmpty_ReturnsEmpty()
    {
        Assert.Equal("", LogSanitizer.Clean(""));
    }
}
