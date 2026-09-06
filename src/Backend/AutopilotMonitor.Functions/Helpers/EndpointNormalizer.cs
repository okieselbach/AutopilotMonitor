using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Normalizes request paths for usage grouping by replacing GUIDs with {id}.
/// E.g. "/api/sessions/abc-def-123/events" -> "sessions/{id}/events"
/// </summary>
public static class EndpointNormalizer
{
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    // Same classes LogSanitizer strips: C0/C1 control characters (Table Storage rejects them in
    // keys) plus the Unicode line/paragraph separators a log viewer breaks a line on.
    private static readonly Regex ControlChars = new(@"[\p{Cc}\p{Zl}\p{Zp}]", RegexOptions.Compiled);

    /// <summary>
    /// Cap for the caller-supplied tool-name prefix of a usage row key. Real MCP tool names are
    /// well under this; the cap only bounds what an arbitrary header can push into a key.
    /// </summary>
    public const int MaxToolNameLength = 64;

    public static string Normalize(string path)
    {
        // Strip /api/ prefix
        var normalized = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(5)
            : path.TrimStart('/');

        // Replace GUIDs with {id}
        normalized = GuidPattern.Replace(normalized, "{id}");

        return ReplaceIllegalKeyChars(normalized).ToLowerInvariant();
    }

    /// <summary>
    /// Makes the <c>X-MCP-Tool-Name</c> header value safe to use as the prefix of a usage row key.
    /// The header is caller-supplied (the MCP server sets it to the real tool name, but any
    /// authenticated MCP-source caller can send it), and the key ends up in the CAS-conflict warning
    /// of the usage counter: control characters and line separators are removed, the characters Table
    /// Storage rejects in keys are replaced, and the length is capped. A real tool name
    /// (<c>get_session_events</c>) passes through unchanged, so existing usage rows keep their keys.
    /// Returns an empty string when nothing key-safe remains; callers then skip the prefix.
    /// </summary>
    public static string ToolNameKey(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return string.Empty;

        // Explicit CR/LF removal first — the log-injection barrier the CodeQL taint model
        // recognizes; the regex pass then drops any remaining control character or separator.
        var cleaned = toolName.Replace("\r", string.Empty).Replace("\n", string.Empty);
        cleaned = ControlChars.Replace(cleaned, string.Empty);
        cleaned = ReplaceIllegalKeyChars(cleaned).Trim();
        return cleaned.Length <= MaxToolNameLength ? cleaned : cleaned[..MaxToolNameLength];
    }

    /// <summary>Replaces the characters Azure Table Storage rejects in keys (/, \, #, ?).</summary>
    private static string ReplaceIllegalKeyChars(string value)
        => value.Replace('/', '_').Replace('\\', '_').Replace('#', '_').Replace('?', '_');
}
