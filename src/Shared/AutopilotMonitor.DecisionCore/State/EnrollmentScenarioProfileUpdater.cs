using System;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Pure static mutator that maps a <see cref="DecisionSignal"/> to a new
    /// <see cref="EnrollmentScenarioProfile"/>. Codex follow-up #5 — keeps the signal →
    /// profile rules out of the reducer handlers so they can be unit-tested in isolation
    /// from the full reducer pipeline.
    /// <para>
    /// <b>Monotonic confidence</b>: these helpers never regress <see cref="EnrollmentScenarioProfile.Confidence"/>
    /// or <see cref="EnrollmentScenarioProfile.Mode"/>. A new signal can only raise confidence
    /// or swap mode when the current classification is <see cref="EnrollmentMode.Unknown"/> /
    /// below the incoming strength. That keeps replay deterministic even if signals arrive
    /// out of order during recovery.
    /// </para>
    /// </summary>
    public static class EnrollmentScenarioProfileUpdater
    {
        // ================================================================== EnrollmentFactsObserved

        /// <summary>
        /// Seed the profile from a <see cref="DecisionSignalKind.EnrollmentFactsObserved"/>
        /// signal. Payload keys: <see cref="SignalPayloadKeys.EnrollmentType"/> (literal
        /// "v1" / "v2" from <c>EnrollmentRegistryDetector.DetectEnrollmentType</c>),
        /// <see cref="SignalPayloadKeys.IsHybridJoin"/> (boolean from
        /// <c>EnrollmentRegistryDetector.DetectHybridJoin</c>).
        /// <para>
        /// V2 race-fix (10c8e0bf debrief, 2026-04-26) — replaces the profile-seeding side
        /// of <see cref="ApplySessionStarted"/>, which lived inside the Stage-anchored
        /// <c>HandleSessionStartedV1</c> handler and was therefore lost whenever the
        /// SessionStarted signal arrived after the first non-anchor signal moved Stage
        /// off SessionStarted. This handler is stage-agnostic on the reducer side, so
        /// the race vanishes structurally.
        /// </para>
        /// <para>
        /// <b>Idempotency</b>: a repeat signal with identical facts produces no profile
        /// change (returns <paramref name="current"/> unchanged). <b>Monotonicity</b>:
        /// once <see cref="EnrollmentScenarioProfile.JoinMode"/> is non-<see cref="EnrollmentJoinMode.Unknown"/>,
        /// a subsequent signal cannot overwrite it — protects against a flaky registry
        /// re-read returning the default <c>false</c> after the real value was learned
        /// (mirrors the set-once semantics already used by <see cref="ApplyEspConfigDetected"/>).
        /// </para>
        /// </summary>
        public static EnrollmentScenarioProfile ApplyEnrollmentFactsObserved(
            EnrollmentScenarioProfile current,
            DecisionSignal signal)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            var mode = current.Mode;
            var joinMode = current.JoinMode;
            var confidence = current.Confidence;
            var reason = current.Reason;
            var changed = false;

            if (signal.Payload != null)
            {
                if (signal.Payload.TryGetValue(SignalPayloadKeys.EnrollmentType, out var rawType)
                    && !string.IsNullOrEmpty(rawType)
                    && current.Mode == EnrollmentMode.Unknown)
                {
                    // Registry detector distinguishes v1 from v2 only. v1 stays Unknown here —
                    // the finer UserDriven / SelfDeploying / WhiteGlove classification comes
                    // from later signals (AccountSetup phase, ImeUserSessionCompleted,
                    // DeviceSetupProvisioningComplete, classifier verdicts).
                    //
                    // Set-once on the reason annotation: the v1 branch leaves Mode at Unknown,
                    // so without this guard a repeat post would re-trigger `changed=true` purely
                    // through the reason slot and break the idempotency contract documented on
                    // the public API. Mirroring the v2 branch's natural set-once (Mode promotion
                    // gate) keeps both branches structurally identical.
                    var newReason = string.Equals(rawType, "v2", StringComparison.OrdinalIgnoreCase)
                        ? "enrollment_facts_observed:v2"
                        : $"enrollment_facts_observed:{rawType}";

                    if (string.Equals(rawType, "v2", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = EnrollmentMode.DevicePreparation;
                        confidence = Max(confidence, ProfileConfidence.Medium);
                        reason = newReason;
                        changed = true;
                    }
                    else if (!string.Equals(current.Reason, newReason, StringComparison.Ordinal))
                    {
                        // v1 (or any other): keep Mode=Unknown but record the reason once.
                        reason = newReason;
                        changed = true;
                    }
                }

                if (signal.Payload.TryGetValue(SignalPayloadKeys.IsHybridJoin, out var rawHybrid)
                    && bool.TryParse(rawHybrid, out var isHybrid)
                    && current.JoinMode == EnrollmentJoinMode.Unknown)
                {
                    joinMode = isHybrid ? EnrollmentJoinMode.HybridAzureAdJoin : EnrollmentJoinMode.AzureAdJoin;
                    changed = true;
                }
            }

            if (!changed) return current;

            return current.With(
                mode: mode,
                joinMode: joinMode,
                confidence: confidence,
                reason: reason,
                evidenceOrdinal: signal.SessionSignalOrdinal);
        }

        // ================================================================== EspConfigDetected

        /// <summary>
        /// Apply the <c>skipUserEsp</c> / <c>skipDeviceEsp</c> payload on
        /// <see cref="DecisionSignalKind.EspConfigDetected"/> to the observations +
        /// derived profile. Returns both new values as a tuple — the signal can arrive
        /// **partially** (e.g. first call with <c>skipUser</c> only, second with
        /// <c>skipDevice</c>), so the updater fills whichever half is present + parseable
        /// and only derives <see cref="EnrollmentScenarioProfile.EspConfig"/> once BOTH
        /// halves are known.
        /// <para>
        /// Set-once: existing <see cref="EnrollmentScenarioObservations.SkipUserEsp"/> /
        /// <see cref="EnrollmentScenarioObservations.SkipDeviceEsp"/> facts are never
        /// overwritten by a later signal with the same (or different) payload.
        /// </para>
        /// </summary>
        public static (EnrollmentScenarioProfile profile, EnrollmentScenarioObservations observations) ApplyEspConfigDetected(
            EnrollmentScenarioProfile currentProfile,
            EnrollmentScenarioObservations currentObservations,
            DecisionSignal signal)
        {
            if (currentProfile == null) throw new ArgumentNullException(nameof(currentProfile));
            if (currentObservations == null) throw new ArgumentNullException(nameof(currentObservations));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            var newObservations = currentObservations;
            if (signal.Payload != null)
            {
                if (signal.Payload.TryGetValue(SignalPayloadKeys.SkipUserEsp, out var rawUser)
                    && bool.TryParse(rawUser, out var skipUser))
                {
                    newObservations = newObservations.WithSkipUserEsp(skipUser, signal.SessionSignalOrdinal);
                }
                if (signal.Payload.TryGetValue(SignalPayloadKeys.SkipDeviceEsp, out var rawDevice)
                    && bool.TryParse(rawDevice, out var skipDevice))
                {
                    newObservations = newObservations.WithSkipDeviceEsp(skipDevice, signal.SessionSignalOrdinal);
                }
                if (signal.Payload.TryGetValue(SignalPayloadKeys.EspSyncFailureTimeoutMinutes, out var rawTimeout)
                    && int.TryParse(rawTimeout, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timeoutMinutes)
                    && timeoutMinutes > 0)
                {
                    newObservations = newObservations.WithEspSyncFailureTimeoutMinutes(timeoutMinutes, signal.SessionSignalOrdinal);
                }
                if (signal.Payload.TryGetValue(SignalPayloadKeys.EspAllowContinueAnyway, out var rawContinue)
                    && bool.TryParse(rawContinue, out var allowContinue))
                {
                    newObservations = newObservations.WithEspAllowContinueAnyway(allowContinue, signal.SessionSignalOrdinal);
                }
            }

            var newProfile = currentProfile;
            if (currentProfile.EspConfig == EspConfig.Unknown
                && newObservations.SkipUserEsp != null
                && newObservations.SkipDeviceEsp != null)
            {
                var derived = DeriveEspConfig(
                    newObservations.SkipUserEsp.Value,
                    newObservations.SkipDeviceEsp.Value);

                newProfile = currentProfile.With(
                    espConfig: derived,
                    confidence: Max(currentProfile.Confidence, ProfileConfidence.Medium),
                    reason: $"esp_config_detected:{derived}",
                    evidenceOrdinal: signal.SessionSignalOrdinal);
            }

            return (newProfile, newObservations);
        }

        internal static EspConfig DeriveEspConfig(bool skipUser, bool skipDevice) =>
            (skipUser, skipDevice) switch
            {
                (false, false) => EspConfig.FullEsp,
                (true, false) => EspConfig.DeviceEspOnly,
                (false, true) => EspConfig.UserEspOnly,
                (true, true) => EspConfig.NoEsp,
            };

        // ================================================================== EspPhaseChanged

        /// <summary>
        /// Observing <see cref="Shared.Models.EnrollmentPhase.AccountSetup"/> for the first time
        /// is the canonical Classic UserDriven-v1 tell (mirrors the legacy
        /// <c>account_setup_observed</c> hypothesis update). Only promotes Mode when it is still
        /// <see cref="EnrollmentMode.Unknown"/> — a WhiteGlove or SelfDeploying classification
        /// from an earlier signal wins.
        /// </summary>
        public static EnrollmentScenarioProfile ApplyAccountSetupObserved(
            EnrollmentScenarioProfile current,
            DecisionSignal signal)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            if (current.Mode != EnrollmentMode.Unknown) return current;

            return current.With(
                mode: EnrollmentMode.Classic,
                confidence: Max(current.Confidence, ProfileConfidence.Medium),
                reason: "account_setup_observed",
                evidenceOrdinal: signal.SessionSignalOrdinal);
        }

        // ================================================================== ImeUserSessionCompleted

        /// <summary>
        /// <see cref="DecisionSignalKind.ImeUserSessionCompleted"/> is a strong UserDriven-v1
        /// indicator. Upgrades Mode to <see cref="EnrollmentMode.Classic"/> at
        /// <see cref="ProfileConfidence.High"/>, UNLESS a prior signal has already confirmed a
        /// different mode at High confidence (monotonic rule).
        /// </summary>
        public static EnrollmentScenarioProfile ApplyImeUserSessionCompleted(
            EnrollmentScenarioProfile current,
            DecisionSignal signal)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            if (current.Confidence == ProfileConfidence.High && current.Mode != EnrollmentMode.Classic
                && current.Mode != EnrollmentMode.Unknown)
            {
                return current;
            }

            return current.With(
                mode: EnrollmentMode.Classic,
                confidence: ProfileConfidence.High,
                reason: "ime_user_session_completed",
                evidenceOrdinal: signal.SessionSignalOrdinal);
        }

        // ================================================================== DeviceSetupProvisioningComplete

        /// <summary>
        /// <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/> without a preceding
        /// AccountSetup is a strong SelfDeploying-v1 indicator (caller must guard that invariant).
        /// Upgrades Mode to <see cref="EnrollmentMode.SelfDeploying"/> at
        /// <see cref="ProfileConfidence.High"/>.
        /// </summary>
        public static EnrollmentScenarioProfile ApplyDeviceSetupProvisioningComplete(
            EnrollmentScenarioProfile current,
            DecisionSignal signal)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            if (current.Confidence == ProfileConfidence.High && current.Mode != EnrollmentMode.SelfDeploying
                && current.Mode != EnrollmentMode.Unknown)
            {
                return current;
            }

            return current.With(
                mode: EnrollmentMode.SelfDeploying,
                confidence: ProfileConfidence.High,
                reason: "selfdeploying_provisioning_complete",
                evidenceOrdinal: signal.SessionSignalOrdinal);
        }

        // ================================================================== WhiteGlove verdict

        /// <summary>
        /// When the WhiteGlove sealing classifier issues a <see cref="HypothesisLevel.Confirmed"/>
        /// verdict, the profile mirrors the decision: Mode=<see cref="EnrollmentMode.WhiteGlove"/>
        /// at <see cref="ProfileConfidence.High"/>, PreProvisioningSide=<see cref="PreProvisioningSide.Technician"/>
        /// (Part 1 is by definition the technician side). Non-Confirmed verdicts leave the profile
        /// alone — the verdict itself is stored in <see cref="ClassifierOutcomes.WhiteGloveSealing"/>.
        /// </summary>
        public static EnrollmentScenarioProfile ApplyWhiteGloveSealingConfirmed(
            EnrollmentScenarioProfile current,
            DecisionSignal signal)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            return current.With(
                mode: EnrollmentMode.WhiteGlove,
                preProvisioningSide: PreProvisioningSide.Technician,
                confidence: ProfileConfidence.High,
                reason: "classifier_whiteglove_sealing_confirmed",
                evidenceOrdinal: signal.SessionSignalOrdinal);
        }

        // ================================================================== AadUserJoinedLate

        /// <summary>
        /// <see cref="DecisionSignalKind.AadUserJoinedLate"/> is a classifier-state update only
        /// (see <c>feedback_aad_joined_late_not_completion</c>). It does NOT touch
        /// <see cref="EnrollmentScenarioProfile.JoinMode"/> — that comes from
        /// <see cref="DecisionSignalKind.EnrollmentFactsObserved"/> <c>isHybridJoin</c>
        /// payload (V2 race-fix, 10c8e0bf debrief 2026-04-26). The payload-carrying
        /// presence flag is stored in <see cref="EnrollmentScenarioObservations.AadUserJoinWithUserObserved"/>;
        /// the profile only records the reason annotation.
        /// </summary>
        public static EnrollmentScenarioProfile ApplyAadUserJoinedLate(
            EnrollmentScenarioProfile current,
            DecisionSignal signal,
            bool withUser)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            return current.With(
                reason: $"late_aadj_observed:withUser={(withUser ? "true" : "false")}",
                evidenceOrdinal: signal.SessionSignalOrdinal);
        }

        // ================================================================== helpers

        internal static ProfileConfidence Max(ProfileConfidence a, ProfileConfidence b) =>
            a >= b ? a : b;
    }
}
