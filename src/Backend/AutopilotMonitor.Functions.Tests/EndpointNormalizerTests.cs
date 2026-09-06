using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

public class EndpointNormalizerTests
{
    [Fact]
    public void Normalize_StripsApiPrefixReplacesGuidsAndKeyChars()
    {
        var normalized = EndpointNormalizer.Normalize("/api/Sessions/0f8fad5b-d9cb-469f-a165-70867728950e/events?x=1");
        Assert.Equal("sessions_{id}_events_x=1", normalized);
    }

    [Fact]
    public void ToolNameKey_RealToolName_PassesThroughUnchanged()
    {
        // Existing UserUsageLog rows are keyed by "<tool>:<endpoint>"; a real tool name must keep its key.
        Assert.Equal("get_session_events", EndpointNormalizer.ToolNameKey("get_session_events"));
    }

    [Fact]
    public void ToolNameKey_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, EndpointNormalizer.ToolNameKey(null));
        Assert.Equal(string.Empty, EndpointNormalizer.ToolNameKey(string.Empty));
    }

    [Fact]
    public void ToolNameKey_WithCrlfAndControlChars_StripsThem()
    {
        // The log-forging vector: a header that tries to inject a fake log line through the row key.
        var forged = $"get_metrics\r\n[McpQuota] FAKE{(char)0}{(char)27}{(char)0x2028}x";
        var key = EndpointNormalizer.ToolNameKey(forged);
        Assert.Equal("get_metrics[McpQuota] FAKEx", key);
    }

    [Fact]
    public void ToolNameKey_WithTableKeyIllegalChars_ReplacesThem()
    {
        // '/', '\', '#', '?' are rejected by Table Storage in keys — a header carrying them would make
        // every usage increment fail with 400, so they are replaced the same way the path is.
        Assert.Equal("a_b_c_d_e", EndpointNormalizer.ToolNameKey("a/b\\c#d?e"));
    }

    [Fact]
    public void ToolNameKey_LongerThanCap_IsTruncated()
    {
        var key = EndpointNormalizer.ToolNameKey(new string('t', EndpointNormalizer.MaxToolNameLength + 10));
        Assert.Equal(EndpointNormalizer.MaxToolNameLength, key.Length);
    }

    [Fact]
    public void ToolNameKey_OnlyControlChars_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, EndpointNormalizer.ToolNameKey("\r\n  "));
    }
}
