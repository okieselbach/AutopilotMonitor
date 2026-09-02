using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Defines a regex pattern for IME log parsing.
    /// Delivered from backend via agent config endpoint.
    /// Allows updating patterns without agent rebuild when Microsoft changes IME log formats.
    /// </summary>
    public class ImeLogPattern
    {
        /// <summary>
        /// Unique pattern identifier (e.g., "IME-ESP-PHASE")
        /// </summary>
        public string PatternId { get; set; } = default!;

        /// <summary>
        /// Pattern category controlling when the pattern is active:
        /// - "always": Always active regardless of ESP phase
        /// - "currentPhase": Only active during the current ESP phase
        /// - "otherPhases": Only active during non-current ESP phases (for history/completed apps)
        /// </summary>
        public string Category { get; set; } = default!;

        /// <summary>
        /// Regex pattern string to match against IME log message content.
        /// Supports named capture groups (e.g., (?&lt;id&gt;...)) which are passed to the action handler.
        /// Uses {GUID} as placeholder for the standard GUID capture pattern.
        /// </summary>
        public string Pattern { get; set; } = default!;

        /// <summary>
        /// Action to perform when the pattern matches:
        /// - "setCurrentApp": Set the current app being processed (uses 'id' capture group)
        /// - "updateStateInstalled": Mark app as installed
        /// - "updateStateDownloading": Mark app as downloading (uses 'bytes'/'ofbytes' for progress)
        /// - "updateStateInstalling": Mark app as installing
        /// - "updateStateSkipped": Mark app as skipped/not applicable
        /// - "updateStateError": Mark app as errored
        /// - "updateStatePostponed": Mark app as postponed (e.g., timeout)
        /// - "espPhaseDetected": ESP phase transition detected (uses 'espPhase' capture group)
        /// - "imeStarted": IME agent started
        /// - "policiesDiscovered": App policies JSON discovered (uses 'policies' capture group)
        /// - "ignoreCompletedApp": Add current app to ignore list (already completed in prior phase)
        /// - "imeAgentVersion": IME version detected (uses 'agentVersion' capture group)
        /// - "espTrackStatus": ESP tracked install status update (uses 'from'/'to'/'id' capture groups)
        /// - "updateName": Update app name (uses 'id'/'name' capture groups)
        /// - "updateWin32AppState": Update from Win32 app state (uses 'id'/'state' capture groups)
        /// - "cancelStuckAndSetCurrent": Cancel stuck app and set new current (uses 'id' capture group)
        /// </summary>
        public string Action { get; set; } = default!;

        /// <summary>
        /// Optional extra parameters for the action handler.
        /// Examples:
        /// - { "phase": "AccountSetup" } for espPhaseDetected to force a specific phase
        /// - { "useCurrentApp": "true" } to use CurrentPackageId instead of captured 'id'
        /// - { "checkTo": "true" } to check the 'to' capture group value before applying state
        /// </summary>
        public Dictionary<string, string> Parameters { get; set; } = default!;

        /// <summary>
        /// Whether this pattern is enabled. Allows disabling patterns without removing them.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Human-readable description of what this pattern detects and why.
        /// Not used by the agent — purely for documentation and UI display.
        /// </summary>
        public string Description { get; set; } = default!;

        /// <summary>
        /// Whether this is a built-in pattern (shipped with the system).
        /// Built-in patterns cannot be deleted, only disabled.
        /// </summary>
        public bool IsBuiltIn { get; set; } = true;

        /// <summary>
        /// Copy for cache hand-out: scalars copied, the Parameters dictionary duplicated, so a
        /// caller mutating the copy never touches a shared cached instance.
        /// </summary>
        public ImeLogPattern Clone()
        {
            var copy = (ImeLogPattern)MemberwiseClone();
            // Preserve a null Parameters as null (the wire omits it) rather than promoting it to {}.
            copy.Parameters = Parameters == null ? null! : new Dictionary<string, string>(Parameters);
            return copy;
        }
    }
}
