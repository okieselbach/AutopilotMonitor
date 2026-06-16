using System.Text;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Pure NDJSON parsing logic — no service dependencies.
    /// Gzip transport compression is handled upstream by ASP.NET Core's UseRequestDecompression
    /// middleware, so this parser sees an already-decompressed stream.
    /// </summary>
    internal static class NdjsonParser
    {
        /// <summary>
        /// Reads an NDJSON stream with a size cap and parses it.
        /// First line: metadata (sessionId, tenantId). Subsequent lines: EnrollmentEvent objects.
        /// </summary>
        internal static async Task<(string sessionId, string tenantId, List<EnrollmentEvent> events)>
            ParseNdjsonStreamAsync(Stream body, int maxSizeBytes)
        {
            using var buffered = new MemoryStream();
            var buffer = new byte[8192];
            int bytesRead;
            long totalBytesRead = 0;

            while ((bytesRead = await body.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > maxSizeBytes)
                    throw new InvalidOperationException(
                        $"NDJSON payload size exceeds maximum allowed size. " +
                        $"Current size: {totalBytesRead / 1024.0 / 1024.0:F2} MB");
                await buffered.WriteAsync(buffer, 0, bytesRead);
            }

            buffered.Position = 0;
            var ndjson = await new StreamReader(buffered, Encoding.UTF8).ReadToEndAsync();
            return ParseNdjson(ndjson);
        }

        /// <summary>
        /// Parses a raw NDJSON string (uncompressed).
        /// Exposed as internal so tests can call it directly without building gzip payloads.
        /// </summary>
        internal static (string sessionId, string tenantId, List<EnrollmentEvent> events)
            ParseNdjson(string ndjson)
        {
            var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 1)
                throw new InvalidOperationException("NDJSON must contain at least a metadata line");

            var metadata = JsonConvert.DeserializeObject<NdjsonMetadata>(lines[0]);
            if (metadata == null)
                throw new InvalidOperationException("Failed to parse NDJSON metadata");

            var events = new List<EnrollmentEvent>();
            for (int i = 1; i < lines.Length; i++)
            {
                try
                {
                    var evt = JsonConvert.DeserializeObject<EnrollmentEvent>(lines[i]);
                    if (evt != null)
                    {
                        EventDataNormalizer.Normalize(evt);
                        events.Add(evt);
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed event lines rather than failing the entire batch.
                    // A single corrupted event should not cause data loss for valid events.
                }
            }

            return (metadata.SessionId, metadata.TenantId, events);
        }
    }
}
