namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Engine stage — what the engine is currently waiting on. Plan §2.3.
    /// Distinct from <c>EnrollmentPhase</c> (end-user reality) and from <c>Hypothesis</c> (engine guesses).
    /// <para>
    /// Placeholder stages will be extended as partial-class reducers (§2.5) come online in M3.
    /// This enum is extended additively; new values are forward-compatible via
    /// <c>UnknownFallbackEnumConverter</c> (§2.15 L.14).
    /// </para>
    /// </summary>
    public enum SessionStage
    {
        Unknown = 0,
        SessionStarted,

        // Classic (UserDriven-v1)
        AwaitingEspPhaseChange,
        EspDeviceSetup,
        EspAccountSetup,
        AwaitingHello,
        AwaitingDesktop,
        DesktopArrivedEspBlocking,

        // Finalizing: both Hello + Desktop have resolved. Grace window before the session
        // transitions to `Completed` so the emitted `enrollment_complete` + phase-declaration
        // events reach the backend before `EnrollmentTerminationHandler` tears everything
        // down. Non-terminal (IsTerminal()==false) — the FinalizingGrace deadline fires the
        // actual Completed transition.
        Finalizing,

        // SelfDeploying-v1 / Device-Only
        AwaitingDeviceSetupProvisioning,
        AwaitingDeviceOnlyEsp,

        // WhiteGlove Part 1
        WhiteGloveCandidate,
        WhiteGloveSealed,

        // Terminal
        Completed,
        Failed,
    }
}
