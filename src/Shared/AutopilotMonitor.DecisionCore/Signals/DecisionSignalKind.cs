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
        // Session 330f73f3 fix (2026-05-18) — strong "AccountSetup truly succeeded" fact.
        // Posted by ProvisioningStatusTracker once AccountSetupCategory.Status resolves to
        // categorySucceeded=true OR the fallback fires (all subcategories succeeded/notRequired
        // but Windows never set the boolean — analog to the existing DeviceSetup fallback).
        // Consumed by <c>ShouldTransitionToAwaitingHello</c>: entering AccountSetup is no longer
        // sufficient on its own, because Shell-Core event 62407 fires at every ESP-page
        // transition and the first occurrence (Device→Account handoff) is NOT the genuine
        // final exit.
        AccountSetupProvisioningComplete,
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

        // Session 772fe502 fix (2026-07-13) — genuine Hello-wizard launch observed via
        // Shell-Core event 62404 (CloudExperienceHost web-app started, CXID 'AADHello'/'NGC').
        // Posted by EspAndHelloTrackerAdapter (live + startup backfill). Records the set-once
        // DecisionState.HelloWizardStartedUtc fact. Two consumers:
        //   * Prevention — the hello-satisfied completion predicate stops treating
        //     HelloPolicyEnabled=false as Hello-satisfied once a wizard start is on record
        //     (a flip-flopping user-scoped CSP can read "disabled" while the wizard launches).
        //   * Cure — HandleHelloWizardStartedV1 un-skips an already-synthesized
        //     HelloOutcome="Skipped" resolution: cancels FinalizingGrace, returns to
        //     AwaitingHello and arms HelloSafety so a real HelloResolved (or its timeout)
        //     decides the session.
        HelloWizardStarted,

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

        // --- RealmJoin (RJ) deployment tracking ---
        // RealmJoin is a third-party deployment agent that installs additional software
        // after Autopilot reaches the desktop. When detected, the V2 engine keeps the
        // session non-terminal until DeploymentPhase reaches CompletedFirstDeployment (110)
        // or the 60-min hard timeout fires.
        RealmJoinDetected,
        RealmJoinResolved,
        RealmJoinTimeout,
        RealmJoinPackageStarted,
        RealmJoinPackageCompleted,
    }
}
