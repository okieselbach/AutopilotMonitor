using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.State
{
    /// <summary>
    /// Signal → <see cref="EnrollmentScenarioProfile"/> dispatch tests for
    /// <see cref="EnrollmentScenarioProfileUpdater"/>. Codex follow-up #5.
    /// Covers monotonic confidence, mode preservation, and the split-payload
    /// EspConfig contract.
    /// </summary>
    public sealed class EnrollmentScenarioProfileUpdaterTests
    {
        private static readonly DateTime FixedUtc = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);

        // ================================================================== EnrollmentFactsObserved

        // V2 race-fix (10c8e0bf debrief, 2026-04-26) — these tests cover the new
        // dedicated facts signal that replaced the profile-seeding side of
        // SessionStarted. Behaviour parity with the legacy tests is preserved
        // (same payload keys, same Mode/JoinMode mapping); the reason token now
        // reads `enrollment_facts_observed:*` so it can be told apart in the
        // Inspector trace.

        [Fact]
        public void EnrollmentFactsObserved_v2_setsDevicePreparation_mediumConfidence()
        {
            var signal = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 0,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v2",
                    [SignalPayloadKeys.IsHybridJoin] = "false",
                });

            var profile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Equal(EnrollmentMode.DevicePreparation, profile.Mode);
            Assert.Equal(ProfileConfidence.Medium, profile.Confidence);
            Assert.Equal(EnrollmentJoinMode.AzureAdJoin, profile.JoinMode);
            Assert.Equal("enrollment_facts_observed:v2", profile.Reason);
            Assert.Equal(0, profile.EvidenceOrdinal);
        }

        [Fact]
        public void EnrollmentFactsObserved_v1_leavesModeUnknown_recordsReason()
        {
            var signal = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 0,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsHybridJoin] = "true",
                });

            var profile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, signal);

            // v1 is ambiguous (Classic / SelfDeploying / WhiteGlove); later signals resolve it.
            Assert.Equal(EnrollmentMode.Unknown, profile.Mode);
            Assert.Equal(EnrollmentJoinMode.HybridAzureAdJoin, profile.JoinMode);
            Assert.Equal("enrollment_facts_observed:v1", profile.Reason);
        }

        [Fact]
        public void EnrollmentFactsObserved_emptyPayload_returnsSameInstance()
        {
            var signal = MakeSignal(DecisionSignalKind.EnrollmentFactsObserved, ordinal: 0, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Same(EnrollmentScenarioProfile.Empty, profile);
        }

        [Fact]
        public void EnrollmentFactsObserved_repeatPostWithSameFacts_isIdempotent()
        {
            var first = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 0,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsHybridJoin] = "false",
                });
            var second = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 7,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsHybridJoin] = "false",
                });

            var p1 = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, first);
            var p2 = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(p1, second);

            // Idempotent: second post is a no-op on the profile, including the EvidenceOrdinal —
            // the Unknown-guards short-circuit because every dimension is already set.
            Assert.Same(p1, p2);
            Assert.Equal(EnrollmentJoinMode.AzureAdJoin, p2.JoinMode);
            Assert.Equal(0, p2.EvidenceOrdinal);
        }

        // ------------------------------------------------ isSelfDeployingProfile seed
        // Session 320b3bf7 kiosk fix: CloudAssignedOobeConfig 0x20|0x40 marker seeds
        // Mode=SelfDeploying at High confidence so the IME AccountSetup false positive
        // can no longer flip the session to Classic and suppress the DeviceOnly path.

        [Fact]
        public void EnrollmentFactsObserved_selfDeployingProfile_setsSelfDeploying_highConfidence()
        {
            var signal = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 0,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsHybridJoin] = "false",
                    [SignalPayloadKeys.IsSelfDeployingProfile] = "true",
                });

            var profile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Equal(EnrollmentMode.SelfDeploying, profile.Mode);
            Assert.Equal(ProfileConfidence.High, profile.Confidence);
            Assert.Equal("oobe_config_self_deploying", profile.Reason);
            Assert.Equal(EnrollmentJoinMode.AzureAdJoin, profile.JoinMode);
        }

        [Theory]
        [InlineData("false")]
        [InlineData("not-a-bool")]
        public void EnrollmentFactsObserved_selfDeployingProfile_falseOrUnparseable_leavesModeUnknown(string raw)
        {
            var signal = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 0,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsSelfDeployingProfile] = raw,
                });

            var profile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Equal(EnrollmentMode.Unknown, profile.Mode);
            Assert.Equal("enrollment_facts_observed:v1", profile.Reason);
        }

        [Fact]
        public void EnrollmentFactsObserved_selfDeployingProfile_repeatPost_isIdempotent()
        {
            Dictionary<string, string> Payload() => new Dictionary<string, string>
            {
                [SignalPayloadKeys.EnrollmentType] = "v1",
                [SignalPayloadKeys.IsHybridJoin] = "false",
                [SignalPayloadKeys.IsSelfDeployingProfile] = "true",
            };

            var p1 = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty,
                MakeSignal(DecisionSignalKind.EnrollmentFactsObserved, ordinal: 0, payload: Payload()));
            var p2 = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                p1,
                MakeSignal(DecisionSignalKind.EnrollmentFactsObserved, ordinal: 9, payload: Payload()));

            // Every dimension already set → set-once guards short-circuit, same instance back.
            Assert.Same(p1, p2);
            Assert.Equal(0, p2.EvidenceOrdinal);
        }

        [Fact]
        public void EnrollmentFactsObserved_v2_beatsSelfDeployingProfile_inSamePayload()
        {
            // WDP (Windows Device Preparation) has its own completion path — an enrollmentType
            // "v2" in the same payload wins over the OobeConfig marker.
            var signal = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 0,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v2",
                    [SignalPayloadKeys.IsSelfDeployingProfile] = "true",
                });

            var profile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Equal(EnrollmentMode.DevicePreparation, profile.Mode);
        }

        [Theory]
        [InlineData(EnrollmentMode.Classic)]
        [InlineData(EnrollmentMode.WhiteGlove)]
        [InlineData(EnrollmentMode.DevicePreparation)]
        public void EnrollmentFactsObserved_selfDeployingProfile_doesNotOverrideExistingMode(EnrollmentMode existing)
        {
            var current = EnrollmentScenarioProfile.Empty.With(
                mode: existing, confidence: ProfileConfidence.Medium,
                reason: "preexisting", evidenceOrdinal: 3);
            var signal = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 5,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.IsSelfDeployingProfile] = "true",
                });

            var profile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(current, signal);

            Assert.Equal(existing, profile.Mode);
        }

        [Fact]
        public void EnrollmentFactsObserved_laterSignalCannotRegressJoinMode()
        {
            // Initial post sets HADJ.
            var firstSignal = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 0,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.IsHybridJoin] = "true",
                });
            var p1 = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                EnrollmentScenarioProfile.Empty, firstSignal);
            Assert.Equal(EnrollmentJoinMode.HybridAzureAdJoin, p1.JoinMode);

            // A later, flaky re-read claims AADJ — must NOT overwrite the already-known value.
            var flapped = MakeSignal(
                DecisionSignalKind.EnrollmentFactsObserved,
                ordinal: 12,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.IsHybridJoin] = "false",
                });
            var p2 = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(p1, flapped);

            Assert.Equal(EnrollmentJoinMode.HybridAzureAdJoin, p2.JoinMode);
        }

        // ================================================================== EspConfigDetected

        [Fact]
        public void EspConfigDetected_bothHalvesPresent_derivesEspConfig()
        {
            var signal = MakeSignal(
                DecisionSignalKind.EspConfigDetected,
                ordinal: 3,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                });

            var (profile, obs) = EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected(
                EnrollmentScenarioProfile.Empty, EnrollmentScenarioObservations.Empty, signal);

            Assert.Equal(EspConfig.DeviceEspOnly, profile.EspConfig);
            Assert.Equal(ProfileConfidence.Medium, profile.Confidence);
            Assert.True(obs.SkipUserEsp!.Value);
            Assert.False(obs.SkipDeviceEsp!.Value);
        }

        [Fact]
        public void EspConfigDetected_partialFirstSignal_profileStaysUnknown_observationFilled()
        {
            var signal = MakeSignal(
                DecisionSignalKind.EspConfigDetected,
                ordinal: 1,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                });

            var (profile, obs) = EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected(
                EnrollmentScenarioProfile.Empty, EnrollmentScenarioObservations.Empty, signal);

            // Profile.EspConfig cannot be derived without both halves.
            Assert.Equal(EspConfig.Unknown, profile.EspConfig);
            Assert.True(obs.SkipUserEsp!.Value);
            Assert.Null(obs.SkipDeviceEsp);
        }

        [Fact]
        public void EspConfigDetected_secondSignalCompletesPair_derivesEspConfig()
        {
            var first = MakeSignal(
                DecisionSignalKind.EspConfigDetected,
                ordinal: 1,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                });
            var second = MakeSignal(
                DecisionSignalKind.EspConfigDetected,
                ordinal: 2,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                });

            var (p1, o1) = EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected(
                EnrollmentScenarioProfile.Empty, EnrollmentScenarioObservations.Empty, first);
            var (p2, o2) = EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected(p1, o1, second);

            Assert.Equal(EspConfig.Unknown, p1.EspConfig);
            Assert.Equal(EspConfig.FullEsp, p2.EspConfig);
            Assert.Equal(1, o2.SkipUserEsp!.SourceSignalOrdinal);
            Assert.Equal(2, o2.SkipDeviceEsp!.SourceSignalOrdinal);
        }

        [Fact]
        public void EspConfigDetected_setOnce_laterSignalDoesNotRegressEspConfig()
        {
            var seed = MakeSignal(
                DecisionSignalKind.EspConfigDetected,
                ordinal: 3,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                });
            var flipped = MakeSignal(
                DecisionSignalKind.EspConfigDetected,
                ordinal: 9,
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    [SignalPayloadKeys.SkipDeviceEsp] = "true",
                });

            var (p1, o1) = EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected(
                EnrollmentScenarioProfile.Empty, EnrollmentScenarioObservations.Empty, seed);
            var (p2, _) = EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected(p1, o1, flipped);

            // Observations are set-once → Profile stays on the first derivation.
            Assert.Equal(EspConfig.FullEsp, p2.EspConfig);
        }

        [Theory]
        [InlineData(false, false, EspConfig.FullEsp)]
        [InlineData(true, false, EspConfig.DeviceEspOnly)]
        [InlineData(false, true, EspConfig.UserEspOnly)]
        [InlineData(true, true, EspConfig.NoEsp)]
        public void DeriveEspConfig_mapsAllFourCombinations(bool skipUser, bool skipDevice, EspConfig expected)
        {
            Assert.Equal(expected, EnrollmentScenarioProfileUpdater.DeriveEspConfig(skipUser, skipDevice));
        }

        // ================================================================== Mode promotions

        [Fact]
        public void AccountSetupObserved_modeUnknown_promotesToClassic_medium()
        {
            var signal = MakeSignal(DecisionSignalKind.EspPhaseChanged, ordinal: 5, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplyAccountSetupObserved(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Equal(EnrollmentMode.Classic, profile.Mode);
            Assert.Equal(ProfileConfidence.Medium, profile.Confidence);
            Assert.Equal("account_setup_observed", profile.Reason);
            Assert.Equal(5, profile.EvidenceOrdinal);
        }

        [Fact]
        public void AccountSetupObserved_modeAlreadySelfDeploying_isNoOp()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(
                mode: EnrollmentMode.SelfDeploying,
                confidence: ProfileConfidence.High);
            var signal = MakeSignal(DecisionSignalKind.EspPhaseChanged, ordinal: 5, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplyAccountSetupObserved(seed, signal);

            Assert.Same(seed, profile);
        }

        [Fact]
        public void ImeUserSessionCompleted_upgradesToClassicHigh()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(
                mode: EnrollmentMode.Classic,
                confidence: ProfileConfidence.Medium);
            var signal = MakeSignal(DecisionSignalKind.ImeUserSessionCompleted, ordinal: 10, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplyImeUserSessionCompleted(seed, signal);

            Assert.Equal(EnrollmentMode.Classic, profile.Mode);
            Assert.Equal(ProfileConfidence.High, profile.Confidence);
            Assert.Equal("ime_user_session_completed", profile.Reason);
        }

        [Fact]
        public void ImeUserSessionCompleted_alreadyHighDifferentMode_doesNotRegress()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(
                mode: EnrollmentMode.WhiteGlove,
                confidence: ProfileConfidence.High);
            var signal = MakeSignal(DecisionSignalKind.ImeUserSessionCompleted, ordinal: 10, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplyImeUserSessionCompleted(seed, signal);

            // Monotonic: Classic cannot overwrite a WhiteGlove classification at High confidence.
            Assert.Same(seed, profile);
        }

        // ================================================================== ApplySelfDeployingDeadlineConfirmed
        // Plan v9 — ApplyDeviceSetupProvisioningComplete was removed (signal-time Mode promotion
        // caused false-positive SelfDeploying terminations on Classic flows, session 88a53223).
        // ApplySelfDeployingDeadlineConfirmed is its replacement, invoked from the deadline-fired
        // handler after all guards have passed. Same monotonic semantics as ApplyImeUserSessionCompleted.

        [Fact]
        public void SelfDeployingDeadlineConfirmed_fromUnknown_upgradesToSelfDeployingHigh()
        {
            var signal = MakeSignal(DecisionSignalKind.DeadlineFired, ordinal: 6, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplySelfDeployingDeadlineConfirmed(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Equal(EnrollmentMode.SelfDeploying, profile.Mode);
            Assert.Equal(ProfileConfidence.High, profile.Confidence);
            Assert.Equal("selfdeploying_deadline_confirmed", profile.Reason);
        }

        [Fact]
        public void SelfDeployingDeadlineConfirmed_idempotent_whenAlreadySelfDeployingHigh()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(
                mode: EnrollmentMode.SelfDeploying,
                confidence: ProfileConfidence.High,
                reason: "selfdeploying_deadline_confirmed",
                evidenceOrdinal: 5);
            var signal = MakeSignal(DecisionSignalKind.DeadlineFired, ordinal: 6, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplySelfDeployingDeadlineConfirmed(seed, signal);

            Assert.Equal(EnrollmentMode.SelfDeploying, profile.Mode);
            Assert.Equal(ProfileConfidence.High, profile.Confidence);
            Assert.Equal("selfdeploying_deadline_confirmed", profile.Reason);
        }

        [Fact]
        public void SelfDeployingDeadlineConfirmed_monotonicBail_whenClassicAlreadyHigh()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(
                mode: EnrollmentMode.Classic,
                confidence: ProfileConfidence.High,
                reason: "ime_user_session_completed");
            var signal = MakeSignal(DecisionSignalKind.DeadlineFired, ordinal: 6, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplySelfDeployingDeadlineConfirmed(seed, signal);

            // Monotonic: SelfDeploying must not overwrite a Classic/High classification.
            Assert.Same(seed, profile);
        }

        [Fact]
        public void SelfDeployingDeadlineConfirmed_monotonicBail_whenWhiteGloveAlreadyHigh()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(
                mode: EnrollmentMode.WhiteGlove,
                confidence: ProfileConfidence.High,
                reason: "classifier_whiteglove_sealing_confirmed");
            var signal = MakeSignal(DecisionSignalKind.DeadlineFired, ordinal: 6, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplySelfDeployingDeadlineConfirmed(seed, signal);

            Assert.Same(seed, profile);
        }

        [Fact]
        public void WhiteGloveSealingConfirmed_setsWhiteGloveHigh_andTechnicianSide()
        {
            var signal = MakeSignal(DecisionSignalKind.ClassifierVerdictIssued, ordinal: 15, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplyWhiteGloveSealingConfirmed(
                EnrollmentScenarioProfile.Empty, signal);

            Assert.Equal(EnrollmentMode.WhiteGlove, profile.Mode);
            Assert.Equal(ProfileConfidence.High, profile.Confidence);
            Assert.Equal(PreProvisioningSide.Technician, profile.PreProvisioningSide);
            Assert.Equal("classifier_whiteglove_sealing_confirmed", profile.Reason);
        }

        // ================================================================== AadUserJoinedLate

        [Fact]
        public void AadUserJoinedLate_withUserTrue_recordsReason_butLeavesJoinModeUntouched()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(joinMode: EnrollmentJoinMode.AzureAdJoin);
            var signal = MakeSignal(DecisionSignalKind.AadUserJoinedLate, ordinal: 8, payload: null);

            var profile = EnrollmentScenarioProfileUpdater.ApplyAadUserJoinedLate(seed, signal, withUser: true);

            // Critical Codex correction: AadUserJoinedLate is NOT a JoinMode signal.
            Assert.Equal(EnrollmentJoinMode.AzureAdJoin, profile.JoinMode);
            Assert.Equal("late_aadj_observed:withUser=true", profile.Reason);
            Assert.Equal(8, profile.EvidenceOrdinal);
        }

        // ================================================================== helpers

        private static DecisionSignal MakeSignal(
            DecisionSignalKind kind,
            long ordinal,
            Dictionary<string, string>? payload)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: FixedUtc,
                sourceOrigin: "test",
                evidence: new Evidence(kind: EvidenceKind.Synthetic, identifier: "test", summary: "test"),
                payload: payload);
        }
    }
}
