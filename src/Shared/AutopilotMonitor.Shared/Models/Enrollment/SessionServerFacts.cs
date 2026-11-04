namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// What the backend established about a registering device, kept apart from what the agent
    /// reported in <see cref="SessionRegistration"/>: the session store takes both, so no server fact
    /// can ride on the agent's request body.
    /// </summary>
    public sealed class SessionServerFacts
    {
        /// <summary>The validator that admitted the request; Unknown when device validation is off.</summary>
        public ValidatorType ValidatedBy { get; set; } = ValidatorType.Unknown;

        /// <summary>
        /// The Autopilot registration the Autopilot validator read; null whenever another path (or none)
        /// admitted the device. A re-registration without it keeps the values the row already holds.
        /// </summary>
        public AutopilotRegistration? AutopilotRegistration { get; set; }
    }
}
