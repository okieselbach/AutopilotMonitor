namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Terminal outcome of a session. Plan §2.3.
    /// Non-null only when <see cref="SessionStage"/> is a terminal stage
    /// (<c>Completed</c>, <c>Failed</c>, or <c>WhiteGloveSealed</c>).
    /// </summary>
    public enum SessionOutcome
    {
        Unknown = 0,
        EnrollmentComplete,
        EnrollmentFailed,
        WhiteGlovePart1Sealed,
        Aborted,

        // V2 parity PR-B3 / plan §2.7 admin-action audit:
        // Set when the register-session response carries an AdminAction
        // (operator marked the session terminal via the portal before the
        // agent even started). Stage transitions to Completed or Failed
        // depending on the adminOutcome payload.
        AdminPreempted,
    }
}
