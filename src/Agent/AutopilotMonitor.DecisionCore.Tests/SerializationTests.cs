using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    public sealed class SerializationTests
    {
        [Fact]
        public void SignalSerializer_roundtrip_preservesAllFields()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 42,
                sessionTraceOrdinal: 99,
                kind: DecisionSignalKind.EspPhaseChanged,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "EspAndHelloTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "esp-phase-detector-v1",
                    summary: "AccountSetup observed in registry",
                    rawPointer: "blob://events/42",
                    derivationInputs: new Dictionary<string, string>
                    {
                        ["registryKey"] = "HKLM\\...Autopilot\\EspStatus",
                        ["rawValue"] = "2",
                    }),
                payload: new Dictionary<string, string>
                {
                    ["phase"] = "AccountSetup",
                });

            var json = SignalSerializer.Serialize(original);
            var roundtripped = SignalSerializer.Deserialize(json);

            Assert.Equal(original.SessionSignalOrdinal, roundtripped.SessionSignalOrdinal);
            Assert.Equal(original.SessionTraceOrdinal, roundtripped.SessionTraceOrdinal);
            Assert.Equal(original.Kind, roundtripped.Kind);
            Assert.Equal(original.KindSchemaVersion, roundtripped.KindSchemaVersion);
            Assert.Equal(original.OccurredAtUtc, roundtripped.OccurredAtUtc);
            Assert.Equal(DateTimeKind.Utc, roundtripped.OccurredAtUtc.Kind);
            Assert.Equal(original.SourceOrigin, roundtripped.SourceOrigin);
            Assert.Equal(original.Evidence.Kind, roundtripped.Evidence.Kind);
            Assert.Equal(original.Evidence.Identifier, roundtripped.Evidence.Identifier);
            Assert.Equal(original.Evidence.Summary, roundtripped.Evidence.Summary);
            Assert.Equal(original.Evidence.RawPointer, roundtripped.Evidence.RawPointer);
            Assert.Equal("HKLM\\...Autopilot\\EspStatus", roundtripped.Evidence.DerivationInputs!["registryKey"]);
            Assert.Equal("AccountSetup", roundtripped.Payload!["phase"]);
        }

        [Fact]
        public void SignalSerializer_Deserialize_MissingEvidence_throws()
        {
            var json = "{\"SessionSignalOrdinal\":0,\"Kind\":\"SessionStarted\",\"KindSchemaVersion\":1,\"OccurredAtUtc\":\"2026-04-20T10:00:00Z\",\"SourceOrigin\":\"test\"}";
            Assert.Throws<JsonSerializationException>(() => SignalSerializer.Deserialize(json));
        }

        /// <summary>
        /// Single-rail typed-sidecar (plan §1.3) — when a signal carries structured
        /// <see cref="EnrollmentEvent.Data"/> through <see cref="DecisionSignal.TypedPayload"/>,
        /// persistence must write it to disk and restore it on read with enough fidelity that
        /// the next <c>TelemetryEventEmitter.Emit</c> re-emits the original wire shape.
        /// Dictionary values come back as Newtonsoft <c>JValue</c>/<c>JArray</c>/<c>JObject</c>
        /// tokens — the same tokens Newtonsoft serializes identically on the outbound side.
        /// </summary>
        [Fact]
        public void SignalSerializer_roundtrip_preserves_TypedPayload_structure()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 7,
                sessionTraceOrdinal: 7,
                kind: DecisionSignalKind.InformationalEvent,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "DeviceInfoCollector",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "informational_event:network_adapters",
                    summary: "Network adapters configuration"),
                payload: new Dictionary<string, string>
                {
                    ["eventType"] = "network_adapters",
                    ["source"] = "DeviceInfoCollector",
                },
                typedPayload: new Dictionary<string, object>
                {
                    ["adapterCount"] = 2,
                    ["adapters"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["description"] = "Intel Wireless",
                            ["macAddress"] = "AA:BB:CC:DD:EE:FF",
                        },
                        new Dictionary<string, object>
                        {
                            ["description"] = "Loopback",
                        },
                    },
                });

            var json = SignalSerializer.Serialize(original);
            var roundtripped = SignalSerializer.Deserialize(json);

            // TypedPayload comes back as Dictionary<string, object> with JToken values —
            // enough for EventTimelineEmitter.ResolveData to consume it as Data, and for
            // Newtonsoft to re-serialize it identically.
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(roundtripped.TypedPayload!);
            // Scalar → JValue(Integer)
            var adapterCount = Assert.IsType<Newtonsoft.Json.Linq.JValue>(typed["adapterCount"]);
            Assert.Equal(2L, adapterCount.Value);
            // Nested list → JArray of JObjects.
            var adapters = Assert.IsType<Newtonsoft.Json.Linq.JArray>(typed["adapters"]);
            Assert.Equal(2, adapters.Count);
            Assert.Equal("Intel Wireless", (string?)adapters[0]["description"]);
            Assert.Equal("AA:BB:CC:DD:EE:FF", (string?)adapters[0]["macAddress"]);
            Assert.Equal("Loopback", (string?)adapters[1]["description"]);
        }

        [Fact]
        public void SignalSerializer_roundtrip_null_TypedPayload_stays_null()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, "session-start", "first signal"),
                payload: null,
                typedPayload: null);

            var roundtripped = SignalSerializer.Deserialize(SignalSerializer.Serialize(original));
            Assert.Null(roundtripped.TypedPayload);
        }

        /// <summary>
        /// Codex Pass-2 finding — top-level null values inside a TypedPayload dict MUST survive
        /// persistence as C# null, not degenerate into <c>string.Empty</c>. Realistic producers
        /// (e.g. <c>DeviceInfoCollector</c> writing <c>displayVersion</c> = null when the
        /// registry key is absent) rely on this for wire/replay parity: live emits
        /// <c>{"displayVersion":null}</c>, so replay must too. The pre-fix code coerced null
        /// tokens to "" at deserialize time, breaking single-rail determinism on replay.
        /// </summary>
        [Fact]
        public void SignalSerializer_roundtrip_null_value_in_TypedPayload_stays_null_not_empty_string()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.InformationalEvent,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "DeviceInfoCollector",
                evidence: new Evidence(EvidenceKind.Raw, "informational_event:os_info", "OS information collected"),
                payload: new Dictionary<string, string>
                {
                    ["eventType"] = "os_info",
                    ["source"] = "DeviceInfoCollector",
                },
                typedPayload: new Dictionary<string, object>
                {
                    ["version"] = "10.0.26100.1",
                    // Realistic: GetOsDisplayVersion() returned null on this SKU — collector
                    // places it directly into Data as null rather than coercing to "".
                    ["displayVersion"] = null!,
                    ["edition"] = "Enterprise",
                });

            var json = SignalSerializer.Serialize(original);
            // On-disk representation must already be JSON null, not "".
            Assert.Contains("\"displayVersion\":null", json);

            var roundtripped = SignalSerializer.Deserialize(json);
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(roundtripped.TypedPayload!);

            // Pass-2 regression: was "" before the fix.
            Assert.True(typed.ContainsKey("displayVersion"));
            Assert.Null(typed["displayVersion"]);

            // Wire-parity check — re-serialize the restored payload as JSON and confirm the
            // null shape is preserved. This is what the outbound TelemetryEventEmitter.Emit
            // chain sees when replay fires through EventTimelineEmitter.
            var reemittedDataJson = Newtonsoft.Json.JsonConvert.SerializeObject(typed);
            Assert.Contains("\"displayVersion\":null", reemittedDataJson);
            Assert.DoesNotContain("\"displayVersion\":\"\"", reemittedDataJson);

            // Sibling non-null values untouched.
            Assert.Equal("10.0.26100.1", ((Newtonsoft.Json.Linq.JValue)typed["version"]).Value);
            Assert.Equal("Enterprise", ((Newtonsoft.Json.Linq.JValue)typed["edition"]).Value);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_unknownValue_mapsToFallback()
        {
            // An old backend reading a row produced by a newer DecisionCore that added a
            // new Stage value must not crash — it reads it as Unknown instead.
            var settings = DecisionCoreJsonSettings.Create();
            var json = "\"NeueUnbekannteStage\"";

            var result = JsonConvert.DeserializeObject<SessionStage>(json, settings);

            Assert.Equal(SessionStage.Unknown, result);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_knownValue_roundtripsByName()
        {
            var settings = DecisionCoreJsonSettings.Create();
            var original = SessionStage.AwaitingHello;

            var json = JsonConvert.SerializeObject(original, settings);
            var roundtripped = JsonConvert.DeserializeObject<SessionStage>(json, settings);

            Assert.Contains("AwaitingHello", json);
            Assert.Equal(original, roundtripped);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_legacyNumericValue_mapsIfDefined()
        {
            var settings = DecisionCoreJsonSettings.Create();
            // EnrollmentPhase.AccountSetup is integer value 4.
            var json = "4";

            var result = JsonConvert.DeserializeObject<EnrollmentPhase>(json, settings);

            Assert.Equal(EnrollmentPhase.AccountSetup, result);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_outOfRangeNumeric_mapsToFallback()
        {
            var settings = DecisionCoreJsonSettings.Create();
            var json = "9999";

            var result = JsonConvert.DeserializeObject<HypothesisLevel>(json, settings);

            Assert.Equal(HypothesisLevel.Unknown, result);
        }

        // ================================================================ Codex follow-up #5 (post-#51)
        // EnrollmentScenarioProfile enums — the five dimensions of the new profile aggregate
        // must be registered in DecisionCoreJsonSettings just like every other DecisionCore
        // enum, otherwise snapshots round-trip as numeric JSON and unknown cross-version
        // values crash instead of landing on the Unknown-fallback. These tests pin the
        // registration contract.

        [Fact]
        public void UnknownFallbackEnumConverter_EnrollmentMode_roundtripsByName_andFallsBackOnUnknown()
        {
            var settings = DecisionCoreJsonSettings.Create();

            var json = JsonConvert.SerializeObject(EnrollmentMode.WhiteGlove, settings);
            Assert.Contains("WhiteGlove", json);
            Assert.Equal(EnrollmentMode.WhiteGlove, JsonConvert.DeserializeObject<EnrollmentMode>(json, settings));

            Assert.Equal(EnrollmentMode.Unknown,
                JsonConvert.DeserializeObject<EnrollmentMode>("\"FutureMode\"", settings));
        }

        [Fact]
        public void UnknownFallbackEnumConverter_EnrollmentJoinMode_roundtripsByName_andFallsBackOnUnknown()
        {
            var settings = DecisionCoreJsonSettings.Create();

            var json = JsonConvert.SerializeObject(EnrollmentJoinMode.HybridAzureAdJoin, settings);
            Assert.Contains("HybridAzureAdJoin", json);
            Assert.Equal(EnrollmentJoinMode.HybridAzureAdJoin,
                JsonConvert.DeserializeObject<EnrollmentJoinMode>(json, settings));

            Assert.Equal(EnrollmentJoinMode.Unknown,
                JsonConvert.DeserializeObject<EnrollmentJoinMode>("\"FutureJoinMode\"", settings));
        }

        [Fact]
        public void UnknownFallbackEnumConverter_EspConfig_roundtripsByName_andFallsBackOnUnknown()
        {
            var settings = DecisionCoreJsonSettings.Create();

            var json = JsonConvert.SerializeObject(EspConfig.DeviceEspOnly, settings);
            Assert.Contains("DeviceEspOnly", json);
            Assert.Equal(EspConfig.DeviceEspOnly, JsonConvert.DeserializeObject<EspConfig>(json, settings));

            Assert.Equal(EspConfig.Unknown,
                JsonConvert.DeserializeObject<EspConfig>("\"NewEspShape\"", settings));
        }

        [Fact]
        public void UnknownFallbackEnumConverter_PreProvisioningSide_roundtripsByName_andFallsBackToNone()
        {
            var settings = DecisionCoreJsonSettings.Create();

            var json = JsonConvert.SerializeObject(PreProvisioningSide.Technician, settings);
            Assert.Contains("Technician", json);
            Assert.Equal(PreProvisioningSide.Technician,
                JsonConvert.DeserializeObject<PreProvisioningSide>(json, settings));

            // The fallback for PreProvisioningSide is None (there is no Unknown member).
            Assert.Equal(PreProvisioningSide.None,
                JsonConvert.DeserializeObject<PreProvisioningSide>("\"FutureSide\"", settings));
        }

        [Fact]
        public void UnknownFallbackEnumConverter_ProfileConfidence_roundtripsByName_andFallsBackToLow()
        {
            var settings = DecisionCoreJsonSettings.Create();

            var json = JsonConvert.SerializeObject(ProfileConfidence.High, settings);
            Assert.Contains("High", json);
            Assert.Equal(ProfileConfidence.High,
                JsonConvert.DeserializeObject<ProfileConfidence>(json, settings));

            Assert.Equal(ProfileConfidence.Low,
                JsonConvert.DeserializeObject<ProfileConfidence>("\"Unverified\"", settings));
        }

        [Fact]
        public void Deserialize_v3Snapshot_withoutDeviceSetupResolvedUtc_field_yieldsNullAnchor()
        {
            // Rollout-safety regression (Plan v9, 88a53223 SelfDeploying defang): the new
            // DeviceSetupResolvedUtc SignalFact is an additive nullable field. Old persisted
            // snapshots from the v3 schema (DecisionState.CurrentSchemaVersion was "v3" before
            // 2026-05-21) DO NOT contain this property in their JSON. Loading such a snapshot
            // under v4 code must succeed and produce DeviceSetupResolvedUtc == null — otherwise
            // any session in flight at the time of the agent upgrade would fail to rehydrate.
            //
            // The JSON below is a minimal v3-shaped DecisionState payload constructed by hand
            // (NOT round-tripped through StateSerializer.Serialize, which would include the new
            // field). It mirrors what an actual on-disk v3 snapshot.json would look like.
            const string v3Json = @"{
                ""SessionId"": ""legacy-session"",
                ""TenantId"": ""legacy-tenant"",
                ""Stage"": ""EspDeviceSetup"",
                ""Outcome"": null,
                ""CurrentEnrollmentPhase"": null,
                ""DeviceSetupEnteredUtc"": null,
                ""AccountSetupEnteredUtc"": null,
                ""FinalizingEnteredUtc"": null,
                ""AccountSetupProvisioningSucceededUtc"": null,
                ""EspFinalExitUtc"": null,
                ""DesktopArrivedUtc"": null,
                ""HelloResolvedUtc"": null,
                ""SystemRebootUtc"": null,
                ""HelloOutcome"": null,
                ""ImeMatchedPatternId"": null,
                ""Deadlines"": [],
                ""LastAppliedSignalOrdinal"": 5,
                ""StepIndex"": 6,
                ""AppInstallFacts"": null,
                ""ScenarioProfile"": null,
                ""ScenarioObservations"": null,
                ""ClassifierOutcomes"": null,
                ""HelloPolicyEnabled"": null,
                ""AgentBootUtc"": ""2026-05-20T09:00:00Z"",
                ""LastFailureTrigger"": null,
                ""RealmJoinFacts"": null,
                ""SchemaVersion"": ""v3""
            }";

            var deserialized = StateSerializer.Deserialize(v3Json);

            // Critical: the missing DeviceSetupResolvedUtc field deserializes to null (the optional
            // ctor parameter default) — no ArgumentException, no JsonSerializationException.
            Assert.Null(deserialized.DeviceSetupResolvedUtc);

            // The rest of the state survived the load — non-default fields are preserved.
            Assert.Equal("legacy-session", deserialized.SessionId);
            Assert.Equal("legacy-tenant", deserialized.TenantId);
            Assert.Equal(SessionStage.EspDeviceSetup, deserialized.Stage);
            Assert.Equal(5L, deserialized.LastAppliedSignalOrdinal);
            Assert.Equal(6, deserialized.StepIndex);
            // SchemaVersion is preserved verbatim (the reducer doesn't auto-upgrade v3 → v4 on read;
            // the next state mutation re-builds via DecisionState ctor and gets the current default).
            Assert.Equal("v3", deserialized.SchemaVersion);
        }

        [Fact]
        public void Deserialize_v4Snapshot_withoutEspAdvisoryFailureRecordedUtc_field_yieldsNullAnchor()
        {
            // Rollout-safety regression (PR1 ContinueAnyway-Defang, Session 4fa5a2d4, 2026-05-22):
            // EspAdvisoryFailureRecordedUtc is an additive nullable field appended to the end of
            // the DecisionState ctor (Codex review #9). Old persisted snapshots from v4 versions
            // before 2026-05-22 DO NOT contain this property. Loading such a snapshot under the
            // PR1+ code must succeed and produce EspAdvisoryFailureRecordedUtc == null —
            // otherwise sessions in flight at agent-upgrade time fail to rehydrate.
            const string priorV4Json = @"{
                ""SessionId"": ""sess-rehydrate"",
                ""TenantId"": ""tenant-rehydrate"",
                ""Stage"": ""EspAccountSetup"",
                ""Outcome"": null,
                ""CurrentEnrollmentPhase"": null,
                ""DeviceSetupEnteredUtc"": null,
                ""AccountSetupEnteredUtc"": null,
                ""FinalizingEnteredUtc"": null,
                ""AccountSetupProvisioningSucceededUtc"": null,
                ""EspFinalExitUtc"": null,
                ""DesktopArrivedUtc"": null,
                ""HelloResolvedUtc"": null,
                ""SystemRebootUtc"": null,
                ""HelloOutcome"": null,
                ""ImeMatchedPatternId"": null,
                ""Deadlines"": [],
                ""LastAppliedSignalOrdinal"": 42,
                ""StepIndex"": 100,
                ""AppInstallFacts"": null,
                ""ScenarioProfile"": null,
                ""ScenarioObservations"": null,
                ""ClassifierOutcomes"": null,
                ""HelloPolicyEnabled"": null,
                ""AgentBootUtc"": ""2026-05-22T08:00:00Z"",
                ""LastFailureTrigger"": null,
                ""RealmJoinFacts"": null,
                ""DeviceSetupResolvedUtc"": null,
                ""SchemaVersion"": ""v4""
            }";

            var deserialized = StateSerializer.Deserialize(priorV4Json);

            // Critical: the missing EspAdvisoryFailureRecordedUtc field deserializes to null
            // (the optional ctor parameter default). No ArgumentException, no JsonSerialization-
            // Exception. The next EspTerminalFailure signal can then write the fact normally.
            Assert.Null(deserialized.EspAdvisoryFailureRecordedUtc);

            // Other state survived.
            Assert.Equal("sess-rehydrate", deserialized.SessionId);
            Assert.Equal(SessionStage.EspAccountSetup, deserialized.Stage);
            Assert.Equal(42L, deserialized.LastAppliedSignalOrdinal);
            Assert.Equal(100, deserialized.StepIndex);
            Assert.Equal("v4", deserialized.SchemaVersion);
        }

        [Fact]
        public void ScenarioProfile_stateRoundtripsThroughStateSerializer_enumsAsStrings()
        {
            // End-to-end regression: the full DecisionState must serialize the new Profile
            // dimensions as string literals (not integers) so cross-version reads stay robust.
            var initial = DecisionState.CreateInitial("s", "t");
            var state = initial
                .ToBuilder()
                .Apply(b => b.ScenarioProfile = b.ScenarioProfile.With(
                    mode: EnrollmentMode.WhiteGlove,
                    joinMode: EnrollmentJoinMode.HybridAzureAdJoin,
                    espConfig: EspConfig.DeviceEspOnly,
                    preProvisioningSide: PreProvisioningSide.Technician,
                    confidence: ProfileConfidence.High,
                    evidenceOrdinal: 7,
                    reason: "test_round_trip"))
                .Build();

            var json = StateSerializer.Serialize(state);

            // Enums persist as their string names, not their underlying integer values.
            Assert.Contains("\"Mode\":\"WhiteGlove\"", json);
            Assert.Contains("\"JoinMode\":\"HybridAzureAdJoin\"", json);
            Assert.Contains("\"EspConfig\":\"DeviceEspOnly\"", json);
            Assert.Contains("\"PreProvisioningSide\":\"Technician\"", json);
            Assert.Contains("\"Confidence\":\"High\"", json);

            var roundtripped = StateSerializer.Deserialize(json);
            var p = roundtripped.ScenarioProfile;
            Assert.Equal(EnrollmentMode.WhiteGlove, p.Mode);
            Assert.Equal(EnrollmentJoinMode.HybridAzureAdJoin, p.JoinMode);
            Assert.Equal(EspConfig.DeviceEspOnly, p.EspConfig);
            Assert.Equal(PreProvisioningSide.Technician, p.PreProvisioningSide);
            Assert.Equal(ProfileConfidence.High, p.Confidence);
            Assert.Equal(7, p.EvidenceOrdinal);
            Assert.Equal("test_round_trip", p.Reason);
        }

        [Fact]
        public void ScenarioObservations_stateRoundtripsThroughStateSerializer()
        {
            // Snapshot-compat pin for the record conversion (2026-07-14): observations were a
            // ctor-based class before; the record deserializes via init setters instead. The
            // persisted JSON shape (property names + SignalFact {Value, SourceSignalOrdinal})
            // is identical, so on-disk snapshots written by either shape must rehydrate — this
            // round-trip guards facts, ordinals, and unset-stays-null across that seam.
            var initial = DecisionState.CreateInitial("s", "t");
            var state = initial
                .ToBuilder()
                .Apply(b => b.ScenarioObservations = b.ScenarioObservations
                    .WithShellCoreWhiteGloveSuccessSeen(sourceSignalOrdinal: 3)
                    .WithSkipUserEsp(value: true, sourceSignalOrdinal: 4)
                    .WithEspSyncFailureTimeoutMinutes(value: 90, sourceSignalOrdinal: 5)
                    .WithRegistrySelfDeployingProfile(value: false, sourceSignalOrdinal: 6))
                .Build();

            var json = StateSerializer.Serialize(state);
            var o = StateSerializer.Deserialize(json).ScenarioObservations;

            Assert.True(o.ShellCoreWhiteGloveSuccessSeen!.Value);
            Assert.Equal(3, o.ShellCoreWhiteGloveSuccessSeen!.SourceSignalOrdinal);
            Assert.True(o.SkipUserEsp!.Value);
            Assert.Equal(4, o.SkipUserEsp!.SourceSignalOrdinal);
            Assert.Equal(90, o.EspSyncFailureTimeoutMinutes!.Value);
            Assert.Equal(5, o.EspSyncFailureTimeoutMinutes!.SourceSignalOrdinal);
            Assert.False(o.RegistrySelfDeployingProfile!.Value);
            Assert.Equal(6, o.RegistrySelfDeployingProfile!.SourceSignalOrdinal);

            // Never-observed facts must rehydrate as null (null ≠ veto — session 62e603c9).
            Assert.Null(o.WhiteGloveSealingPatternSeen);
            Assert.Null(o.AadUserJoinWithUserObserved);
            Assert.Null(o.SkipDeviceEsp);
            Assert.Null(o.EspAllowContinueAnyway);
        }
    }

    internal static class DecisionStateBuilderTestExtensions
    {
        public static DecisionStateBuilder Apply(this DecisionStateBuilder b, Action<DecisionStateBuilder> mutate)
        {
            mutate(b);
            return b;
        }
    }
}
