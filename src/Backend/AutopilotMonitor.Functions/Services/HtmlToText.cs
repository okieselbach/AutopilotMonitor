using System.Net;
using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// The plain-text twin of an HTML mail, good enough for the text/plain part of a multipart
/// message: a balanced multipart with a real text part is one of the strongest content signals
/// a mail filter has, and a derived one by the provider is weaker than our own. Conservative on
/// purpose — head, style, script and comments go, block boundaries become line breaks, list
/// items get a dash, links keep their URL in parentheses, entities are decoded, whitespace is
/// collapsed. Never throws on odd markup; the worst case is slightly ugly text.
/// </summary>
public static class HtmlToText
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex Dropped = new(@"<!--.*?-->|<head\b.*?</head>|<style\b.*?</style>|<script\b.*?</script>", Opts);
    private static readonly Regex Anchor = new(@"<a\b[^>]*?href\s*=\s*([""'])(?<url>.*?)\1[^>]*>(?<text>.*?)</a>", Opts);
    private static readonly Regex LineBreak = new(@"<br\s*/?>", Opts);
    private static readonly Regex ListItem = new(@"<li\b[^>]*>", Opts);
    // A list item ends where the next one starts ("\n- "), so </li> adds no line of its own.
    private static readonly Regex BlockEnd = new(@"</(p|div|tr|h[1-6]|table|ul|ol|blockquote|section|article|header|footer)\s*>", Opts);
    private static readonly Regex CellEnd = new(@"</t[dh]\s*>", Opts);
    private static readonly Regex AnyTag = new(@"<[^>]+>", Opts);
    private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = Dropped.Replace(html, string.Empty);
        text = Anchor.Replace(text, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups["url"].Value).Trim();
            var label = Spaces.Replace(WebUtility.HtmlDecode(AnyTag.Replace(m.Groups["text"].Value, string.Empty)), " ").Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return label;
            if (label.Length == 0 || string.Equals(label, url, StringComparison.OrdinalIgnoreCase))
                return url;
            return $"{label} ({url})";
        });
        text = LineBreak.Replace(text, "\n");
        text = ListItem.Replace(text, "\n- ");
        text = BlockEnd.Replace(text, "\n");
        text = CellEnd.Replace(text, " ");
        text = AnyTag.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var lines = text.Split('\n').Select(l => Spaces.Replace(l, " ").Trim());
        var joined = string.Join("\n", lines).Trim();
        return BlankLines.Replace(joined, "\n\n");
    }
}
