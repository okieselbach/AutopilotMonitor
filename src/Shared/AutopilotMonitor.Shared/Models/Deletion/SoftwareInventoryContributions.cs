using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AutopilotMonitor.Shared.Models.Deletion
{
    /// <summary>
    /// Single software entry as it arrives from a <c>software_inventory_analysis</c> event.
    /// PR2: the unit that <see cref="DeletionDecrementKey"/> identifies for cascade-time
    /// decrement, plus the metadata needed to (re-)create a fresh <c>SoftwareInventory</c> row
    /// when this is the first session that's seen the software.
    /// </summary>
    public class SoftwareInventoryItem
    {
        // Normalized fields used as the identity key. The triple (Vendor, Name, Version) is
        // the side-row key and the SoftwareInventory RowKey component.
        public string NormalizedVendor { get; set; } = string.Empty;
        public string NormalizedName { get; set; } = string.Empty;
        public string NormalizedVersion { get; set; } = string.Empty;

        // Display metadata — only written on first-insert into SoftwareInventory; later
        // correlations only bump SessionCount + LastSeenAt + LastSessionId.
        public string DisplayName { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string RegistrySource { get; set; } = string.Empty;
        public string NormalizationConfidence { get; set; } = string.Empty;

        /// <summary>
        /// Optional CPE URI for this software (resolved by CpeMappingService at correlation
        /// time). Carried so the increment helper can stamp it on the row at first-insert.
        /// </summary>
        public string? CpeUri { get; set; }

        /// <summary>The canonical key used in the contributions side-row JSON and as the SoftwareInventory RowKey component.</summary>
        public DeletionDecrementKey ToDecrementKey() => new DeletionDecrementKey
        {
            Vendor = NormalizedVendor,
            Name = NormalizedName,
            Version = NormalizedVersion,
        };
    }

    /// <summary>
    /// Result of a <c>UpsertSessionInventoryContributionsAsync</c> call: the items added vs the
    /// keys removed compared to the previous side-row state. Caller applies <c>+1</c> per added
    /// (with full metadata for first-insert) and <c>-1</c> per removed (key only, decrement
    /// clamps at zero).
    /// </summary>
    public class InventoryContributionsDelta
    {
        public List<SoftwareInventoryItem> AddedItems { get; set; } = new List<SoftwareInventoryItem>();
        public List<DeletionDecrementKey> RemovedKeys { get; set; } = new List<DeletionDecrementKey>();

        /// <summary>True when this was the first time the side-row was created for the session.</summary>
        public bool FirstTime { get; set; }
    }

    /// <summary>
    /// JSON + gzip+Base64 codec for the <c>SoftwareKeysJson</c> column on the
    /// <c>SessionInventoryContributions</c> side-row. Pure functions — easy to unit-test
    /// independent of any Azure SDK plumbing.
    /// <para>
    /// Plan §17.7: raw JSON for small key sets, gzip+Base64 once the raw payload exceeds
    /// <see cref="CompressionThresholdBytes"/>. <c>IsCompressed</c> on the row chooses the
    /// decode path. <see cref="DeletionManifestBuilder"/> uses this same codec for the
    /// cascade-time decrement-key extraction (PR1 already wired).
    /// </para>
    /// </summary>
    public static class SoftwareKeysJsonCodec
    {
        public const int CompressionThresholdBytes = 30_000;

        public static EncodedSoftwareKeys Encode(IEnumerable<DeletionDecrementKey> keys)
        {
            var ordered = keys
                .OrderBy(k => k.Vendor, StringComparer.Ordinal)
                .ThenBy(k => k.Name, StringComparer.Ordinal)
                .ThenBy(k => k.Version, StringComparer.Ordinal)
                .ToList();

            var raw = JsonSerializer.Serialize(ordered, DeletionManifestJson.SerializerOptions);
            if (raw.Length <= CompressionThresholdBytes)
            {
                return new EncodedSoftwareKeys { Encoded = raw, IsCompressed = false, KeyCount = ordered.Count };
            }

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(raw);
                gzip.Write(bytes, 0, bytes.Length);
            }
            var compressed = Convert.ToBase64String(output.ToArray());
            return new EncodedSoftwareKeys { Encoded = compressed, IsCompressed = true, KeyCount = ordered.Count };
        }

        public static List<DeletionDecrementKey> Decode(string encoded, bool isCompressed)
        {
            if (string.IsNullOrEmpty(encoded)) return new List<DeletionDecrementKey>();

            string json;
            if (isCompressed)
            {
                byte[] gz;
                try
                {
                    gz = Convert.FromBase64String(encoded);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        "SoftwareKeysJson is marked IsCompressed=true but the payload is not valid Base64.", ex);
                }
                using var input = new MemoryStream(gz);
                using var gunzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gunzip.CopyTo(output);
                json = Encoding.UTF8.GetString(output.ToArray());
            }
            else
            {
                json = encoded;
            }

            try
            {
                return JsonSerializer.Deserialize<List<DeletionDecrementKey>>(json, DeletionManifestJson.SerializerOptions)
                       ?? new List<DeletionDecrementKey>();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "SoftwareKeysJson is not valid JSON for List<DeletionDecrementKey>.", ex);
            }
        }

        /// <summary>
        /// Compares <paramref name="newKeys"/> against <paramref name="oldKeys"/> and returns the
        /// (added, removed) delta. Comparison is case-insensitive on every component because
        /// upstream normalization is best-effort across registry sources.
        /// </summary>
        public static (HashSet<string> Added, HashSet<string> Removed) ComputeDelta(
            IEnumerable<DeletionDecrementKey> oldKeys,
            IEnumerable<DeletionDecrementKey> newKeys)
        {
            var oldSet = ToCompositeSet(oldKeys);
            var newSet = ToCompositeSet(newKeys);
            var added = new HashSet<string>(newSet, StringComparer.OrdinalIgnoreCase);
            added.ExceptWith(oldSet);
            var removed = new HashSet<string>(oldSet, StringComparer.OrdinalIgnoreCase);
            removed.ExceptWith(newSet);
            return (added, removed);
        }

        /// <summary>
        /// Composite-key string used as a lookup identity. Three-part null-safe join with a
        /// separator character that cannot legitimately appear in any normalized component
        /// (we use <c>''</c>, START OF HEADING — never present in publisher / name / version).
        /// </summary>
        public static string CompositeKey(string vendor, string name, string version)
            => $"{vendor}{name}{version}";

        public static string CompositeKey(DeletionDecrementKey key)
            => CompositeKey(key.Vendor ?? string.Empty, key.Name ?? string.Empty, key.Version ?? string.Empty);

        private static HashSet<string> ToCompositeSet(IEnumerable<DeletionDecrementKey> keys)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
            {
                set.Add(CompositeKey(k));
            }
            return set;
        }
    }

    /// <summary>Encoded form of a software-keys JSON payload, tagged with the codec flag.</summary>
    public class EncodedSoftwareKeys
    {
        public string Encoded { get; set; } = string.Empty;
        public bool IsCompressed { get; set; }
        public int KeyCount { get; set; }
    }
}
