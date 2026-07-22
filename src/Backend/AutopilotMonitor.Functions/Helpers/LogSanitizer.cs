using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Strips control characters from user-controlled values before they reach a log
/// sink, so a caller cannot forge additional log lines (CRLF injection) or smuggle
/// terminal escape sequences through the message. Structured logging already stores
/// each value as a discrete property, so this is defense-in-depth — it also clears
/// the CodeQL cs/log-forging alerts by removing the newline characters they key on.
/// Only ever apply to the LOGGED copy of a value; never to a value used for routing,
/// authorization, or storage keys.
/// </summary>
public static class LogSanitizer
{
    // \p{Cc} = Unicode "Other, control" (C0 + C1 ranges), which includes CR and LF.
    // \p{Zl} and \p{Zp} are the line and paragraph separators (U+2028 / U+2029). They
    // are separators, not control characters, so \p{Cc} misses them — yet a log viewer,
    // a JSON-lines consumer, or any JavaScript-side reader still breaks a line on them.
    private static readonly Regex ControlChars = new(@"[\p{Cc}\p{Zl}\p{Zp}]", RegexOptions.Compiled);

    /// <summary>
    /// Removes CR, LF, the Unicode line/paragraph separators, and every other control
    /// character from <paramref name="value"/>. Returns null/empty inputs unchanged.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Explicit CR/LF removal first — this is the log-injection barrier the CodeQL
        // taint model recognizes; the regex pass then drops any remaining separators.
        var sanitized = value.Replace("\r", string.Empty).Replace("\n", string.Empty);
        return ControlChars.Replace(sanitized, string.Empty);
    }
}
