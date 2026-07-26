using System.Collections;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Parsed ESP blocking/tracking lists from an <c>esp_config_detected</c> event payload
/// (agent emitter: <c>EspTrackingInfoProbe</c> → <c>DeviceInfoCollector.CollectEspConfiguration</c>).
///
/// Semantics (source-data audit Q2, insights spec §0.5): the lists are a POSITIVE-EVIDENCE set.
/// An app id found in any list is proven ESP-blocking; an id that is absent is UNKNOWN — never
/// "not blocking" — because the registry lists grow progressively (one timestamped subkey per
/// CSP status write), the user-scope <c>S-&lt;SID&gt;</c> lists usually appear only after
/// sign-in, each list is capped at 50 entries (uncapped totals ride in the <c>espTracked*Count</c>
/// fields), and MSI ProductCodes / PFNs live in namespaces the IME <c>appId</c> may not match.
/// Consumers must therefore always use the LATEST payload of a session that carries lists, and
/// must map "absent" to null/unknown, never to false.
/// </summary>
public sealed class EspBlockingSets
{
    private readonly HashSet<string> _all;

    private EspBlockingSets(
        HashSet<string> all,
        HashSet<string> userWin32AppIds,
        int listedTotal,
        bool isTruncated)
    {
        _all = all;
        UserWin32AppIds = userWin32AppIds;
        ListedCount = listedTotal;
        IsTruncated = isTruncated;
    }

    /// <summary>Distinct ids across all four lists (Win32 device+user, MSI, PFN).</summary>
    public int ListedCount { get; }

    /// <summary>
    /// True when any <c>espTracked*Count</c> exceeds its emitted list length — the 50-per-category
    /// cap dropped entries, so even the positive evidence is incomplete for that category.
    /// </summary>
    public bool IsTruncated { get; }

    /// <summary>
    /// The user-scope Win32 subset (<c>espTrackedUserWin32AppIds</c>). Usually empty until the
    /// agent-side re-emit fix ships (audit Q2) — exposed so callers can label user-ESP coverage
    /// as unknown rather than inferring it from the merged list.
    /// </summary>
    public IReadOnlyCollection<string> UserWin32AppIds { get; }

    /// <summary>Positive-evidence membership test (case-insensitive, all four lists).</summary>
    public bool Contains(string? appId)
        => !string.IsNullOrEmpty(appId) && _all.Contains(appId!);

    /// <summary>
    /// Parses the payload of one <c>esp_config_detected</c> event. Returns null when the payload
    /// carries none of the <c>espTracked*</c> list keys (probe found no Diagnostics key — e.g.
    /// non-Autopilot device or pre-list agent build): "no lists observed" must stay
    /// distinguishable from "empty lists observed".
    /// Handles both value shapes: live-ingest / storage-rehydrated data (<c>List&lt;object&gt;</c>
    /// via EventDataNormalizer) and test fixtures (<c>string[]</c> / <c>List&lt;string&gt;</c>).
    /// </summary>
    public static EspBlockingSets? FromEventData(IReadOnlyDictionary<string, object>? data)
    {
        if (data == null) return null;

        var win32 = ReadStringList(data, "espTrackedWin32AppIds");
        var userWin32 = ReadStringList(data, "espTrackedUserWin32AppIds");
        var msi = ReadStringList(data, "espTrackedMsiProductCodes");
        var pfn = ReadStringList(data, "espTrackedModernAppPfns");

        if (win32 == null && userWin32 == null && msi == null && pfn == null)
            return null;

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in new[] { win32, userWin32, msi, pfn })
            if (list != null)
                foreach (var id in list)
                    all.Add(id);

        var userSet = new HashSet<string>(userWin32 ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        // The agent emits uncapped distinct totals alongside the capped lists. The Win32 count
        // covers the merged device+user list; user-Win32 has no own count field.
        var truncated =
            CountExceeds(data, "espTrackedWin32Count", win32) ||
            CountExceeds(data, "espTrackedMsiCount", msi) ||
            CountExceeds(data, "espTrackedModernCount", pfn);

        return new EspBlockingSets(all, userSet, all.Count, truncated);
    }

    private static bool CountExceeds(IReadOnlyDictionary<string, object> data, string countKey, List<string>? list)
    {
        if (list == null) return false;
        if (!data.TryGetValue(countKey, out var raw)) return false;
        return long.TryParse(raw?.ToString(), out var count) && count > list.Count;
    }

    private static List<string>? ReadStringList(IReadOnlyDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var raw) || raw == null) return null;
        // Strings enumerate as chars — exclude explicitly before the IEnumerable fallback.
        if (raw is string) return null;
        if (raw is not IEnumerable enumerable) return null;

        var result = new List<string>();
        foreach (var item in enumerable)
        {
            var s = item?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(s))
                result.Add(s!);
        }
        return result;
    }
}
