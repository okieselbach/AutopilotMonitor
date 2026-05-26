using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Models.Backup
{
    /// <summary>
    /// Centralised JSON options for the critical-table backup feature: manifest write,
    /// NDJSON-line serialisation, HTTP response bodies. Deliberately distinct from
    /// <c>DeletionManifestJson.SerializerOptions</c> because the backup pipeline needs a
    /// <see cref="JsonStringEnumConverter"/> with <c>allowIntegerValues=false</c> so
    /// legacy or hand-crafted manifests with numeric enum values fail loudly instead of
    /// silently mapping to the wrong member.
    /// <para>
    /// <b>DictionaryKeyPolicy is intentionally NOT set</b> — <c>DeletionRowDump.Props</c>
    /// uses the original Azure Table column names (case-sensitive) as dictionary keys;
    /// CamelCasing them would create a new column on restore.
    /// </para>
    /// </summary>
    public static class BackupManifestJson
    {
        public static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // DictionaryKeyPolicy intentionally unset (see remarks above).
            Converters =
            {
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
            },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
    }
}
