#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Tests for the edge-triggered DecisionState snapshot builder (Plan §A,
    /// 2026-05-03). Covers schema-version pinning, top-level allowlist, per-fact
    /// {value, ordinal} provenance, the explicit Reflection-coverage of all
    /// <see cref="SignalFact{T}"/> properties (so future SignalFact additions to
    /// DecisionState are caught automatically by the test suite, not silently
    /// dropped on the wire), and JSON round-trip stability.
    /// </summary>
    public sealed class DecisionStateSnapshotBuilderTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 5, 1, 13, 45, 32, DateTimeKind.Utc);

        // ============================================================================
        // SchemaVersion — checked against the const, not a duplicated literal
        // ============================================================================

        [Fact]
        public void Build_top_level_includes_schemaVersion_referencing_the_const()
        {
            var snapshot = DecisionStateSnapshotBuilder.Build(DecisionState.CreateInitial("s", "t"));

            Assert.True(snapshot.ContainsKey("schemaVersion"));
            Assert.Equal(DecisionStateSnapshotBuilder.SchemaVersion, snapshot["schemaVersion"]);
        }

        [Fact]
        public void SchemaVersion_const_value_is_v1()
        {
            // Pinning the actual version string here is intentional — bumping the schema
            // is a deliberate act that should require updating this test alongside any
            // consumer (UI / Inspector) that depends on a specific shape.
            Assert.Equal("decision-state-snapshot-v1", DecisionStateSnapshotBuilder.SchemaVersion);
        }

        // ============================================================================
        // Top-level allowlist — exactly these 8 keys, no PII / no large fields
        // ============================================================================

        [Fact]
        public void Build_top_level_keys_match_explicit_allowlist()
        {
            var expected = new HashSet<string>
            {
                "schemaVersion", "stepIndex", "lastAppliedSignalOrdinal",
                "stage", "outcome", "facts", "scenario", "activeDeadlines",
            };

            var actual = new HashSet<string>(DecisionStateSnapshotBuilder.Build(
                DecisionState.CreateInitial("s", "t")).Keys);

            Assert.True(expected.SetEquals(actual),
                $"Top-level keys diverged. Expected={string.Join(",", expected.OrderBy(x => x))} " +
                $"Actual={string.Join(",", actual.OrderBy(x => x))}");
        }

        [Theory]
        [InlineData("SessionId")]
        [InlineData("TenantId")]
        [InlineData("AppInstallFacts")]
        [InlineData("ScenarioObservations")]
        [InlineData("ClassifierOutcomes")]
        public void Build_top_level_does_NOT_expose_excluded_decisionstate_fields(string excludedField)
        {
            // Note: DecisionState.SchemaVersion is intentionally excluded from this list —
            // the snapshot DOES have a top-level "schemaVersion" key, but its meaning is
            // "snapshot-payload-schema-v1", not DecisionState's internal state-machine
            // schema. Two different concepts that happen to share a camelCase name.
            var snapshot = DecisionStateSnapshotBuilder.Build(DecisionState.CreateInitial("s", "t"));

            Assert.DoesNotContain(snapshot.Keys, k =>
                string.Equals(k, excludedField, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, ToCamelCase(excludedField), StringComparison.Ordinal));
        }

        // ============================================================================
        // Empty / fresh state — facts all null, scenario at defaults
        // ============================================================================

        [Fact]
        public void Build_freshState_factsAllNull()
        {
            var snapshot = DecisionStateSnapshotBuilder.Build(DecisionState.CreateInitial("s", "t"));
            var facts = (Dictionary<string, object?>)snapshot["facts"]!;

            // The WG-resume cleanup (2026-05-04) dropped the four V2-only post-reseal
            // SignalFacts along with the rest of the dedicated Part-2 apparatus; the
            // Classic Hello/Desktop/AAD facts cover both runs now, leaving 11 slots.
            // The c117946b debrief (2026-05-12) added `lastFailureTrigger` so the
            // EnrollmentTerminationHandler can discriminate which Failed pathway fired
            // and only promote in-flight installs on ESP-Apps timeout → 12 slots.
            // Session 330f73f3 fix (2026-05-18) added `accountSetupProvisioningSucceededUtc`
            // as the strong post-AccountSetup gate → 13 slots.
            // 88a53223 SelfDeploying defang (Plan v9) added `deviceSetupResolvedUtc` so the
            // new DeviceOnlyEspDetection deadline arms from DeviceSetup-END not -START → 14.
            // PR1 ContinueAnyway-Defang (Session 4fa5a2d4, 2026-05-22) added
            // `espAdvisoryFailureRecordedUtc` as the fire-once gate for downgraded ESP
            // terminal failures → 15.
            Assert.Equal(15, facts.Count);
            Assert.All(facts.Values, v => Assert.Null(v));
        }

        [Fact]
        public void Build_freshState_scenarioAtDefaults()
        {
            var snapshot = DecisionStateSnapshotBuilder.Build(DecisionState.CreateInitial("s", "t"));
            var scenario = (Dictionary<string, object?>)snapshot["scenario"]!;

            Assert.Equal(EnrollmentScenarioProfile.Empty.Mode.ToString(), scenario["mode"]);
            Assert.Equal(EnrollmentScenarioProfile.Empty.JoinMode.ToString(), scenario["joinMode"]);
            // EvidenceOrdinal == -1 ("never strengthened") → null in JSON
            Assert.Null(scenario["evidenceOrdinal"]);
        }

        [Fact]
        public void Build_freshState_activeDeadlines_isEmpty()
        {
            var snapshot = DecisionStateSnapshotBuilder.Build(DecisionState.CreateInitial("s", "t"));
            var deadlines = (List<object>)snapshot["activeDeadlines"]!;
            Assert.Empty(deadlines);
        }

        // ============================================================================
        // Per-SignalFact field — value + ordinal provenance preserved, camelCase key
        // ============================================================================

        [Fact]
        public void Build_DesktopArrivedUtc_carries_value_and_ordinal()
        {
            var state = DecisionState.CreateInitial("s", "t").ToBuilder()
                .Build()
                .ToBuilder();
            state.DesktopArrivedUtc = new SignalFact<DateTime>(Fixed, sourceSignalOrdinal: 28);
            var built = state.Build();

            var snapshot = DecisionStateSnapshotBuilder.Build(built);
            var facts = (Dictionary<string, object?>)snapshot["facts"]!;
            var fact = (Dictionary<string, object?>)facts["desktopArrivedUtc"]!;

            Assert.Equal(Fixed.ToString("o"), fact["value"]);
            Assert.Equal(28L, fact["ordinal"]);
        }

        [Fact]
        public void Build_HelloOutcome_string_fact_preserves_value()
        {
            var b = DecisionState.CreateInitial("s", "t").ToBuilder();
            b.HelloOutcome = new SignalFact<string>("completed", sourceSignalOrdinal: 99);

            var snapshot = DecisionStateSnapshotBuilder.Build(b.Build());
            var facts = (Dictionary<string, object?>)snapshot["facts"]!;
            var fact = (Dictionary<string, object?>)facts["helloOutcome"]!;

            Assert.Equal("completed", fact["value"]);
            Assert.Equal(99L, fact["ordinal"]);
        }

        [Fact]
        public void Build_HelloPolicyEnabled_bool_fact_preserves_value_as_boolean()
        {
            var b = DecisionState.CreateInitial("s", "t").ToBuilder();
            b.HelloPolicyEnabled = new SignalFact<bool>(true, sourceSignalOrdinal: 42);

            var snapshot = DecisionStateSnapshotBuilder.Build(b.Build());
            var facts = (Dictionary<string, object?>)snapshot["facts"]!;
            var fact = (Dictionary<string, object?>)facts["helloPolicyEnabled"]!;

            // Bool stays bool (no string-coercion) so the wire-payload is faithful.
            Assert.Equal(true, fact["value"]);
            Assert.Equal(42L, fact["ordinal"]);
        }

        [Fact]
        public void Build_CurrentEnrollmentPhase_enum_fact_serializes_as_enum_name()
        {
            var b = DecisionState.CreateInitial("s", "t").ToBuilder();
            b.CurrentEnrollmentPhase = new SignalFact<EnrollmentPhase>(EnrollmentPhase.AccountSetup, sourceSignalOrdinal: 18);

            var snapshot = DecisionStateSnapshotBuilder.Build(b.Build());
            var facts = (Dictionary<string, object?>)snapshot["facts"]!;
            var fact = (Dictionary<string, object?>)facts["currentEnrollmentPhase"]!;

            Assert.Equal("AccountSetup", fact["value"]);
            Assert.Equal(18L, fact["ordinal"]);
        }

        // ============================================================================
        // Reflection coverage — scoped to SignalFact<T>, with camelCase pinning
        // ============================================================================

        [Fact]
        public void Build_covers_every_SignalFact_property_of_DecisionState_with_camelCase_keys()
        {
            // Discover all SignalFact<T> properties on DecisionState via reflection.
            // Test fails the moment a new SignalFact field is added to DecisionState
            // without also being projected into the snapshot — preventing silent loss
            // of evidence on the wire when the state model evolves.
            var expectedFactKeys = typeof(DecisionState)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType
                            && p.PropertyType.GetGenericTypeDefinition() == typeof(SignalFact<>))
                .Select(p => ToCamelCase(p.Name))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            // PR-B (2026-05-04): 11 SignalFact properties on DecisionState (was 15;
            // the 4 WG-Part-2 facts were removed). c117946b debrief (2026-05-12):
            // `lastFailureTrigger` added for the EnrollmentTerminationHandler's
            // 4-check discriminator → 12. Session 330f73f3 fix (2026-05-18) added
            // `accountSetupProvisioningSucceededUtc` as the strong post-AccountSetup
            // gate → 13. 88a53223 SelfDeploying defang (Plan v9, 2026-05-21) added
            // `deviceSetupResolvedUtc` as the DeviceSetup-END anchor for the new
            // deadline arm-point → 14. PR1 ContinueAnyway-Defang (Session 4fa5a2d4,
            // 2026-05-22) added `espAdvisoryFailureRecordedUtc` as the fire-once gate
            // for downgraded ESP terminal failures → 15. If this number ever changes,
            // both the count expectation AND the actual snapshot output need to evolve
            // in lockstep.
            Assert.Equal(15, expectedFactKeys.Count);

            var snapshot = DecisionStateSnapshotBuilder.Build(DecisionState.CreateInitial("s", "t"));
            var facts = (Dictionary<string, object?>)snapshot["facts"]!;
            var actualFactKeys = facts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.Equal(expectedFactKeys, actualFactKeys);
        }

        // ============================================================================
        // JSON round-trip — Newtonsoft serializes Dict<string, object?> with nulls
        // ============================================================================

        [Fact]
        public void Build_round_trips_through_Newtonsoft_JSON()
        {
            var b = DecisionState.CreateInitial("s", "t").ToBuilder();
            b.DesktopArrivedUtc = new SignalFact<DateTime>(Fixed, sourceSignalOrdinal: 28);
            b.HelloOutcome = new SignalFact<string>("skipped", sourceSignalOrdinal: 50);

            var snapshot = DecisionStateSnapshotBuilder.Build(b.Build());
            var json = JsonConvert.SerializeObject(snapshot, Formatting.None);

            // Null facts must serialize as JSON null, not be skipped (a missing key
            // would force consumers to differentiate "not in payload" from "absent
            // fact" — null in payload is the cleaner contract).
            Assert.Contains("\"espFinalExitUtc\":null", json);

            // Set facts must serialize value + ordinal — confirm both are visible.
            Assert.Contains("\"desktopArrivedUtc\":{\"value\":\"2026-05-01T13:45:32.0000000Z\",\"ordinal\":28}", json);
            Assert.Contains("\"helloOutcome\":{\"value\":\"skipped\",\"ordinal\":50}", json);
        }

        // ============================================================================
        // Null-arg guard
        // ============================================================================

        [Fact]
        public void Build_null_state_throws()
        {
            Assert.Throws<ArgumentNullException>(() => DecisionStateSnapshotBuilder.Build(null!));
        }

        // ---------- helpers ----------

        private static string ToCamelCase(string pascal)
        {
            if (string.IsNullOrEmpty(pascal)) return pascal;
            return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        }
    }
}
