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
    public sealed record EnrollmentScenarioObservations
    {
        public static readonly EnrollmentScenarioObservations Empty = new EnrollmentScenarioObservations();

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveShellCoreSuccess"/> has fired.</summary>
        public SignalFact<bool>? ShellCoreWhiteGloveSuccessSeen { get; init; }

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveSealingPatternDetected"/> has fired.</summary>
        public SignalFact<bool>? WhiteGloveSealingPatternSeen { get; init; }

        /// <summary>
        /// Payload-carrying observation from <see cref="Signals.DecisionSignalKind.AadUserJoinedLate"/>.
        /// <c>true</c> = late AADJ observed with a user-side principal (hard-excluder for
        /// the WhiteGlove classifier); <c>false</c> = late AADJ observed but device-only.
        /// Independent of <see cref="EnrollmentJoinMode"/>, which reflects the
        /// <c>SessionStarted</c> registry hint.
        /// </summary>
        public SignalFact<bool>? AadUserJoinWithUserObserved { get; init; }

        /// <summary>Raw payload half-fact from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.</summary>
        public SignalFact<bool>? SkipUserEsp { get; init; }

        /// <summary>Raw payload half-fact from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.</summary>
        public SignalFact<bool>? SkipDeviceEsp { get; init; }

        /// <summary>
        /// FirstSync <c>SyncFailureTimeout</c> in minutes — Intune ESP setting
        /// "Show error when installation takes longer than" (default 60). Consumed by the
        /// terminal-ESP-Apps promotion path to enrich <c>app_install_failed</c> messages
        /// with the actual timeout instead of a generic "ESP timed out" string.
        /// Set-once from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.
        /// </summary>
        public SignalFact<int>? EspSyncFailureTimeoutMinutes { get; init; }

        /// <summary>
        /// Decoded bit 4 of the FirstSync <c>BlockInStatusPage</c> bitmask — Intune ESP
        /// setting "Allow users to use device if installation error occurs". When
        /// <c>true</c> the ESP failure screen shows a "Continue anyway" button; the
        /// <c>enrollment_failed</c> audit then carries a <c>mayHaveContinuedAnyway</c>
        /// hint because the agent's terminal-failure verdict does not preclude the user
        /// reaching the desktop. Set-once from
        /// <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.
        /// </summary>
        public SignalFact<bool>? EspAllowContinueAnyway { get; init; }

        /// <summary>
        /// Tenant-config opt-in from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>
        /// (payload <c>espContinueAnywayObservationEnabled</c>, stamped by the agent from
        /// <c>RemoteConfig.EnableEspContinueAnywayObservation</c> — an operator-set tenant
        /// setting, not an ESP registry fact). When <c>true</c> AND the ESP profile allows
        /// "Continue anyway", a Device-phase ESP terminal failure (AccountSetup never entered)
        /// is defanged into an observation advisory instead of failing the session immediately
        /// — see <c>HandleEspTerminalFailureV1</c>. Default (absent/false) keeps the immediate
        /// hard-fail semantics. Set-once.
        /// </summary>
        public SignalFact<bool>? EspContinueAnywayObservationEnabled { get; init; }

        /// <summary>
        /// Raw registry fact from <see cref="Signals.DecisionSignalKind.EnrollmentFactsObserved"/>
        /// (payload <c>isSelfDeployingProfile</c>, from <c>CloudAssignedOobeConfig</c> bits
        /// 0x20|0x40 — deterministic, validated platform-wide as exclusive to self-deploying /
        /// kiosk profiles with zero user-driven false positives). Unlike the positive-only
        /// <see cref="EnrollmentScenarioProfile"/> seed, this records BOTH values: an explicit
        /// <c>false</c> is a hard "this is NOT a self-deploying device" fact that vetoes the
        /// behavioural <c>device_only_esp_detection</c> deadline (session 62e603c9). <c>null</c>
        /// means the fact was never observed and must NOT be treated as a veto.
        /// </summary>
        public SignalFact<bool>? RegistrySelfDeployingProfile { get; init; }

        /// <summary>
        /// Raw marker fact from <see cref="Signals.DecisionSignalKind.EnrollmentFactsObserved"/>
        /// (payload <c>isCloudPc</c>, from the Windows365 registry key + installed
        /// CloudManagedDesktopExtension service — the marker-AND field-verified by bootstrap
        /// v2.3-dev.2). <c>true</c> means the session runs on a Windows 365 Cloud PC whose
        /// Device-ESP already completed headless at provisioning time: no DeviceSetup phase
        /// signals are expected, monitoring effectively starts at Account Setup. Records BOTH
        /// values; <c>null</c> means the fact was never observed.
        /// </summary>
        public SignalFact<bool>? CloudPc { get; init; }

        public EnrollmentScenarioObservations WithShellCoreWhiteGloveSuccessSeen(long sourceSignalOrdinal) =>
            ShellCoreWhiteGloveSuccessSeen != null
                ? this
                : this with { ShellCoreWhiteGloveSuccessSeen = new SignalFact<bool>(true, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithWhiteGloveSealingPatternSeen(long sourceSignalOrdinal) =>
            WhiteGloveSealingPatternSeen != null
                ? this
                : this with { WhiteGloveSealingPatternSeen = new SignalFact<bool>(true, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithAadUserJoinWithUserObserved(bool value, long sourceSignalOrdinal) =>
            AadUserJoinWithUserObserved != null
                ? this
                : this with { AadUserJoinWithUserObserved = new SignalFact<bool>(value, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithSkipUserEsp(bool value, long sourceSignalOrdinal) =>
            SkipUserEsp != null
                ? this
                : this with { SkipUserEsp = new SignalFact<bool>(value, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithSkipDeviceEsp(bool value, long sourceSignalOrdinal) =>
            SkipDeviceEsp != null
                ? this
                : this with { SkipDeviceEsp = new SignalFact<bool>(value, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithEspSyncFailureTimeoutMinutes(int value, long sourceSignalOrdinal) =>
            EspSyncFailureTimeoutMinutes != null
                ? this
                : this with { EspSyncFailureTimeoutMinutes = new SignalFact<int>(value, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithEspAllowContinueAnyway(bool value, long sourceSignalOrdinal) =>
            EspAllowContinueAnyway != null
                ? this
                : this with { EspAllowContinueAnyway = new SignalFact<bool>(value, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithEspContinueAnywayObservationEnabled(bool value, long sourceSignalOrdinal) =>
            EspContinueAnywayObservationEnabled != null
                ? this
                : this with { EspContinueAnywayObservationEnabled = new SignalFact<bool>(value, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithRegistrySelfDeployingProfile(bool value, long sourceSignalOrdinal) =>
            RegistrySelfDeployingProfile != null
                ? this
                : this with { RegistrySelfDeployingProfile = new SignalFact<bool>(value, sourceSignalOrdinal) };

        public EnrollmentScenarioObservations WithCloudPc(bool value, long sourceSignalOrdinal) =>
            CloudPc != null
                ? this
                : this with { CloudPc = new SignalFact<bool>(value, sourceSignalOrdinal) };
    }
}
