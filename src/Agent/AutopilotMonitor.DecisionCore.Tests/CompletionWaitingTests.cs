using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Liveness plan PR2 — <c>completion_waiting</c> observability. Whenever the engine blocks
    /// or defers a completion attempt it says what it is still waiting on; the
    /// <c>CompletionWaitingFingerprint</c> state fact makes the event state-change-only (no
    /// repeats while the missing-set is unchanged, a new event when the set changes).
    /// Coverage:
    /// <list type="bullet">
    ///   <item>1ec8f4c6 replay: blocked completion attempts list exactly the missing
    ///         prerequisites (satisfied facts are not listed).</item>
    ///   <item>Fingerprint dedupe: same missing-set → no second event; set change → new event.</item>
    ///   <item>RealmJoin defer → <c>realmjoin_resolution</c> listed.</item>
    ///   <item>HelloSafety-timeout / AwaitingDesktop / no-promote emission sites.</item>
    ///   <item><c>CompletionWaitingFingerprint</c> serialization compatibility (roundtrip +
    ///         legacy snapshot without the field).</item>
    /// </list>
    /// </summary>
    public sealed class CompletionWaitingTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null,
            string sourceOrigin = "test")
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: sourceOrigin,
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionSignal DeadlineFired(long ordinal, DateTime occurredAtUtc, string deadlineName) =>
            MakeSignal(ordinal, DecisionSignalKind.DeadlineFired, occurredAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = deadlineName });

        private static List<DecisionEffect> CompletionWaitingEffects(DecisionStep step) =>
            step.Effects.Where(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == "completion_waiting").ToList();

        /// <summary>
        /// Classic session driven to "AccountSetup entered, Hello policy known" — the common
        /// prefix of the blocked-completion scenarios.
        /// </summary>
        private static DecisionState SetupAccountSetupSession(
            DecisionEngine engine,
            bool helloPolicyDisabled = true)
        {
            var state = DecisionState.CreateInitial("sess-waiting", "tenant-waiting", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                })).NewState;
            if (helloPolicyDisabled)
            {
                state = engine.Reduce(state, MakeSignal(
                    8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                    new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            }
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(17),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            return state;
        }

        // ===================================================== 1ec8f4c6 replay ====

        [Fact]
        public void DesktopFirst_BlockedFastPath_EmitsWaiting_ListingOnlyTheMissingGate()
        {
            // 1ec8f4c6 shape: Hello disabled, desktop arrives mid-AccountSetup, strong gate
            // (AccountSetupProvisioningSucceeded) unsatisfied. The blocked attempt must list
            // exactly the gate — hello (policy disabled) and desktop (just recorded) are NOT
            // missing.
            var engine = new DecisionEngine();
            var state = SetupAccountSetupSession(engine);

            var step = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.DesktopArrived, T0.AddMinutes(18)));

            var waiting = Assert.Single(CompletionWaitingEffects(step));
            Assert.Equal("account_setup_provisioning_complete", waiting.Parameters!["missingPrerequisites"]);
            Assert.Equal("DesktopArrived", waiting.Parameters!["trigger"]);
            Assert.Equal("Info", waiting.Parameters!["severity"]);
            Assert.Equal(
                "account_setup_provisioning_complete",
                step.NewState.CompletionWaitingFingerprint!.Value);
        }

        [Fact]
        public void GuardBlockedFinalExit_SameMissingSet_IsDeduped_ButStillArmsBackstop()
        {
            // After the desktop-first emission, the guard-blocked esp_exiting computes the SAME
            // missing-set → fingerprint unchanged → no second completion_waiting. The
            // AdvisoryCompletion backstop (b0f7e0fc) still arms.
            var engine = new DecisionEngine();
            var state = SetupAccountSetupSession(engine);
            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.DesktopArrived, T0.AddMinutes(18))).NewState;

            var exitStep = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.EspExiting, T0.AddMinutes(20)));

            Assert.Empty(CompletionWaitingEffects(exitStep));
            Assert.Contains(exitStep.NewState.Deadlines, d => d.Name == DeadlineNames.AdvisoryCompletion);
            Assert.Equal(
                "account_setup_provisioning_complete",
                exitStep.NewState.CompletionWaitingFingerprint!.Value);
        }

        [Fact]
        public void GuardBlockedFinalExit_FirstEmission_CarriesResolutionDeadlineDueAt()
        {
            // No prior desktop → the guard-blocked exit is the first blocked attempt. The event
            // lists gate + desktop and carries the AdvisoryCompletion window's due-time.
            var engine = new DecisionEngine();
            var state = SetupAccountSetupSession(engine);

            var step = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.EspExiting, T0.AddMinutes(20)));

            var waiting = Assert.Single(CompletionWaitingEffects(step));
            Assert.Equal(
                "account_setup_provisioning_complete,desktop_arrival",
                waiting.Parameters!["missingPrerequisites"]);
            Assert.Equal("EspExiting:GuardBlocked", waiting.Parameters!["trigger"]);

            var backstop = step.NewState.Deadlines.Single(d => d.Name == DeadlineNames.AdvisoryCompletion);
            Assert.Equal(backstop.DueAtUtc.ToString("o"), waiting.Parameters!["resolutionDeadlineDueAtUtc"]);
            Assert.Contains(DeadlineNames.AdvisoryCompletion, waiting.Parameters!["armedDeadlines"]);
        }

        [Fact]
        public void PreAccountSetup_HandoffExit_EmitsNothing()
        {
            // The Device→Account handoff exit is normal flow, not a completion attempt.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-handoff", "tenant-handoff", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;

            var step = engine.Reduce(state, MakeSignal(15, DecisionSignalKind.EspExiting, T0.AddMinutes(16)));

            Assert.Empty(CompletionWaitingEffects(step));
            Assert.Null(step.NewState.CompletionWaitingFingerprint);
        }

        // ===================================================== fingerprint dedupe ====

        [Fact]
        public void MissingSetChange_EmitsNewEvent_WithReducedSet()
        {
            // Hello ENABLED flow: blocked exit lists gate+hello+desktop; when Desktop then
            // arrives the set shrinks → a NEW completion_waiting fires with the reduced set.
            var engine = new DecisionEngine();
            var state = SetupAccountSetupSession(engine, helloPolicyDisabled: false);

            var exitStep = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.EspExiting, T0.AddMinutes(20)));
            var first = Assert.Single(CompletionWaitingEffects(exitStep));
            Assert.Equal(
                "account_setup_provisioning_complete,hello_resolution,desktop_arrival",
                first.Parameters!["missingPrerequisites"]);

            var desktopStep = engine.Reduce(
                exitStep.NewState, MakeSignal(50, DecisionSignalKind.DesktopArrived, T0.AddMinutes(22)));
            var second = Assert.Single(CompletionWaitingEffects(desktopStep));
            Assert.Equal(
                "account_setup_provisioning_complete,hello_resolution",
                second.Parameters!["missingPrerequisites"]);
            Assert.Equal(
                "account_setup_provisioning_complete,hello_resolution",
                desktopStep.NewState.CompletionWaitingFingerprint!.Value);
        }

        // ===================================================== realmjoin defer ====

        [Fact]
        public void RealmJoinDefer_ListsRealmJoinResolution()
        {
            // SkipUserEsp flow → genuine final exit promotes to AwaitingHello; an active
            // RealmJoin deployment then closes the completion gate. Hello resolution with
            // Desktop already in defers — the event lists exactly realmjoin_resolution.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-rj", "tenant-rj", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;
            state = engine.Reduce(state, MakeSignal(
                15, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(11),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.DesktopArrived, T0.AddMinutes(12))).NewState;

            var step = engine.Reduce(state, MakeSignal(
                30, DecisionSignalKind.HelloResolved, T0.AddMinutes(13),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));

            var waiting = Assert.Single(CompletionWaitingEffects(step));
            Assert.Equal("realmjoin_resolution", waiting.Parameters!["missingPrerequisites"]);
            Assert.Contains("RealmJoinGateClosed", waiting.Parameters!["trigger"]);
        }

        // ===================================================== other emission sites ====

        [Fact]
        public void HelloSafetyTimeout_AwaitingDesktop_EmitsWaitingOnDesktop()
        {
            // SkipUserEsp flow, Hello policy unknown: final exit → AwaitingHello + HelloSafety.
            // The timeout synthesises Hello=Timeout; Desktop is still missing → event.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-hs", "tenant-hs", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;
            Assert.Equal(SessionStage.AwaitingHello, state.Stage);

            var step = engine.Reduce(state, DeadlineFired(20, T0.AddMinutes(15), DeadlineNames.HelloSafety));

            Assert.Equal(SessionStage.AwaitingDesktop, step.NewState.Stage);
            var waiting = Assert.Single(CompletionWaitingEffects(step));
            Assert.Equal("desktop_arrival", waiting.Parameters!["missingPrerequisites"]);
            Assert.Equal("AwaitingDesktop", waiting.Parameters!["stage"]);
        }

        [Fact]
        public void HelloResolved_DesktopStillMissing_EmitsWaitingOnDesktop()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-hr", "tenant-hr", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;

            var step = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.HelloResolved, T0.AddMinutes(12),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));

            Assert.Equal(SessionStage.AwaitingDesktop, step.NewState.Stage);
            var waiting = Assert.Single(CompletionWaitingEffects(step));
            Assert.Equal("desktop_arrival", waiting.Parameters!["missingPrerequisites"]);
        }

        [Fact]
        public void AccountSetupProvisioningComplete_NoPromote_EmitsWaitingOnHelloAndDesktop()
        {
            // Typical ordering: the strong gate resolves BEFORE the final esp_exiting (no
            // EspFinalExit / FinalizingEntered yet) → no promotion. Hello policy unknown.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-np", "tenant-np", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(17),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state, MakeSignal(
                30, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(20)));

            var waiting = Assert.Single(CompletionWaitingEffects(step));
            Assert.Equal("hello_resolution,desktop_arrival", waiting.Parameters!["missingPrerequisites"]);
            Assert.Equal("AccountSetupProvisioningComplete:NoPromote", waiting.Parameters!["trigger"]);
        }

        // ===================================================== helper unit checks ====

        [Fact]
        public void BuildMissingCompletionPrerequisites_FreshState_ListsAllButOpenGates()
        {
            var state = DecisionState.CreateInitial("s", "t", T0);
            var missing = DecisionEngine.BuildMissingCompletionPrerequisites(state);

            // RealmJoin never detected → its gate is open → not listed.
            Assert.Equal(
                new[] { "account_setup_provisioning_complete", "hello_resolution", "desktop_arrival" },
                missing);
        }

        [Fact]
        public void BuildMissingCompletionPrerequisites_AllSatisfied_IsEmpty()
        {
            var builder = DecisionState.CreateInitial("s", "t", T0).ToBuilder();
            builder.AccountSetupProvisioningSucceededUtc = new SignalFact<DateTime>(T0.AddMinutes(20), 10);
            builder.HelloResolvedUtc = new SignalFact<DateTime>(T0.AddMinutes(21), 11);
            builder.DesktopArrivedUtc = new SignalFact<DateTime>(T0.AddMinutes(22), 12);

            Assert.Empty(DecisionEngine.BuildMissingCompletionPrerequisites(builder.Build()));
        }

        // ================================================== serialization compat ====

        [Fact]
        public void StateSerializer_Roundtrip_PreservesCompletionWaitingFingerprint()
        {
            var builder = DecisionState.CreateInitial("sess-ser", "tenant-ser", T0).ToBuilder();
            builder.CompletionWaitingFingerprint = new SignalFact<string>(
                "hello_resolution,desktop_arrival", sourceSignalOrdinal: 40);
            var state = builder.Build();

            var roundtripped = StateSerializer.Deserialize(StateSerializer.Serialize(state));

            Assert.NotNull(roundtripped.CompletionWaitingFingerprint);
            Assert.Equal("hello_resolution,desktop_arrival", roundtripped.CompletionWaitingFingerprint!.Value);
            Assert.Equal(40, roundtripped.CompletionWaitingFingerprint!.SourceSignalOrdinal);
        }

        [Fact]
        public void Deserialize_v4Snapshot_withoutCompletionWaitingFingerprint_field_yieldsNullFact()
        {
            // Rollout-safety: CompletionWaitingFingerprint is an additive nullable field
            // appended to the end of the DecisionState ctor. Persisted snapshots from versions
            // before 2026-06-12 do not contain the property; rehydration must succeed.
            const string priorV4Json = @"{
                ""SessionId"": ""sess-rehydrate-cw"",
                ""TenantId"": ""tenant-rehydrate-cw"",
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
                ""AgentBootUtc"": ""2026-06-12T08:00:00Z"",
                ""LastFailureTrigger"": null,
                ""RealmJoinFacts"": null,
                ""DeviceSetupResolvedUtc"": null,
                ""EspAdvisoryFailureRecordedUtc"": null,
                ""ImeUserSessionCompletedUtc"": null,
                ""SchemaVersion"": ""v4""
            }";

            var deserialized = StateSerializer.Deserialize(priorV4Json);

            Assert.Null(deserialized.CompletionWaitingFingerprint);
            Assert.Equal("sess-rehydrate-cw", deserialized.SessionId);
            Assert.Equal(SessionStage.EspAccountSetup, deserialized.Stage);
        }
    }
}
