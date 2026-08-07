using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// W365 — reducer-level coverage for the Cloud PC marker fact riding
    /// <see cref="DecisionSignalKind.EnrollmentFactsObserved"/>: the observation records BOTH
    /// values set-once (mirror of the registry self-deploying fact), the profile reason
    /// carries the "no Device-ESP phase expected" expectation, and the signal census folds the
    /// positive marker into terminal audit trails. The engine deliberately has no DeviceSetup
    /// arrival requirement, so no behavioural gate is added — the observation is the
    /// engine-side context that a session starting at Account Setup is the expected shape.
    /// </summary>
    public sealed class CloudPcObservationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"t-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);

        private static DecisionState ReduceFacts(DecisionEngine engine, DecisionState state, long ordinal, string isCloudPc)
            => engine.Reduce(state, MakeSignal(ordinal, DecisionSignalKind.EnrollmentFactsObserved, T0.AddSeconds(ordinal),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsCloudPc] = isCloudPc,
                })).NewState;

        [Fact]
        public void EnrollmentFactsObserved_true_recordsObservation_andExpectationReason()
        {
            var engine = new DecisionEngine();
            var state = engine.Reduce(
                DecisionState.CreateInitial("cpc-1", "t", T0),
                MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            state = ReduceFacts(engine, state, 1, "true");

            Assert.NotNull(state.ScenarioObservations.CloudPc);
            Assert.True(state.ScenarioObservations.CloudPc!.Value);
            Assert.Equal(1, state.ScenarioObservations.CloudPc!.SourceSignalOrdinal);
            Assert.Equal(
                EnrollmentScenarioProfileUpdater.CloudPcNoDeviceEspExpectedReason,
                state.ScenarioProfile.Reason);
            // Stage-agnostic fact signal — the reducer must not move the session anywhere.
            Assert.Equal(SessionStage.SessionStarted, state.Stage);
        }

        [Fact]
        public void EnrollmentFactsObserved_false_recordsObservation_setOnce()
        {
            var engine = new DecisionEngine();
            var state = engine.Reduce(
                DecisionState.CreateInitial("cpc-2", "t", T0),
                MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            state = ReduceFacts(engine, state, 1, "false");
            Assert.NotNull(state.ScenarioObservations.CloudPc);
            Assert.False(state.ScenarioObservations.CloudPc!.Value);

            // Set-once: a later contradicting re-read must not overwrite the first sighting
            // (same contract as RegistrySelfDeployingProfile — replay-deterministic evidence).
            state = ReduceFacts(engine, state, 2, "true");
            Assert.False(state.ScenarioObservations.CloudPc!.Value);
            Assert.Equal(1, state.ScenarioObservations.CloudPc!.SourceSignalOrdinal);
        }

        [Fact]
        public void Census_foldsPositiveMarker_only()
        {
            var engine = new DecisionEngine();
            var initial = engine.Reduce(
                DecisionState.CreateInitial("cpc-3", "t", T0),
                MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var cloudPcState = ReduceFacts(engine, initial, 1, "true");
            var census = DecisionStateSignalCensus.Build(cloudPcState);
            Assert.Contains("cloud_pc_marker", census.SignalsSeen);
            Assert.True(census.SignalEvidence.ContainsKey("cloudPcMarker"));

            var physicalState = ReduceFacts(engine, initial, 1, "false");
            var physicalCensus = DecisionStateSignalCensus.Build(physicalState);
            Assert.DoesNotContain("cloud_pc_marker", physicalCensus.SignalsSeen);
        }
    }
}
