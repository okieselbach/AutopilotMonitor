#nullable enable
using System;
using AutopilotMonitor.DecisionCore.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Serialize / deserialize <see cref="TelemetryItem"/> JSONL records.
    /// Plan §2.7a Spool on disk.
    /// <para>
    /// Die innere <see cref="TelemetryItem.PayloadJson"/> wird als escaped-JSON-String geschrieben
    /// (nicht als geöffnetes Objekt) — das hält den Spool-Eintrag vollständig ctor-bindbar und
    /// transportiert den Payload byte-für-byte unverändert zum Backend.
    /// </para>
    /// </summary>
    public static class TelemetryItemSerializer
    {
        /// <summary>
        /// DecisionCore settings plus a <see cref="StringEnumConverter"/>: <see cref="TelemetryItem.Kind"/>
        /// is the only enum on a spool line and has always been written by NAME (<c>"Event"</c>),
        /// which is also what the wire carries. The enum lives in Shared (no Newtonsoft there), so
        /// the converter is registered here instead of as an attribute on the type.
        /// </summary>
        private static readonly JsonSerializerSettings SpoolSettings = CreateSpoolSettings();

        private static JsonSerializerSettings CreateSpoolSettings()
        {
            var settings = DecisionCoreJsonSettings.Create();
            settings.Converters.Add(new StringEnumConverter());
            return settings;
        }

        public static string Serialize(TelemetryItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return JsonConvert.SerializeObject(item, SpoolSettings);
        }

        public static TelemetryItem Deserialize(string line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            try
            {
                var result = JsonConvert.DeserializeObject<TelemetryItem>(line, SpoolSettings);
                if (result == null)
                {
                    throw new JsonSerializationException($"TelemetryItem deserialization produced null: {line}");
                }
                return result;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                throw new JsonSerializationException(
                    $"TelemetryItem constructor rejected payload: {ex.Message}", ex);
            }
        }
    }
}
