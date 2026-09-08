#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared;
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
    /// <para>
    /// Events emitted by the gather-rule pipeline (<see cref="GatherRuleSource"/>) are only
    /// enriched when the rule opted in via <see cref="GatherRule.EnrichErrorCodes"/> — the agent
    /// then stamps <c>enrichErrorCodes: true</c> into the data. Without it, their payload keys are
    /// author-chosen and the codes come from arbitrary third-party logs or commands (HP Image
    /// Assistant, Dell Command Update, custom scripts …) with their own exit-code numbering;
    /// resolving those against the Windows/MSI/Intune catalog would attach a confident but
    /// wrong meaning.
    /// </para>
    /// </summary>
    public static class ErrorCodeEnricher
    {
        /// <summary>
        /// <see cref="EnrollmentEvent.Source"/> stamped by the agent on every gather-rule event.
        /// </summary>
        internal const string GatherRuleSource = Constants.EventSources.GatherRuleExecutor;

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
            if (IsGatherRuleEvent(evt) && !HasEnrichOptIn(evt.Data)) return;

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

        private static bool IsGatherRuleEvent(EnrollmentEvent evt) =>
            string.Equals(evt.Source, GatherRuleSource, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The agent-stamped opt-in marker. Data may hold a CLR bool (in-process) or a
        /// deserialized JSON token / string (from DataJson), so the check goes via ToString.
        /// </summary>
        private static bool HasEnrichOptIn(Dictionary<string, object> data)
        {
            if (!TryFindKey(data, Constants.GatherRuleDataKeys.EnrichErrorCodes, out var key)) return false;
            var raw = data[key];
            if (raw is bool b) return b;
            return string.Equals(raw?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
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
