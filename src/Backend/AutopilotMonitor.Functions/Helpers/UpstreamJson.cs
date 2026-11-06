using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Reads Microsoft Graph and Entra token-endpoint responses with System.Text.Json (D-420). The members keep
/// the outcomes the former Newtonsoft code had for every realistic body; GraphResponseParsingTests holds the
/// contract, including the deliberate differences.
/// </summary>
internal static class UpstreamJson
{
    // Newtonsoft skipped comments and accepted trailing commas; keep both.
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The body's root object, or null for an empty or blank body and for a JSON null root. Malformed JSON,
    /// trailing content and any other root kind throw <see cref="JsonException"/>, which is what the callers'
    /// existing catch blocks expect.
    /// </summary>
    internal static JsonElement? ParseObjectOrNull(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        using var document = JsonDocument.Parse(body, ReadOptions);
        var root = document.RootElement;
        return root.ValueKind switch
        {
            JsonValueKind.Object => root.Clone(),
            JsonValueKind.Null => null,
            _ => throw new JsonException($"Expected a JSON object, got {root.ValueKind}."),
        };
    }

    /// <summary>
    /// The property's text; null when it is absent, JSON null, not a string, or text whose escapes are no valid
    /// UTF-16. <paramref name="obj"/> must be an object: a list entry of another kind throws
    /// <see cref="InvalidOperationException"/>, which the validators turn into a transient result, as before.
    /// </summary>
    internal static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) ? TextOf(value) : null;

    // A lone surrogate escape such as \ud800 makes GetString throw; it reads as absent like any other unusable
    // value, so one corrupt field cannot turn a whole lookup transient.
    private static string? TextOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The entries of an array property, of any kind; empty when it is absent or not an array.</summary>
    internal static IEnumerable<JsonElement> Items(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>
    /// The page's <c>@odata.nextLink</c>; null when absent or JSON null. Any other value, text that is no valid
    /// UTF-16 included, throws <see cref="JsonException"/>, so a scan never ends as complete on a link it cannot
    /// follow.
    /// </summary>
    internal static string? GetNextLink(JsonElement page)
    {
        if (!page.TryGetProperty("@odata.nextLink", out var link) || link.ValueKind == JsonValueKind.Null)
            return null;
        return TextOf(link) ?? throw new JsonException($"@odata.nextLink is no readable text ({link.ValueKind}).");
    }

    /// <summary>A whole number, or a string holding one (Entra v1 and ADFS send expires_in as text); else null.</summary>
    internal static long? GetInt64(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(TextOf(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>true or false; any other kind, "true" in quotes or 1 included, is null.</summary>
    internal static bool? GetBoolean(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>
    /// An ISO date string as a UTC instant, read with the invariant culture and full precision; a value
    /// without an offset counts as UTC. Null when absent, not a string, blank, unreadable, or Graph's
    /// <c>0001-01-01</c> placeholder for an unset date.
    /// </summary>
    internal static DateTimeOffset? GetUtcDateTime(JsonElement obj, string name)
    {
        var raw = GetString(obj, name);
        if (string.IsNullOrWhiteSpace(raw)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return null;
        return parsed.Year <= 1 ? null : parsed;
    }
}
