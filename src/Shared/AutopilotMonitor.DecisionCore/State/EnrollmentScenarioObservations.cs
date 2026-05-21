namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Raw per-signal observations that feed the WhiteGlove sealing classifier and downstream
    /// guards. Codex follow-up #5 — replaces the legacy per-flag <see cref="SignalFact{T}"/>
    /// fields (<c>ShellCoreWhiteGloveSuccessSeen</c>, <c>WhiteGloveSealingPatternSeen</c>,
    /// <c>AadJoinedWithUser</c>, <c>SkipUserEsp</c>, <c>SkipDeviceEsp</c>) with a single aggregate.
    /// These are **evidence**, not classification — the derived classification lives in
    /// <see cref="EnrollmentScenarioProfile"/>.
    /// <para>
    /// <b>Invariants</b>:
    /// <list type="bullet">
    ///   <item>Immutable; the <c>With…</c> methods return new instances.</item>
    ///   <item>Set-once semantics for Boolean flags: once observed, later identical signals are
    ///         no-ops (the first-sighting ordinal is preserved as evidence).</item>
    ///   <item><see cref="AadUserJoinWithUserObserved"/> is the late-AADJ user-presence flag
    ///         (payload <c>aadJoinedWithUser</c>) — NOT the <see cref="EnrollmentJoinMode"/>.
    ///         See <see cref="EnrollmentJoinMode"/> remarks.</item>
    ///   <item><see cref="SkipUserEsp"/> / <see cref="SkipDeviceEsp"/> are the raw half-facts
    ///         from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>. The derived
    ///         <see cref="EnrollmentScenarioProfile.EspConfig"/> is only set when BOTH halves
    ///         are observed (signals can arrive partial — first skipUser-only, later skipDevice).</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class EnrollmentScenarioObservations
    {
        public static readonly EnrollmentScenarioObservations Empty = new EnrollmentScenarioObservations(
            shellCoreWhiteGloveSuccessSeen: null,
            whiteGloveSealingPatternSeen: null,
            aadUserJoinWithUserObserved: null,
            skipUserEsp: null,
            skipDeviceEsp: null,
            espSyncFailureTimeoutMinutes: null,
            espAllowContinueAnyway: null);

        public EnrollmentScenarioObservations(
            SignalFact<bool>? shellCoreWhiteGloveSuccessSeen,
            SignalFact<bool>? whiteGloveSealingPatternSeen,
            SignalFact<bool>? aadUserJoinWithUserObserved,
            SignalFact<bool>? skipUserEsp,
            SignalFact<bool>? skipDeviceEsp,
            SignalFact<int>? espSyncFailureTimeoutMinutes,
            SignalFact<bool>? espAllowContinueAnyway)
        {
            ShellCoreWhiteGloveSuccessSeen = shellCoreWhiteGloveSuccessSeen;
            WhiteGloveSealingPatternSeen = whiteGloveSealingPatternSeen;
            AadUserJoinWithUserObserved = aadUserJoinWithUserObserved;
            SkipUserEsp = skipUserEsp;
            SkipDeviceEsp = skipDeviceEsp;
            EspSyncFailureTimeoutMinutes = espSyncFailureTimeoutMinutes;
            EspAllowContinueAnyway = espAllowContinueAnyway;
        }

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveShellCoreSuccess"/> has fired.</summary>
        public SignalFact<bool>? ShellCoreWhiteGloveSuccessSeen { get; }

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveSealingPatternDetected"/> has fired.</summary>
        public SignalFact<bool>? WhiteGloveSealingPatternSeen { get; }

        /// <summary>
        /// Payload-carrying observation from <see cref="Signals.DecisionSignalKind.AadUserJoinedLate"/>.
        /// <c>true</c> = late AADJ observed with a user-side principal (hard-excluder for
        /// the WhiteGlove classifier); <c>false</c> = late AADJ observed but device-only.
        /// Independent of <see cref="EnrollmentJoinMode"/>, which reflects the
        /// <c>SessionStarted</c> registry hint.
        /// </summary>
        public SignalFact<bool>? AadUserJoinWithUserObserved { get; }

        /// <summary>Raw payload half-fact from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.</summary>
        public SignalFact<bool>? SkipUserEsp { get; }

        /// <summary>Raw payload half-fact from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.</summary>
        public SignalFact<bool>? SkipDeviceEsp { get; }

        /// <summary>
        /// FirstSync <c>SyncFailureTimeout</c> in minutes — Intune ESP setting
        /// "Show error when installation takes longer than" (default 60). Consumed by the
        /// terminal-ESP-Apps promotion path to enrich <c>app_install_failed</c> messages
        /// with the actual timeout instead of a generic "ESP timed out" string.
        /// Set-once from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.
        /// </summary>
        public SignalFact<int>? EspSyncFailureTimeoutMinutes { get; }

        /// <summary>
        /// Decoded bit 4 of the FirstSync <c>BlockInStatusPage</c> bitmask — Intune ESP
        /// setting "Allow users to use device if installation error occurs". When
        /// <c>true</c> the ESP failure screen shows a "Continue anyway" button; the
        /// <c>enrollment_failed</c> audit then carries a <c>mayHaveContinuedAnyway</c>
        /// hint because the agent's terminal-failure verdict does not preclude the user
        /// reaching the desktop. Set-once from
        /// <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.
        /// </summary>
        public SignalFact<bool>? EspAllowContinueAnyway { get; }

        public EnrollmentScenarioObservations WithShellCoreWhiteGloveSuccessSeen(long sourceSignalOrdinal) =>
            ShellCoreWhiteGloveSuccessSeen != null
                ? this
                : new EnrollmentScenarioObservations(
                    new SignalFact<bool>(true, sourceSignalOrdinal),
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    SkipDeviceEsp,
                    EspSyncFailureTimeoutMinutes,
                    EspAllowContinueAnyway);

        public EnrollmentScenarioObservations WithWhiteGloveSealingPatternSeen(long sourceSignalOrdinal) =>
            WhiteGloveSealingPatternSeen != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    new SignalFact<bool>(true, sourceSignalOrdinal),
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    SkipDeviceEsp,
                    EspSyncFailureTimeoutMinutes,
                    EspAllowContinueAnyway);

        public EnrollmentScenarioObservations WithAadUserJoinWithUserObserved(bool value, long sourceSignalOrdinal) =>
            AadUserJoinWithUserObserved != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    new SignalFact<bool>(value, sourceSignalOrdinal),
                    SkipUserEsp,
                    SkipDeviceEsp,
                    EspSyncFailureTimeoutMinutes,
                    EspAllowContinueAnyway);

        public EnrollmentScenarioObservations WithSkipUserEsp(bool value, long sourceSignalOrdinal) =>
            SkipUserEsp != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    new SignalFact<bool>(value, sourceSignalOrdinal),
                    SkipDeviceEsp,
                    EspSyncFailureTimeoutMinutes,
                    EspAllowContinueAnyway);

        public EnrollmentScenarioObservations WithSkipDeviceEsp(bool value, long sourceSignalOrdinal) =>
            SkipDeviceEsp != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    new SignalFact<bool>(value, sourceSignalOrdinal),
                    EspSyncFailureTimeoutMinutes,
                    EspAllowContinueAnyway);

        public EnrollmentScenarioObservations WithEspSyncFailureTimeoutMinutes(int value, long sourceSignalOrdinal) =>
            EspSyncFailureTimeoutMinutes != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    SkipDeviceEsp,
                    new SignalFact<int>(value, sourceSignalOrdinal),
                    EspAllowContinueAnyway);

        public EnrollmentScenarioObservations WithEspAllowContinueAnyway(bool value, long sourceSignalOrdinal) =>
            EspAllowContinueAnyway != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    SkipDeviceEsp,
                    EspSyncFailureTimeoutMinutes,
                    new SignalFact<bool>(value, sourceSignalOrdinal));
    }
}
