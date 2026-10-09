using System.Text;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The one rule for turning free text (device inventory, app names, admin input) into an Azure
/// Table Storage key. Every builder of a key from free text uses it, so the same text always maps
/// to the same key.
/// <para>
/// Control characters (U+0000–U+001F, U+007F–U+009F) are removed: Table Storage rejects them in
/// keys, and a point read, update or delete carries the key in its request URL, which the HTTP layer
/// in front of the service refuses outright ("400 Invalid URL"). The characters Table Storage
/// rejects in keys (/, \, #, ?) become '_'. Text without control characters keeps its key byte for
/// byte, so existing rows stay reachable.
/// </para>
/// </summary>
public static class TableKeySanitizer
{
    public static string Sanitize(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (char.IsControl(c))
                continue;
            sb.Append(c is '/' or '\\' or '#' or '?' ? '_' : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Removes control characters only — for text that later becomes part of a key or an identity
    /// compared against one (the software inventory triple).
    /// </summary>
    public static string RemoveControlCharacters(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsControl(c))
                sb.Append(c);
        }
        return sb.ToString();
    }
}
