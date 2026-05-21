using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §6 Fix 9 + Codex follow-up #5 — <see cref="DecisionSignalKind.EspConfigDetected"/>
    /// populates <see cref="EnrollmentScenarioObservations.SkipUserEsp"/> /
    /// <see cref="EnrollmentScenarioObservations.SkipDeviceEsp"/> raw observations and, once
    /// BOTH halves are known, derives <see cref="EnrollmentScenarioProfile.EspConfig"/>.
    /// Set-once semantics: later signals with the same or different payload are no-ops
    /// once an observation is present (monotonic).
    /// </summary>
    public sealed class EspConfigDetectedHandlerTests
    {
        [Fact]
        public void EspConfigDetected_populates_observations_and_derives_EspConfig()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 3,
                skipUser: "true",
                skipDevice: "false"));

            var obs = step.NewState.ScenarioObservations;
            Assert.NotNull(obs.SkipUserEsp);
            Assert.True(obs.SkipUserEsp!.Value);
            Assert.Equal(3, obs.SkipUserEsp!.SourceSignalOrdinal);

            Assert.NotNull(obs.SkipDeviceEsp);
            Assert.False(obs.SkipDeviceEsp!.Value);
            Assert.Equal(3, obs.SkipDeviceEsp!.SourceSignalOrdinal);

            // Both halves observed → Profile.EspConfig derived.
            Assert.Equal(EspConfig.DeviceEspOnly, step.NewState.ScenarioProfile.EspConfig);

            Assert.True(step.Transition.Taken);
            Assert.Null(step.Transition.DeadEndReason);
            Assert.Equal(nameof(DecisionSignalKind.EspConfigDetected), step.Transition.Trigger);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void EspConfigDetected_leaves_stage_unchanged()
        {
            var engine = new DecisionEngine();
            var midFlight = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .Build();

            var step = engine.Reduce(midFlight, MakeSignal(
                ordinal: 5,
                skipUser: "false",
                skipDevice: "false"));

            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
            Assert.Equal(6, step.NewState.StepIndex);
            Assert.Equal(5, step.NewState.LastAppliedSignalOrdinal);
            Assert.Equal(EspConfig.FullEsp, step.NewState.ScenarioProfile.EspConfig);
        }

        [Fact]
        public void EspConfigDetected_is_setonce_laterSignalDoesNotOverwrite()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step1 = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "false",
                skipDevice: "false"));

            // Second signal flips both values — must be ignored.
            var step2 = engine.Reduce(step1.NewState, MakeSignal(
                ordinal: 2,
                skipUser: "true",
                skipDevice: "true"));

            var obs = step2.NewState.ScenarioObservations;
            Assert.False(obs.SkipUserEsp!.Value);
            Assert.Equal(1, obs.SkipUserEsp!.SourceSignalOrdinal);
            Assert.False(obs.SkipDeviceEsp!.Value);
            Assert.Equal(1, obs.SkipDeviceEsp!.SourceSignalOrdinal);

            // Profile.EspConfig was derived on the first signal and not regressed.
            Assert.Equal(EspConfig.FullEsp, step2.NewState.ScenarioProfile.EspConfig);

            // The second signal still bumps bookkeeping (taken transition, no effects).
            Assert.True(step2.Transition.Taken);
            Assert.Equal(2, step2.NewState.StepIndex);
            Assert.Equal(2, step2.NewState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void EspConfigDetected_missingKeys_leavesFactsNull_andProfileUnknown()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: null,
                skipDevice: null));

            var obs = step.NewState.ScenarioObservations;
            Assert.Null(obs.SkipUserEsp);
            Assert.Null(obs.SkipDeviceEsp);
            Assert.Equal(EspConfig.Unknown, step.NewState.ScenarioProfile.EspConfig);
            Assert.True(step.Transition.Taken);
            Assert.Equal(1, step.NewState.StepIndex);
        }

        [Fact]
        public void EspConfigDetected_partialPayload_setsOnlyKnownFact_profileStaysUnknown()
        {
            // Realistic: registry has SkipUserStatusPage but SkipDeviceStatusPage key missing.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "true",
                skipDevice: null));

            var obs = step.NewState.ScenarioObservations;
            Assert.NotNull(obs.SkipUserEsp);
            Assert.True(obs.SkipUserEsp!.Value);
            Assert.Null(obs.SkipDeviceEsp);
            // Only one half observed → Profile.EspConfig cannot be derived yet.
            Assert.Equal(EspConfig.Unknown, step.NewState.ScenarioProfile.EspConfig);
        }

        [Fact]
        public void EspConfigDetected_partialFirstSignal_secondSignalCompletes_derivesEspConfig()
        {
            // First signal sets only skipUser; second signal (different ordinal) can still fill
            // in skipDevice because set-once is per-observation, not per-signal. Profile.EspConfig
            // is derived once both halves are known.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step1 = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "true",
                skipDevice: null));

            var step2 = engine.Reduce(step1.NewState, MakeSignal(
                ordinal: 2,
                skipUser: null,
                skipDevice: "false"));

            var obs = step2.NewState.ScenarioObservations;
            Assert.NotNull(obs.SkipUserEsp);
            Assert.True(obs.SkipUserEsp!.Value);
            Assert.Equal(1, obs.SkipUserEsp!.SourceSignalOrdinal);

            Assert.NotNull(obs.SkipDeviceEsp);
            Assert.False(obs.SkipDeviceEsp!.Value);
            Assert.Equal(2, obs.SkipDeviceEsp!.SourceSignalOrdinal);

            Assert.Equal(EspConfig.DeviceEspOnly, step2.NewState.ScenarioProfile.EspConfig);
        }

        [Fact]
        public void EspConfigDetected_schemaVersion2_fallsThroughAsUnhandled()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "true",
                skipDevice: "false",
                schemaVersion: 2));

            Assert.False(step.Transition.Taken);
            Assert.Equal("unhandled_signal_kind:EspConfigDetected:v2", step.Transition.DeadEndReason);
        }

        [Fact]
        public void EspConfigDetected_stateRoundtripsThroughSerializer()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var populated = engine.Reduce(state, MakeSignal(
                ordinal: 7,
                skipUser: "true",
                skipDevice: "false")).NewState;

            var json = StateSerializer.Serialize(populated);
            var roundtripped = StateSerializer.Deserialize(json);

            var obs = roundtripped.ScenarioObservations;
            Assert.NotNull(obs.SkipUserEsp);
            Assert.True(obs.SkipUserEsp!.Value);
            Assert.Equal(7, obs.SkipUserEsp!.SourceSignalOrdinal);

            Assert.NotNull(obs.SkipDeviceEsp);
            Assert.False(obs.SkipDeviceEsp!.Value);
            Assert.Equal(7, obs.SkipDeviceEsp!.SourceSignalOrdinal);

            Assert.Equal(EspConfig.DeviceEspOnly, roundtripped.ScenarioProfile.EspConfig);
        }

        [Fact]
        public void EspConfigDetected_initialState_observations_areNull()
        {
            var state = DecisionState.CreateInitial("s", "t");
            Assert.Null(state.ScenarioObservations.SkipUserEsp);
            Assert.Null(state.ScenarioObservations.SkipDeviceEsp);
            Assert.Equal(EspConfig.Unknown, state.ScenarioProfile.EspConfig);
        }

        [Fact]
        public void EspConfigDetected_captures_SyncFailureTimeoutMinutes_when_provided()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SignalPayloadKeys.SkipUserEsp] = "false",
                [SignalPayloadKeys.SkipDeviceEsp] = "false",
                [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "90",
                [SignalPayloadKeys.EspAllowContinueAnyway] = "true",
            };
            var signal = MakeSignalWithPayload(ordinal: 4, payload: payload);

            var obs = engine.Reduce(state, signal).NewState.ScenarioObservations;

            Assert.NotNull(obs.EspSyncFailureTimeoutMinutes);
            Assert.Equal(90, obs.EspSyncFailureTimeoutMinutes!.Value);
            Assert.Equal(4, obs.EspSyncFailureTimeoutMinutes!.SourceSignalOrdinal);

            Assert.NotNull(obs.EspAllowContinueAnyway);
            Assert.True(obs.EspAllowContinueAnyway!.Value);
            Assert.Equal(4, obs.EspAllowContinueAnyway!.SourceSignalOrdinal);
        }

        [Fact]
        public void EspConfigDetected_ignores_invalid_or_zero_timeout()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SignalPayloadKeys.SkipUserEsp] = "false",
                [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "0",
                [SignalPayloadKeys.EspAllowContinueAnyway] = "not-a-bool",
            };

            var obs = engine.Reduce(state, MakeSignalWithPayload(ordinal: 5, payload)).NewState.ScenarioObservations;

            Assert.Null(obs.EspSyncFailureTimeoutMinutes);
            Assert.Null(obs.EspAllowContinueAnyway);
        }

        [Fact]
        public void EspConfigDetected_continueAnyway_setOnce_keepsFirstOrdinal()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var first = engine.Reduce(state, MakeSignalWithPayload(
                ordinal: 6,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "true",
                })).NewState;

            var second = engine.Reduce(first, MakeSignalWithPayload(
                ordinal: 10,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "false",
                })).NewState;

            var fact = second.ScenarioObservations.EspAllowContinueAnyway;
            Assert.NotNull(fact);
            Assert.True(fact!.Value);
            Assert.Equal(6, fact!.SourceSignalOrdinal);
        }

        private static DecisionSignal MakeSignal(
            long ordinal,
            string? skipUser,
            string? skipDevice,
            int schemaVersion = 1)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            if (skipUser != null) payload[SignalPayloadKeys.SkipUserEsp] = skipUser;
            if (skipDevice != null) payload[SignalPayloadKeys.SkipDeviceEsp] = skipDevice;
            return MakeSignalWithPayload(ordinal, payload, schemaVersion);
        }

        private static DecisionSignal MakeSignalWithPayload(
            long ordinal,
            Dictionary<string, string> payload,
            int schemaVersion = 1)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.EspConfigDetected,
                kindSchemaVersion: schemaVersion,
                occurredAtUtc: new DateTime(2026, 4, 23, 18, 53, 21, DateTimeKind.Utc),
                sourceOrigin: "DeviceInfoCollector",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "esp_config_detected",
                    summary: "test"),
                payload: payload);
        }
    }
}
