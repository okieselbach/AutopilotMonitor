#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.Services
{
    /// <summary>
    /// Single-source-of-truth lookup for Windows / MSI / Intune error codes. Loaded once
    /// from the embedded <c>Resources/error-codes.json</c> resource and exposed via a
    /// static dictionary keyed by normalised code string (lowercase hex, e.g. <c>0x80070005</c>,
    /// or decimal MSI exit code, e.g. <c>1603</c>).
    /// <para>
    /// Mirror of <c>src/Web/autopilot-monitor-web/utils/errorCodeMap.ts</c>; the JSON file
    /// is the authoritative copy, the web side imports the same JSON via the
    /// <c>sync-error-codes</c> prebuild step.
    /// </para>
    /// </summary>
    public static class ErrorCodeCatalog
    {
        private const string ResourceName = "AutopilotMonitor.Shared.Resources.error-codes.json";

        private static readonly Lazy<IReadOnlyDictionary<string, ErrorCodeEntry>> _entries =
            new Lazy<IReadOnlyDictionary<string, ErrorCodeEntry>>(LoadEntries);

        /// <summary>All loaded entries, keyed by normalised code string.</summary>
        public static IReadOnlyDictionary<string, ErrorCodeEntry> All => _entries.Value;

        /// <summary>
        /// Look up an entry for a raw code value. Accepts:
        /// <list type="bullet">
        ///   <item>Decimal strings (e.g. <c>"1603"</c>, <c>"0"</c>)</item>
        ///   <item>Hex strings (e.g. <c>"0x80070005"</c>, <c>"0X80070005"</c>)</item>
        ///   <item>Signed-decimal HRESULT (e.g. <c>"-2147024891"</c> → <c>0x80070005</c>)</item>
        /// </list>
        /// Returns <c>null</c> when no mapping is found.
        /// </summary>
        public static ErrorCodeEntry? TryLookup(string? rawCode)
        {
            if (string.IsNullOrWhiteSpace(rawCode)) return null;
            var raw = rawCode!.Trim();

            // 1) Direct lookup (lower-cased, handles decimal keys and already-lowered hex)
            if (All.TryGetValue(raw.ToLowerInvariant(), out var direct))
                return direct;

            // 2) Hex input normalisation ("0X..." → "0x...", strip leading zeros, repad to 8)
            if (raw.Length >= 2 && (raw[0] == '0') && (raw[1] == 'x' || raw[1] == 'X'))
            {
                var body = raw.Substring(2).TrimStart('0').ToLowerInvariant();
                if (body.Length == 0) body = "0";
                var normalised = "0x" + body.PadLeft(8, '0');
                if (All.TryGetValue(normalised, out var hex)) return hex;
            }

            // 3) Signed-decimal HRESULT → unsigned hex  (e.g. -2147024891 → 0x80070005)
            if (long.TryParse(raw, out var asLong) && asLong < 0)
            {
                var unsigned = unchecked((uint)asLong);
                var hex = "0x" + unsigned.ToString("x8");
                if (All.TryGetValue(hex, out var match)) return match;
            }

            return null;
        }

        private static IReadOnlyDictionary<string, ErrorCodeEntry> LoadEntries()
        {
            var assembly = typeof(ErrorCodeCatalog).GetTypeInfo().Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
                throw new InvalidOperationException(
                    $"Embedded resource not found: {ResourceName}. Verify AutopilotMonitor.Shared.csproj " +
                    "includes Resources/error-codes.json as EmbeddedResource.");

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
            };

            var doc = JsonSerializer.Deserialize<CatalogFile>(json, options)
                ?? throw new InvalidOperationException("Failed to deserialize error-codes.json.");

            if (doc.Entries == null)
                throw new InvalidOperationException("error-codes.json has no 'entries' property.");

            // Re-key everything to lower-case for case-insensitive lookups.
            var result = new Dictionary<string, ErrorCodeEntry>(doc.Entries.Count, StringComparer.Ordinal);
            foreach (var pair in doc.Entries)
            {
                result[pair.Key.ToLowerInvariant()] = pair.Value;
            }
            return result;
        }

        private sealed class CatalogFile
        {
            [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
            [JsonPropertyName("description")] public string? Description { get; set; }
            [JsonPropertyName("entries")] public Dictionary<string, ErrorCodeEntry>? Entries { get; set; }
        }
    }
}
