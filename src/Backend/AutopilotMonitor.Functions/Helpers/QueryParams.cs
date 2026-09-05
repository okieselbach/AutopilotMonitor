using System.Collections.Specialized;
using System.Globalization;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The one parser for HTTP query parameters. Every function reads its numbers and instants
/// through here (<c>QueryParamGuardTests</c> fails on a stray <c>int.TryParse</c> /
/// <c>DateTime.TryParse</c> under <c>Functions/</c> or <c>Pagination/</c>); the default and
/// the range stay arguments at the call site because they are per-endpoint wire behaviour
/// (a 7-day stats window and a 90-day usage window are both right).
/// <para>
/// Two contracts, deliberately not one:
/// <list type="bullet">
///   <item><b>Lenient</b> (<see cref="Int(string?, int, int, int)"/>, <see cref="UtcInstant"/>,
///   <see cref="IntOrNull"/>): a missing, unparseable or below-minimum value means "use the
///   default", a value above the maximum is clamped. For windows and top-N knobs where a
///   dashboard link with a stale or odd value should still render.</item>
///   <item><b>Strict</b> (<see cref="TryInt"/>, <see cref="TryPageSize"/>,
///   <see cref="TryUtcInstant"/>): a missing value is <c>null</c> (the caller applies its
///   default), anything else that does not parse or falls outside the range is an error
///   message for a 400. For pagination and admin batch sizes, where a silently altered
///   value would misreport what was done.</item>
/// </list>
/// Numbers are parsed invariant (<see cref="NumberStyles.Integer"/>); instants are parsed
/// invariant with <c>AssumeUniversal | AdjustToUniversal</c>, so <c>2026-04-20</c>,
/// <c>2026-04-20T10:00:00</c> and <c>2026-04-20T12:00:00+02:00</c> all yield the same UTC
/// value with <see cref="DateTimeKind.Utc"/>. Whitespace-only counts as absent.
/// </para>
/// </summary>
internal static class QueryParams
{
    /// <summary>Upper bound of every <c>pageSize</c> parameter (continuation-token pagination).</summary>
    public const int MaxPageSize = 1000;

    private const DateTimeStyles UtcStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    // ── Lenient ─────────────────────────────────────────────────────────────────────────

    /// <summary>Lenient integer: absent / unparseable / below <paramref name="min"/> ⇒ <paramref name="default"/>; above <paramref name="max"/> ⇒ <paramref name="max"/>.</summary>
    public static int Int(string? raw, int @default, int min, int max)
    {
        if (!TryParseInvariant(raw, out var value) || value < min) return @default;
        return value > max ? max : value;
    }

    /// <inheritdoc cref="Int(string?, int, int, int)"/>
    public static int Int(NameValueCollection? query, string name, int @default, int min, int max)
        => Int(query?[name], @default, min, max);

    /// <summary>Lenient optional integer without a range: absent or unparseable ⇒ <c>null</c>.</summary>
    public static int? IntOrNull(string? raw)
        => TryParseInvariant(raw, out var value) ? value : (int?)null;

    /// <summary>Lenient optional instant: absent or unparseable ⇒ <c>null</c>, otherwise UTC.</summary>
    public static DateTime? UtcInstant(string? raw)
        => TryParseUtc(raw, out var value) ? value : (DateTime?)null;

    // ── Strict ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strict integer: absent ⇒ <c>null</c> + true; unparseable or outside
    /// [<paramref name="min"/>, <paramref name="max"/>] ⇒ false with an error message that names
    /// the parameter. Pass <c>int.MaxValue</c> as <paramref name="max"/> for "at least min".
    /// </summary>
    public static bool TryInt(string? raw, string name, int min, int max, out int? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!TryParseInvariant(raw, out var parsed))
        {
            error = $"{name} must be an integer";
            return false;
        }

        if (parsed < min || parsed > max)
        {
            error = max == int.MaxValue
                ? $"{name} must be an integer of at least {min}"
                : $"{name} must be between {min} and {max}";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>Strict <c>pageSize</c>: absent ⇒ <c>null</c> (the paginator applies its default), otherwise 1..<see cref="MaxPageSize"/>.</summary>
    public static bool TryPageSize(string? raw, out int? pageSize, out string? error)
        => TryInt(raw, "pageSize", 1, MaxPageSize, out pageSize, out error);

    /// <summary>Strict instant: absent ⇒ <c>null</c> + true; unparseable ⇒ false with an error message; otherwise UTC.</summary>
    public static bool TryUtcInstant(string? raw, string name, out DateTime? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!TryParseUtc(raw, out var parsed))
        {
            error = $"{name} must be an ISO 8601 date or date-time";
            return false;
        }

        value = parsed;
        return true;
    }

    // ── Core ────────────────────────────────────────────────────────────────────────────

    private static bool TryParseInvariant(string? raw, out int value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = 0;
            return false;
        }
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseUtc(string? raw, out DateTime value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = default;
            return false;
        }
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, UtcStyles, out var parsed))
        {
            value = default;
            return false;
        }
        // AdjustToUniversal yields Kind=Utc; SpecifyKind is belt and braces for the one
        // shape (a bare date) where some runtimes leave it Unspecified.
        value = parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
