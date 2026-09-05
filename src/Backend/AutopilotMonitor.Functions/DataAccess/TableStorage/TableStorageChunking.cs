using System;
using System.Collections.Generic;
using System.Text;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Splits large string values across multiple Azure Table Storage properties
    /// to avoid the 64KB (32K UTF-16 chars) per-property limit.
    /// Backward-compatible: small values use the original property name unchanged.
    /// </summary>
    public static class TableStorageChunking
    {
        /// <summary>
        /// Largest string kept in one property: 30,000 UTF-16 chars stay under the 64 KiB
        /// per-property limit with headroom. The event writer truncates to the same bound;
        /// <see cref="TableTransactionBatcher"/> guards the entity and transaction totals.
        /// </summary>
        public const int MaxPropertyChars = 30_000;

        /// <summary>
        /// Splits a string value into chunked properties if it exceeds maxChunkSize.
        /// Small values: { "Prop": value }
        /// Large values: { "Prop_0": chunk0, "Prop_1": chunk1, "Prop_ChunkCount": "2" }
        /// </summary>
        public static Dictionary<string, string> ChunkProperty(string propertyName, string value, int maxChunkSize = MaxPropertyChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChunkSize)
                return new Dictionary<string, string> { { propertyName, value ?? "" } };

            var result = new Dictionary<string, string>();
            int chunkIndex = 0;
            int offset = 0;
            while (offset < value.Length)
            {
                int length = Math.Min(maxChunkSize, value.Length - offset);
                // Avoid splitting a UTF-16 surrogate pair across chunks: if the last
                // char of this slice is a high surrogate, push it into the next chunk.
                // (No-op when length already covers the remainder.)
                if (length < value.Length - offset && char.IsHighSurrogate(value[offset + length - 1]))
                    length--;

                result[$"{propertyName}_{chunkIndex}"] = value.Substring(offset, length);
                chunkIndex++;
                offset += length;
            }
            result[$"{propertyName}_ChunkCount"] = chunkIndex.ToString();
            return result;
        }

        /// <summary>
        /// Reassembles a potentially chunked property from a dictionary entity.
        /// Handles both old (single property) and new (chunked) formats.
        /// When <paramref name="logger"/> is supplied, emits warnings on detected corruption
        /// (unparseable ChunkCount, zero chunks present, or missing chunks in the middle).
        /// </summary>
        public static string? ReassembleProperty(
            IDictionary<string, object> entity,
            string propertyName,
            ILogger? logger = null,
            string? context = null)
        {
            // Backward compat: single non-chunked property
            if (entity.TryGetValue(propertyName, out var single) && single != null)
            {
                var s = single.ToString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }

            // Chunked format
            if (!entity.TryGetValue($"{propertyName}_ChunkCount", out var countObj))
                return null;

            var rawCount = countObj?.ToString();
            if (!int.TryParse(rawCount, out var count) || count <= 0)
            {
                LogInvalidChunkCount(logger, propertyName, rawCount, context);
                return null;
            }

            var sb = new StringBuilder();
            int foundChunks = 0;
            for (int i = 0; i < count; i++)
            {
                if (entity.TryGetValue($"{propertyName}_{i}", out var chunk) && chunk != null)
                {
                    sb.Append(chunk.ToString());
                    foundChunks++;
                }
            }

            return FinalizeReassembly(sb, foundChunks, count, propertyName, logger, context);
        }

        /// <summary>
        /// Reassembles a potentially chunked property from a TableEntity.
        /// Handles both old (single property) and new (chunked) formats.
        /// When <paramref name="logger"/> is supplied, emits warnings on detected corruption.
        /// </summary>
        public static string? ReassembleProperty(
            TableEntity entity,
            string propertyName,
            ILogger? logger = null,
            string? context = null)
        {
            // Backward compat: single non-chunked property
            var single = entity.GetString(propertyName);
            if (!string.IsNullOrEmpty(single))
                return single;

            // Chunked format
            if (!entity.ContainsKey($"{propertyName}_ChunkCount"))
                return null;

            var countStr = entity.GetString($"{propertyName}_ChunkCount");
            if (!int.TryParse(countStr, out var count) || count <= 0)
            {
                LogInvalidChunkCount(logger, propertyName, countStr, context);
                return null;
            }

            var sb = new StringBuilder();
            int foundChunks = 0;
            for (int i = 0; i < count; i++)
            {
                var chunk = entity.GetString($"{propertyName}_{i}");
                if (chunk != null)
                {
                    sb.Append(chunk);
                    foundChunks++;
                }
            }

            return FinalizeReassembly(sb, foundChunks, count, propertyName, logger, context);
        }

        private static string? FinalizeReassembly(
            StringBuilder sb,
            int foundChunks,
            int expectedCount,
            string propertyName,
            ILogger? logger,
            string? context)
        {
            if (foundChunks == 0)
            {
                logger?.LogWarning(
                    "Chunked property {PropertyName} has ChunkCount={Expected} but no chunks found{Context}",
                    propertyName, expectedCount, FormatContext(context));
                return null;
            }

            if (foundChunks < expectedCount)
            {
                logger?.LogWarning(
                    "Chunked property {PropertyName} incomplete: expected {Expected} chunks, got {Actual}{Context}. Returning partial value of {Length} chars.",
                    propertyName, expectedCount, foundChunks, FormatContext(context), sb.Length);
            }

            return sb.ToString();
        }

        private static void LogInvalidChunkCount(ILogger? logger, string propertyName, string? rawValue, string? context)
        {
            logger?.LogWarning(
                "Chunked property {PropertyName} has invalid ChunkCount value '{RawValue}'{Context}",
                propertyName, rawValue ?? "<null>", FormatContext(context));
        }

        private static string FormatContext(string? context)
            => string.IsNullOrEmpty(context) ? string.Empty : $" [{context}]";
    }
}
