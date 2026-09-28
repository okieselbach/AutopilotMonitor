using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// What the agent recorded locally each time a process start ended without a registered
    /// session (register-session failed after all retries, the agent exited). Carried on the
    /// emergency-break report (<see cref="AgentErrorReport.PriorRegistrationFailure"/>): for a
    /// session that never registered it is the only account of why that ever reaches the
    /// backend. Cleared by the next successful registration.
    /// </summary>
    public class RegistrationFailureSummary
    {
        /// <summary>Process starts whose registration failed since the last success.</summary>
        public int FailedRuns { get; set; }

        /// <summary>UTC time the first of these runs gave up.</summary>
        public DateTime FirstFailedAtUtc { get; set; }

        /// <summary>UTC time the latest of these runs gave up.</summary>
        public DateTime LastFailedAtUtc { get; set; }

        /// <summary>Registration outcome of the latest failed run (e.g. <c>Failed</c>, <c>AuthFailed</c>).</summary>
        public string Outcome { get; set; } = default!;

        /// <summary>Last error message of the latest failed run, truncated by the agent.</summary>
        public string? LastError { get; set; }

        /// <summary>Network link up (any interface) when the latest failed run started registering.</summary>
        public bool? NetworkLinkUpAtStart { get; set; }

        /// <summary>Network link up when the latest failed run gave up.</summary>
        public bool? NetworkLinkUpAtEnd { get; set; }

        /// <summary>
        /// Wall-clock seconds the latest failed run spent registering (link wait plus all attempts).
        /// Far above the retry schedule means the device slept or was suspended meanwhile.
        /// </summary>
        public double AttemptWindowSeconds { get; set; }

        /// <summary>Outcome of the remote-config fetch in the latest failed run (e.g. <c>Succeeded</c>, <c>UsedDefaults</c>).</summary>
        public string? ConfigFetchOutcome { get; set; }
    }
}
