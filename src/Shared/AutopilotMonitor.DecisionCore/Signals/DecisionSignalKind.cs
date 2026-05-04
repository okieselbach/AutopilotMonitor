namespace AutopilotMonitor.DecisionCore.Signals
{
    /// <summary>
    /// All signal kinds consumed by the Decision Engine.
    /// Plan §2.2. Every kind is versioned via <see cref="DecisionSignal.KindSchemaVersion"/>;
    /// reducer handlers dispatch on (Kind, SchemaVersion). New kinds or version bumps
    /// require a replay fixture in <c>tests/fixtures/signal-kinds/{kind}-v{n}.json</c>
    /// — missing fixture = merge block.
    /// </summary>
    public enum DecisionSignalKind
    {
        // --- Raw — Part 1 ---
        EspPhaseChanged,
        EspExiting,
        EspResumed,
        EspTerminalFailure,
        DesktopArrived,
        HelloResolved,
        ImeUserSessionCompleted,
        DeviceSetupProvisioningComplete,
        AppInstallCompleted,
        AppInstallFailed,
        WhiteGloveShellCoreSuccess,
        WhiteGloveSealingPatternDetected,
        AadUserJoinedLate,
        SystemRebootObserved,
        DeviceInfoCollected,
        AutopilotProfileRead,
        EspConfigDetected,

        // PR4 (882fef64 debrief) — Hello/WHfB policy fact. Carries
        // { "helloEnabled": "true|false", "policySource": "<csp|gpo|...>" }.
        // Updates DecisionState.HelloPolicyEnabled so the wait-cadence + downstream
        // observability (`hello_policy_detection_mismatch`) can read it. Does NOT
        // gate enrollment_complete — completion stays orthogonal to Hello policy.
        HelloPolicyDetected,

        // V2 race-fix (10c8e0bf debrief, 2026-04-26) — static enrollment facts read
        // from the Autopilot policy registry (EnrollmentRegistryDetector). Carries
        // { "enrollmentType": "v1|v2", "isHybridJoin": "true|false" }.
        // <para>
        // Replaces the profile-seeding side of <see cref="SessionStarted"/>, which had
        // a Stage-Wache that swallowed the update when the signal arrived after the
        // first non-anchor signal had already moved Stage off SessionStarted (the
        // V2-Tracker / Backend-register-session race).
        // </para>
        // <para>
        // Reducer guarantees: stage-agnostic (no <see cref="DecisionState.Stage"/>
        // restriction), idempotent (re-posting the same facts is a no-op on the
        // profile, only the EvidenceOrdinal advances), and monotonic (a later signal
        // with the registry default-fallback values cannot regress a value that was
        // already established as Known).
        // </para>
        EnrollmentFactsObserved,

        // --- Synthetic ---
        DeadlineFired,
        ClassifierVerdictIssued,

        // Codex follow-up #2 — posted by EffectRunner when a critical effect
        // (ScheduleDeadline / CancelDeadline) fails so the orchestrator's timer
        // infrastructure cannot enforce a just-decided safety-net deadline.
        // Carries payload { "reason": "<abortReason>", "failingEffect": "<EffectKind>" }.
        // The reducer's HandleEffectInfrastructureFailureV1 transitions the session
        // to Failed with SessionOutcome.EnrollmentFailed and emits enrollment_failed.
        EffectInfrastructureFailure,

        // --- Lifecycle ---
        SessionStarted,
        SessionAborted,

        // --- Admin-driven preemption (Plan §2.7 admin-action audit, V2 parity PR-B3) ---
        AdminPreemptionDetected,

        // --- Informational pass-through (Single-Rail refactor, plan §1.3) ---
        // Carries a full EnrollmentEvent payload through the reducer without mutating
        // DecisionState. The HandleInformationalEventV1 reducer case emits exactly one
        // EmitEventTimelineEntry effect with the payload 1:1, then yields the unchanged
        // state. Any peripheral collector / lifecycle source that needs to appear on the
        // Events timeline posts an InformationalEvent instead of calling TelemetryEventEmitter
        // directly — the engine remains the single ordering / replay source.
        //
        // Promotion path: if a sender later needs its event to drive a decision, replace
        // the InformationalEvent post with a specific kind (e.g. PlatformScriptCompleted)
        // and add a state-mutating reducer case. Emission shape and UI contract stay the
        // same because the effect parameters carry the same EnrollmentEvent fields.
        InformationalEvent,
    }
}
