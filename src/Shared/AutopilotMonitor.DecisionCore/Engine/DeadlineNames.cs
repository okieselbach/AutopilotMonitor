namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Canonical deadline identifiers. Plan §2.6 pflicht-deadlines.
    /// <para>
    /// Every decision-relevant timer in the engine uses one of these names. The
    /// <see cref="State.ActiveDeadline"/> record is keyed on <see cref="State.ActiveDeadline.Name"/>,
    /// so a re-scheduled deadline with the same name replaces the prior entry (see
    /// <c>DecisionStateBuilder.AddDeadline</c>). The name also appears in the
    /// <c>DeadlineFired</c> signal payload under <see cref="SignalPayloadKeys.Deadline"/>.
    /// </para>
    /// </summary>
    public static class DeadlineNames
    {
        /// <summary>Post-ESP-exit grace period for Hello resolution. Plan §2.7 (300 s).</summary>
        public const string HelloSafety = "hello_safety";

        /// <summary>Brief settle window after ESP exit before we emit completion.</summary>
        public const string EspSettle = "esp_settle";

        /// <summary>Detects "no ESP at all" for self-deploying / device-only paths.</summary>
        public const string DeviceOnlyEspDetection = "device_only_esp_detection";

        /// <summary>Safety net for device-only sessions that never arrive at a terminal signal.</summary>
        public const string DeviceOnlySafety = "device_only_safety";

        /// <summary>Secondary Hello wait window (separate from HelloSafety; plan §2.7).</summary>
        public const string HelloWait = "hello_wait";

        /// <summary>Periodic classifier-tick every 30 s — replaces legacy signal-correlated loop.</summary>
        public const string ClassifierTick = "classifier_tick";

        /// <summary>
        /// Brief grace window (~5 s) between both-prerequisites-resolved and Completed.
        /// Gives the reducer-emitted <c>phase_transition(FinalizingSetup)</c> and the terminal
        /// <c>enrollment_complete</c> effect time to reach the backend before
        /// <see cref="State.SessionStageExtensions.IsTerminal"/> fires and
        /// <c>EnrollmentTerminationHandler</c> drains the spool. Plan §5 Fix 6.
        /// </summary>
        public const string FinalizingGrace = "finalizing_grace";
    }

    /// <summary>
    /// Well-known payload keys on a <see cref="Signals.DecisionSignal"/>.
    /// </summary>
    public static class SignalPayloadKeys
    {
        /// <summary>On <c>DeadlineFired</c>: the deadline name from <see cref="DeadlineNames"/>.</summary>
        public const string Deadline = "deadline";

        /// <summary>On <c>EspPhaseChanged</c>: the raw phase name as observed by the collector.</summary>
        public const string EspPhase = "phase";

        /// <summary>On <c>HelloResolved</c>: outcome string (e.g. Success, Timeout, Skipped).</summary>
        public const string HelloOutcome = "outcome";

        /// <summary>On <c>AadUserJoinedLate</c>: user presence indicator ("true" / "false").</summary>
        public const string AadJoinedWithUser = "aadJoinedWithUser";

        /// <summary>On <c>ImeUserSessionCompleted</c>: matched pattern id.</summary>
        public const string ImePatternId = "patternId";

        /// <summary>On <c>EspConfigDetected</c>: "true" / "false"; missing → fact is not set.</summary>
        public const string SkipUserEsp = "skipUserEsp";

        /// <summary>On <c>EspConfigDetected</c>: "true" / "false"; missing → fact is not set.</summary>
        public const string SkipDeviceEsp = "skipDeviceEsp";

        /// <summary>
        /// On <c>EspConfigDetected</c>: integer minutes parsed from the FirstSync
        /// <c>SyncFailureTimeout</c> registry value (Intune ESP setting
        /// "Show error when installation takes longer than"). Missing → fact stays unset.
        /// Surfaced into <c>app_install_failed</c> messages on terminal ESP-Apps timeout and
        /// into the <c>enrollment_failed</c> audit payload.
        /// </summary>
        public const string EspSyncFailureTimeoutMinutes = "espSyncFailureTimeoutMinutes";

        /// <summary>
        /// On <c>EspConfigDetected</c>: "true" / "false" decoded from bit 4 of the FirstSync
        /// <c>BlockInStatusPage</c> bitmask (Intune ESP setting "Allow users to use device if
        /// installation error occurs"). When true the ESP failure screen presents a
        /// "Continue anyway" button — the user can dismiss the ESP and reach the desktop
        /// after the monitor already observed a terminal failure on the agent side.
        /// Missing → fact stays unset.
        /// </summary>
        public const string EspAllowContinueAnyway = "espAllowContinueAnyway";

        /// <summary>On <c>HelloPolicyDetected</c>: "true" / "false". PR4 (882fef64 debrief).</summary>
        public const string HelloEnabled = "helloEnabled";

        /// <summary>On <c>HelloPolicyDetected</c>: source string (e.g. "csp", "gpo", "registry"). Optional.</summary>
        public const string HelloPolicySource = "policySource";

        /// <summary>On <c>SessionStarted</c>: literal "v1" / "v2" from <c>EnrollmentRegistryDetector.DetectEnrollmentType()</c>.</summary>
        public const string EnrollmentType = "enrollmentType";

        /// <summary>On <c>SessionStarted</c>: "true" / "false" from <c>EnrollmentRegistryDetector.DetectHybridJoin()</c>.</summary>
        public const string IsHybridJoin = "isHybridJoin";

        // --- InformationalEvent payload (plan §1.3, single-rail refactor) ------------
        // Mirrors the EnrollmentEvent fields the reducer must reconstruct for the
        // EmitEventTimelineEntry effect. EventType / Source are mandatory; the rest are
        // optional. Missing Severity defaults to Info, missing ImmediateUpload defaults
        // to false. DataJson, when present, is a JSON object whose properties are merged
        // into the effect parameter dictionary as individual string entries.

        /// <summary>On <c>InformationalEvent</c>: mandatory. Becomes <c>EnrollmentEvent.EventType</c>.</summary>
        public const string EventType = "eventType";

        /// <summary>On <c>InformationalEvent</c>: mandatory. Becomes <c>EnrollmentEvent.Source</c>.</summary>
        public const string Source = "source";

        /// <summary>On <c>InformationalEvent</c>: optional. Becomes <c>EnrollmentEvent.Message</c>.</summary>
        public const string Message = "message";

        /// <summary>On <c>InformationalEvent</c>: optional. Enum-name string (e.g. "Info", "Warning", "Error"); unknown / missing → Info.</summary>
        public const string Severity = "severity";

        /// <summary>On <c>InformationalEvent</c>: optional. "true" / "false"; missing → false.</summary>
        public const string ImmediateUpload = "immediateUpload";

        /// <summary>
        /// On <c>InformationalEvent</c>: reserved for future JSON-blob expansion in the
        /// emitter (plan §1.3). Currently pass-through — the reducer forwards this key
        /// verbatim into the effect parameters, where it ends up as a flat string in
        /// <c>EnrollmentEvent.Data</c>. Senders should prefer flat payload keys directly;
        /// keeping the constant avoids churn if a later commit wires up JSON parsing.
        /// </summary>
        public const string DataJson = "dataJson";
    }
}
