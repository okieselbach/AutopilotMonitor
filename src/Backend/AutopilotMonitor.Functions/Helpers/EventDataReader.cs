using System.Collections;
using System.Globalization;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Typed reads of <see cref="EnrollmentEvent.Data"/>. Stored data comes back as native .NET values
/// (<c>EventDataNormalizer</c>): strings, long / double, bool, DateTime (Newtonsoft turns ISO strings
/// into dates), <c>List&lt;object&gt;</c> and nested <c>Dictionary&lt;string, object&gt;</c>; test
/// fixtures add int, string arrays and typed lists. The agent sends many values as strings
/// ("true", "60", "0x87d1041c"), so every read accepts both shapes, and no value is formatted with
/// the server's culture.
/// </summary>
internal static class EventDataReader
{
    /// <summary>The value as text; null when the key is absent or null. Dates read as ISO 8601 UTC.</summary>
    internal static string? String(EnrollmentEvent? evt, string key) => String(evt?.Data, key);

    /// <inheritdoc cref="String(EnrollmentEvent?, string)"/>
    internal static string? String(IReadOnlyDictionary<string, object>? data, string key)
        => data != null && data.TryGetValue(key, out var raw) ? Text(raw) : null;

    /// <summary>A bool, or a "true"/"false" string in any case; null otherwise.</summary>
    internal static bool? Bool(EnrollmentEvent? evt, string key) => Bool(evt?.Data, key);

    /// <inheritdoc cref="Bool(EnrollmentEvent?, string)"/>
    internal static bool? Bool(IReadOnlyDictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null) return null;
        if (raw is bool b) return b;
        return raw is string s && bool.TryParse(s.Trim(), out var parsed) ? parsed : null;
    }

    /// <summary>A whole number, or a string holding one; null otherwise (a fraction is never truncated).</summary>
    internal static long? Long(IReadOnlyDictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null) return null;
        return raw switch
        {
            long l => l,
            int i => i,
            double d when !double.IsInfinity(d) && d == Math.Floor(d) && Math.Abs(d) < long.MaxValue => (long)d,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>A number, or a string holding one; null otherwise.</summary>
    internal static double? Double(IReadOnlyDictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null) return null;
        return raw switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// A list value as trimmed, non-empty strings; null when the key is absent or the value is not a
    /// list. A plain string is not a list, so "not reported" stays distinguishable from "empty".
    /// </summary>
    internal static List<string>? StringList(IReadOnlyDictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw == null) return null;
        // Strings enumerate as chars — exclude them before the IEnumerable fallback.
        if (raw is string || raw is not IEnumerable items) return null;

        var result = new List<string>();
        foreach (var item in items)
        {
            var s = Text(item)?.Trim();
            if (!string.IsNullOrEmpty(s))
                result.Add(s!);
        }
        return result;
    }

    /// <summary>A nested object; null when absent or not an object.</summary>
    internal static IReadOnlyDictionary<string, object>? Map(IReadOnlyDictionary<string, object>? data, string key)
        => data != null && data.TryGetValue(key, out var raw) ? AsMap(raw) : null;

    /// <summary>A list of objects (such as <c>likelyCauseApps</c>); items that are not objects are skipped.</summary>
    internal static List<IReadOnlyDictionary<string, object>> Maps(IReadOnlyDictionary<string, object>? data, string key)
    {
        var result = new List<IReadOnlyDictionary<string, object>>();
        if (data == null || !data.TryGetValue(key, out var raw) || raw is string || raw is not IEnumerable items)
            return result;
        foreach (var item in items)
        {
            var map = AsMap(item);
            if (map != null) result.Add(map);
        }
        return result;
    }

    /// <summary>A value as an object map (also from typed dictionaries); null when it is none.</summary>
    internal static IReadOnlyDictionary<string, object>? AsMap(object? raw)
    {
        switch (raw)
        {
            case IReadOnlyDictionary<string, object> map:
                return map;
            case IDictionary dict:
                // Typed test fixtures (Dictionary<string, string>) are no IReadOnlyDictionary<string, object>.
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dict)
                    if (entry.Key is string k && entry.Value != null)
                        copy[k] = entry.Value;
                return copy;
            default:
                return null;
        }
    }

    private static string? Text(object? raw) => raw switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc))
            .ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => raw.ToString(),
    };
}
