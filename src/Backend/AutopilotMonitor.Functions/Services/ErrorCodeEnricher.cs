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
    /// Sibling shape: <c>{ description, confidence, source, category, symbol?, derivedFromWin32? }</c>;
    /// for <c>enforcementState</c> the sibling is <c>{ name, description }</c>.
    /// Idempotent: when a sibling info key is already present the entry is skipped.
    /// Read-only on the storage layer — never writes back to the table.
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

        private const string EnforcementStateKey = "enforcementstate";
        private const string EnforcementStateInfoKey = "enforcementStateInfo";

        public static void EnrichEvent(EnrollmentEvent evt)
        {
            if (evt?.Data == null || evt.Data.Count == 0) return;

            foreach (var pair in _codeKeyPairs)
            {
                if (!TryFindKey(evt.Data, pair.CodeKey, out var actualKey)) continue;
                if (evt.Data.ContainsKey(pair.InfoKey)) continue; // idempotent

                var raw = evt.Data[actualKey]?.ToString();
                var result = ErrorCodeCatalog.TryLookupDetailed(raw);
                if (result == null) continue;

                evt.Data[pair.InfoKey] = ToInfo(result);
            }

            if (TryFindKey(evt.Data, EnforcementStateKey, out var stateKey) && !evt.Data.ContainsKey(EnforcementStateInfoKey))
            {
                var state = ErrorCodeCatalog.TryLookupEnforcementState(evt.Data[stateKey]?.ToString());
                if (state != null)
                {
                    evt.Data[EnforcementStateInfoKey] = new { name = state.Name, description = state.Description };
                }
            }
        }

        /// <summary>Wire shape of an <c>*Info</c> sibling; optional members are omitted when absent.</summary>
        private static object ToInfo(ErrorCodeLookupResult result)
        {
            var entry = result.Entry;
            var info = new Dictionary<string, object>(6, StringComparer.Ordinal)
            {
                ["description"] = entry.Description,
                ["confidence"] = entry.Confidence.ToString().ToLowerInvariant(),
                ["source"] = entry.Source,
                ["category"] = entry.Category,
            };
            if (!string.IsNullOrEmpty(entry.Symbol)) info["symbol"] = entry.Symbol!;
            if (result.DerivedFromWin32.HasValue) info["derivedFromWin32"] = result.DerivedFromWin32.Value;
            return info;
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
