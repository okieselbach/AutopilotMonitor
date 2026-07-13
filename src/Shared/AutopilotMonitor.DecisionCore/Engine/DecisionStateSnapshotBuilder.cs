#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Builds a compact JSON-serializable snapshot of <see cref="DecisionState"/> for
    /// inline-attachment to lifecycle-anchor events (Plan §A — Edge-Triggered State
    /// Snapshots, 2026-05-03). The snapshot answers "what did the agent know at this
    /// anchor?" for post-mortem diagnosis without requiring client-side logs.
    /// <para>
    /// Output shape: <c>Dictionary&lt;string, object?&gt;</c> with a fixed top-level
    /// allowlist. Each <see cref="SignalFact{T}"/> property of <see cref="DecisionState"/>
    /// is serialized under <c>facts.{camelCaseName}</c> as a nested
    /// <c>{ value, ordinal }</c> object — <see cref="SignalFact{T}.SourceSignalOrdinal"/>
    /// is preserved so the Inspector can later trace "fact → source signal".
    /// </para>
    /// <para>
    /// Top-level allowlist: <c>schemaVersion</c>, <c>stepIndex</c>,
    /// <c>lastAppliedSignalOrdinal</c>, <c>stage</c>, <c>outcome</c>, <c>facts</c>,
    /// <c>scenario</c>, <c>activeDeadlines</c>. Intentionally NOT in the snapshot:
    /// <c>SessionId</c>/<c>TenantId</c> (PII / redundant on the Sessions row),
    /// <c>AppInstallFacts</c>/<c>ScenarioObservations</c>/<c>ClassifierOutcomes</c>
    /// (too large / hochfrequent), <c>SchemaVersion</c> (DecisionState's own version
    /// — different concept). Adding a top-level field requires a code change.
    /// </para>
    /// </summary>
    public static class DecisionStateSnapshotBuilder
    {
        /// <summary>
        /// Schema version of the snapshot payload. Bumped on incompatible shape changes
        /// so the Web UI / consumers can render forward-compatibly.
        /// </summary>
        public const string SchemaVersion = "decision-state-snapshot-v1";

        /// <summary>
        /// Build a compact snapshot dictionary for the given <paramref name="state"/>.
        /// All <see cref="SignalFact{T}"/> properties of <see cref="DecisionState"/> are
        /// serialized under <c>facts</c>; absent facts appear as JSON <c>null</c>.
        /// </summary>
        public static Dictionary<string, object?> Build(DecisionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["currentEnrollmentPhase"]         = SerializeFact(state.CurrentEnrollmentPhase, v => v.ToString()),
                ["deviceSetupEnteredUtc"]          = SerializeFact(state.DeviceSetupEnteredUtc, FormatUtc),
                ["deviceSetupResolvedUtc"]         = SerializeFact(state.DeviceSetupResolvedUtc, FormatUtc),
                ["accountSetupEnteredUtc"]         = SerializeFact(state.AccountSetupEnteredUtc, FormatUtc),
                ["accountSetupProvisioningSucceededUtc"] = SerializeFact(state.AccountSetupProvisioningSucceededUtc, FormatUtc),
                ["finalizingEnteredUtc"]           = SerializeFact(state.FinalizingEnteredUtc, FormatUtc),
                ["espFinalExitUtc"]                = SerializeFact(state.EspFinalExitUtc, FormatUtc),
                ["desktopArrivedUtc"]              = SerializeFact(state.DesktopArrivedUtc, FormatUtc),
                ["helloResolvedUtc"]               = SerializeFact(state.HelloResolvedUtc, FormatUtc),
                ["systemRebootUtc"]                = SerializeFact(state.SystemRebootUtc, FormatUtc),
                ["helloOutcome"]                   = SerializeFact(state.HelloOutcome, v => v),
                ["imeMatchedPatternId"]            = SerializeFact(state.ImeMatchedPatternId, v => v),
                ["helloPolicyEnabled"]             = SerializeFact(state.HelloPolicyEnabled, v => (object)v),
                ["lastFailureTrigger"]             = SerializeFact(state.LastFailureTrigger, v => v),
                ["espAdvisoryFailureRecordedUtc"]  = SerializeFact(state.EspAdvisoryFailureRecordedUtc, FormatUtc),
                ["imeUserSessionCompletedUtc"]     = SerializeFact(state.ImeUserSessionCompletedUtc, FormatUtc),
                ["completionWaitingFingerprint"]   = SerializeFact(state.CompletionWaitingFingerprint, v => v),
                ["helloWizardStartedUtc"]          = SerializeFact(state.HelloWizardStartedUtc, FormatUtc),
            };

            var scenario = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"]                = state.ScenarioProfile.Mode.ToString(),
                ["joinMode"]            = state.ScenarioProfile.JoinMode.ToString(),
                ["espConfig"]           = state.ScenarioProfile.EspConfig.ToString(),
                ["preProvisioningSide"] = state.ScenarioProfile.PreProvisioningSide.ToString(),
                ["confidence"]          = state.ScenarioProfile.Confidence.ToString(),
                // EvidenceOrdinal == -1 means "never strengthened" — surface as null in JSON
                // so consumers can distinguish "no evidence yet" from "ordinal 0".
                ["evidenceOrdinal"]     = state.ScenarioProfile.EvidenceOrdinal >= 0
                    ? (object?)state.ScenarioProfile.EvidenceOrdinal
                    : null,
                ["reason"]              = state.ScenarioProfile.Reason,
            };

            var deadlines = state.Deadlines
                .Select(d => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"]     = d.Name,
                    ["dueAtUtc"] = FormatUtc(d.DueAtUtc),
                })
                .ToList();

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"]            = SchemaVersion,
                ["stepIndex"]                = state.StepIndex,
                ["lastAppliedSignalOrdinal"] = state.LastAppliedSignalOrdinal,
                ["stage"]                    = state.Stage.ToString(),
                ["outcome"]                  = state.Outcome?.ToString(),
                ["facts"]                    = facts,
                ["scenario"]                 = scenario,
                ["activeDeadlines"]          = deadlines,
            };
        }

        /// <summary>
        /// Serialize a single <see cref="SignalFact{T}"/> as <c>{ value, ordinal }</c>,
        /// or <c>null</c> when the fact is unset. <paramref name="valueProjection"/>
        /// converts the inner value to a JSON-friendly representation (typically string
        /// for enums and timestamps, or the raw value for primitives).
        /// </summary>
        private static Dictionary<string, object?>? SerializeFact<T>(
            SignalFact<T>? fact,
            Func<T, object?> valueProjection)
        {
            if (fact == null) return null;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"]   = valueProjection(fact.Value),
                ["ordinal"] = fact.SourceSignalOrdinal,
            };
        }

        private static string FormatUtc(DateTime ts) =>
            ts.ToString("o", CultureInfo.InvariantCulture);
    }
}
