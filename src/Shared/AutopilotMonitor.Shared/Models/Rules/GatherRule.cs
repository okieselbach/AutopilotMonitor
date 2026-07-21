using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Defines what data the agent should collect
    /// Gather rules are delivered to the agent via the config API
    /// and can be managed (enabled/disabled, created) through the portal
    /// </summary>
    public class GatherRule
    {
        /// <summary>
        /// Unique rule identifier (e.g., "GATHER-NET-001")
        /// </summary>
        public string RuleId { get; set; } = default!;

        /// <summary>
        /// Human-readable rule title (e.g., "Collect WinHTTP Proxy Settings")
        /// </summary>
        public string Title { get; set; } = default!;

        /// <summary>
        /// Detailed description of what this rule collects and why
        /// </summary>
        public string Description { get; set; } = default!;

        /// <summary>
        /// Rule category: network, identity, apps, device, esp, enrollment
        /// </summary>
        public string Category { get; set; } = default!;

        /// <summary>
        /// Semantic version of this rule (e.g., "1.0.0")
        /// </summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Author of this rule
        /// </summary>
        public string Author { get; set; } = "Autopilot Monitor";

        /// <summary>
        /// Whether this rule is currently enabled for the tenant
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether this is a built-in rule (shipped with the system)
        /// Built-in rules cannot be deleted, only disabled
        /// </summary>
        public bool IsBuiltIn { get; set; } = true;

        /// <summary>
        /// Whether this is a community-contributed rule
        /// Community rules behave like built-in rules (read-only, state stored separately)
        /// but are displayed with a distinct "Community" badge in the portal
        /// </summary>
        public bool IsCommunity { get; set; } = false;

        /// <summary>
        /// Where this global rule row came from — see <see cref="RuleProvenance"/>. Drives the
        /// self-maintaining sunset: "embedded"/null = owned by the deployed binary's catalog (may be
        /// sunset when it leaves that catalog); "github" = reseeded from GitHub ahead of the binary
        /// (exempt from the embedded catalog sunset/filter). Null on pre-existing rows = embedded.
        /// </summary>
        public string? Provenance { get; set; }

        // ===== WHAT TO COLLECT =====

        /// <summary>
        /// Type of data collection:
        /// - "registry": Read values from the Windows Registry
        /// - "eventlog": Read entries from a Windows Event Log
        /// - "wmi": Execute a WMI/CIM query
        /// - "file": Check file/directory existence and optionally read content
        /// - "command_allowlisted": Run a pre-approved command (PowerShell or CLI). Only commands on
        ///   the agent's hardcoded allowlist in GatherRuleExecutor.cs are permitted. Unlisted commands
        ///   are blocked and generate a security_warning event. See the allowlist in GatherRuleExecutor.cs
        ///   for the full list of approved commands.
        /// - "logparser": Parse a CMTrace-format log file using a regex pattern with named capture groups
        /// </summary>
        public string CollectorType { get; set; } = default!;

        /// <summary>
        /// Target for collection:
        /// - registry: Registry path (e.g., "HKLM\SOFTWARE\Microsoft\...")
        /// - eventlog: Event log name (e.g., "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin")
        /// - wmi: WMI query (e.g., "SELECT * FROM Win32_TPM WHERE __NAMESPACE='root\\CIMV2\\Security\\MicrosoftTpm'")
        /// - file: File or directory path with env vars (e.g., "C:\Windows\INF\setupapi.dev.log")
        /// - command_allowlisted: Exact command string as it appears in the allowlist (e.g., "Get-Tpm", "dsregcmd /status")
        /// - logparser: Log file path with env vars (e.g., "%ProgramData%\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log")
        /// </summary>
        public string Target { get; set; } = default!;

        /// <summary>
        /// Additional parameters for the collector:
        /// - registry: { "valueName": "ProxyServer" } — omit to read all values
        /// - eventlog: { "maxEntries": "10", "source": "...", "eventId": "62407", "messageFilter": "*ESPProgress*" }
        /// - wmi: { "namespace": "root\\CIMV2\\Security\\MicrosoftTpm" }
        /// - file: { "readContent": "true" } — only reads files &lt;50 KB
        /// - command_allowlisted: (no additional parameters — command is the full string in Target)
        /// - logparser: { "pattern": "regex with (?&lt;namedGroups&gt;)", "trackPosition": "true", "maxLines": "1000" }
        /// </summary>
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();

        // ===== WHEN TO COLLECT =====

        /// <summary>
        /// Trigger type: "startup", "phase_change", "phase_exit", "interval", "on_event".
        /// <para>
        /// "phase_change" fires once when the phase in <see cref="TriggerPhase"/> is ENTERED,
        /// "phase_exit" once when it is LEFT — the two one-shot bookends of a phase.
        /// </para>
        /// </summary>
        public string Trigger { get; set; } = default!;

        /// <summary>
        /// Interval in seconds (only used when Trigger = "interval")
        /// </summary>
        public int? IntervalSeconds { get; set; }

        /// <summary>
        /// Phase to trigger on (used when Trigger = "phase_change" or "phase_exit").
        /// Canonical tokens are the <see cref="EnrollmentPhase"/> enum names, e.g.
        /// "DeviceSetup", "AccountSetup", "Complete". Empty = every phase transition.
        /// </summary>
        public string TriggerPhase { get; set; } = default!;

        /// <summary>
        /// Event type to trigger on (only used when Trigger = "on_event")
        /// e.g., "app_install_failed"
        /// </summary>
        public string TriggerEventType { get; set; } = default!;

        /// <summary>
        /// Restricts the rule to run only while the current enrollment phase is one of these
        /// phases. Canonical tokens are the <see cref="EnrollmentPhase"/> enum names
        /// ("Start", "DevicePreparation", "DeviceSetup", "AppsDevice", "AccountSetup",
        /// "AppsUser", "FinalizingSetup", "Complete"); "Unknown" and "Failed" are rejected
        /// by backend validation. Null or empty = unrestricted (runs in every phase —
        /// legacy behavior). Mutually exclusive with <see cref="ActiveFromPhase"/>;
        /// if both are set the agent defensively prefers this list.
        /// Applies to ALL trigger types. Before the first phase signal of a session,
        /// scoped rules are inactive.
        /// </summary>
        public List<string>? ActivePhases { get; set; }

        /// <summary>
        /// Activates the rule once the enrollment phase first reaches this phase
        /// (ordinal comparison on <see cref="EnrollmentPhase"/>, ignoring Unknown/Failed),
        /// then keeps it active for the rest of the session (sticky latch — including
        /// through Failed). Canonical tokens as in <see cref="ActivePhases"/>.
        /// Null = unrestricted. Mutually exclusive with <see cref="ActivePhases"/>.
        /// </summary>
        public string? ActiveFromPhase { get; set; }

        /// <summary>
        /// Emit behavior for collected results:
        /// null / "always" = emit on every collection (legacy behavior);
        /// "on_change" = poll on the trigger cadence but emit only when the collected
        /// result differs from the last emitted one. The first in-scope result always
        /// emits; the suppressed poll count is carried on the next emitted event
        /// (suppressedPolls / suppressedSinceUtc in the event data).
        /// </summary>
        public string? EmitMode { get; set; }

        // ===== OUTPUT =====

        /// <summary>
        /// EventType for the emitted event (e.g., "gather_proxy_settings")
        /// </summary>
        public string OutputEventType { get; set; } = default!;

        /// <summary>
        /// Severity for the emitted event
        /// Default: "Info"
        /// </summary>
        public string OutputSeverity { get; set; } = "Info";

        // ===== METADATA =====

        /// <summary>
        /// Tags for filtering and categorization
        /// </summary>
        public string[] Tags { get; set; } = new string[0];

        /// <summary>
        /// When this rule was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this rule was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
