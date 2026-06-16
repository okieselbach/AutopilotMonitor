using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Normalizes an <see cref="EnrollmentEvent.Data"/> graph produced by Newtonsoft into plain
    /// .NET types (<see cref="Dictionary{TKey,TValue}"/>, <see cref="List{T}"/>, primitives).
    ///
    /// Both ingest paths deserialize the agent payload with Newtonsoft into a
    /// <c>Dictionary&lt;string, object&gt;</c>; for NESTED values Newtonsoft leaves
    /// <see cref="JObject"/>/<see cref="JArray"/> instances in place. Downstream storage
    /// (e.g. <c>UpsertDeviceSnapshotAsync</c>) re-serializes <c>Data</c> with System.Text.Json,
    /// which cannot represent a Newtonsoft <c>JToken</c> and emits corrupt output
    /// (e.g. <c>"disks":[[[[]],[[]]]]</c>). Converting the graph to native types first makes
    /// the value System.Text.Json-safe and keeps both ingest paths behaviourally identical.
    /// </summary>
    internal static class EventDataNormalizer
    {
        /// <summary>
        /// Replaces <paramref name="evt"/>'s <c>Data</c> with a graph of native .NET types.
        /// No-op when there is nothing to normalize.
        /// </summary>
        internal static void Normalize(EnrollmentEvent evt)
        {
            if (evt.Data == null || evt.Data.Count == 0) return;
            evt.Data = NormalizeMap(evt.Data);
        }

        /// <summary>
        /// Returns a new map with every value converted to native .NET types.
        /// Used by the read path when re-hydrating stored event data.
        /// </summary>
        internal static Dictionary<string, object> NormalizeMap(Dictionary<string, object>? data)
        {
            var normalized = new Dictionary<string, object>();
            if (data == null) return normalized;
            foreach (var kvp in data)
                normalized[kvp.Key] = ConvertJTokenToNative(kvp.Value);
            return normalized;
        }

        private static object ConvertJTokenToNative(object value)
        {
            if (value is JArray jArray)
                return jArray.Select(item => ConvertJTokenToNative(item)).ToList<object>();
            if (value is JObject jObject)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObject.Properties())
                    dict[prop.Name] = ConvertJTokenToNative(prop.Value);
                return dict;
            }
            if (value is JValue jValue)
                return jValue.Value ?? string.Empty;
            return value;
        }
    }
}
