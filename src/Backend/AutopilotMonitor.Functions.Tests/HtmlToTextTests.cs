using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>The text/plain twin of the HTML mails (HtmlToText): what it keeps, what it drops, how it breaks lines.</summary>
public class HtmlToTextTests
{
    [Fact]
    public void Drops_head_style_script_and_comments_and_keeps_the_prose()
    {
        const string html = "<!DOCTYPE html><html><head><title>t</title><style>p{color:red}</style></head>" +
                            "<body><!-- note --><script>alert(1)</script><p>Hello <strong>world</strong></p></body></html>";
        Assert.Equal("Hello world", HtmlToText.Convert(html));
    }

    [Fact]
    public void Turns_blocks_lists_and_breaks_into_lines_and_decodes_entities()
    {
        const string html = "<h2>Welcome</h2><p>Great &ndash; access for <strong>contoso.invalid</strong> is ready.<br>Second line.</p>" +
                            "<ul><li>One &amp; two</li><li>Three</li></ul><table><tr><td>a</td><td>b</td></tr></table>";
        var text = HtmlToText.Convert(html);
        Assert.Equal("Welcome\nGreat – access for contoso.invalid is ready.\nSecond line.\n\n- One & two\n- Three\na b", text);
    }

    [Fact]
    public void Keeps_link_targets_and_collapses_whitespace()
    {
        const string html = "<p>You can now <a href=\"https://portal.example.invalid/\" target=\"_blank\" style=\"color:red\">sign   in</a>   and start.</p>" +
                            "<p><a href=\"https://docs.example.invalid\">https://docs.example.invalid</a></p>" +
                            "<p><a href=\"mailto:x@example.invalid\">write us</a> or <a href=\"https://x.invalid/a?b=1&amp;c=2\">this</a></p>";
        var text = HtmlToText.Convert(html);
        Assert.Equal("You can now sign in (https://portal.example.invalid/) and start.\nhttps://docs.example.invalid\nwrite us or this (https://x.invalid/a?b=1&c=2)", text);
    }

    [Fact]
    public void Survives_empty_and_broken_markup()
    {
        Assert.Equal(string.Empty, HtmlToText.Convert(null));
        Assert.Equal(string.Empty, HtmlToText.Convert("   "));
        Assert.Equal("unterminated bold", HtmlToText.Convert("<p>unterminated <b>bold"));
        Assert.Equal("x", HtmlToText.Convert("<a href='nolink'>x</a>"));
    }

    [Fact]
    public void Renders_the_default_welcome_template_as_readable_text()
    {
        var text = HtmlToText.Convert(EmailTemplates.GetPreviewApprovedHtml("contoso.invalid"));
        Assert.Contains("Welcome to Autopilot Monitor", text);
        Assert.Contains("contoso.invalid", text);
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("style=", text);
        Assert.Contains("https://", text);   // the links survive as URLs
    }
}
