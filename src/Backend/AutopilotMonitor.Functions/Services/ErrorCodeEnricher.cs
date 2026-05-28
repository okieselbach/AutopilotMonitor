#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Services;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Read-time enricher that injects <c>errorCodeInfo</c> entries into the <c>Data</c>
    /// dictionary of <see cref="EnrollmentEvent"/>s. The agent emits raw error codes
    /// (<c>0x87d1041c</c>, <c>1603</c>, …) and the backend resolves them at API response
    /// time via the shared <see cref="ErrorCodeCatalog"/>. Web/MCP/API consumers all
    /// benefit without each having to ship its own catalog.
    /// <para>
    /// Idempotent: when a sibling <c>errorCodeInfo</c> is already present the entry is
    /// skipped. Read-only on the storage layer — never writes back to the table.
    /// </para>
    /// </summary>
    public static class ErrorCodeEnricher
    {
        /// <summary>
        /// Keys (lower-cased) whose value the enricher looks up. Match is case-insensitive.
        /// Each is paired with the sibling info-key written next to it in <c>Data</c>.
        /// </summary>
        private static readonly (string CodeKey, string InfoKey)[] _codeKeyPairs = new[]
        {
            ("errorcode", "errorCodeInfo"),
            ("exitcode", "exitCodeInfo"),
            ("hresult", "hresultInfo"),
            ("hresultfromwin32", "hresultFromWin32Info"),
            ("failurecode", "failureCodeInfo"),
            ("code", "codeInfo"),
            ("lasterror", "lastErrorInfo"),
        };

        public static void EnrichEvent(EnrollmentEvent evt)
        {
            if (evt?.Data == null || evt.Data.Count == 0) return;

            foreach (var pair in _codeKeyPairs)
            {
                if (!TryFindKey(evt.Data, pair.CodeKey, out var actualKey)) continue;
                if (evt.Data.ContainsKey(pair.InfoKey)) continue; // idempotent

                var raw = evt.Data[actualKey]?.ToString();
                var entry = ErrorCodeCatalog.TryLookup(raw);
                if (entry == null) continue;

                evt.Data[pair.InfoKey] = new
                {
                    description = entry.Description,
                    confidence = entry.Confidence.ToString().ToLowerInvariant(),
                    source = entry.Source,
                };
            }
        }

        public static void EnrichEvents(IEnumerable<EnrollmentEvent>? events)
        {
            if (events == null) return;
            foreach (var e in events) EnrichEvent(e);
        }

        private static bool TryFindKey(Dictionary<string, object> data, string lowerKey, out string actualKey)
        {
            foreach (var k in data.Keys)
            {
                if (string.Equals(k, lowerKey, StringComparison.OrdinalIgnoreCase))
                {
                    actualKey = k;
                    return true;
                }
            }
            actualKey = string.Empty;
            return false;
        }
    }
}
