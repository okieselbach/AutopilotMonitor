using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Gate for a built-in diagnostics section. The agent evaluates it against its effective
    /// configuration and the enrollment scenario; the portal renders it as a context pill.
    /// </summary>
    public enum DiagnosticsSectionCondition
    {
        /// <summary>Collected by every agent on every enrollment.</summary>
        Always = 0,

        /// <summary>
        /// Collected only when the tenant's RealmJoin Watcher is enabled
        /// (<c>AnalyzerConfiguration.EnableRealmJoinWatcher</c>).
        /// </summary>
        RealmJoinWatcher = 1,

        /// <summary>
        /// Collected only on Windows Autopilot Device Preparation enrollments (deterministic
        /// WDP marker — see <c>EnrollmentRegistryDetector.IsDeterministicDevicePreparation</c>).
        /// </summary>
        DevicePreparation = 2,
    }

    /// <summary>
    /// One built-in collection section of the diagnostics ZIP. Pure data — no I/O. The agent
    /// expands <see cref="SourceFolder"/> (environment variables and the
    /// <see cref="DiagnosticsBuiltInSections.UserProfileToken"/>) at collection time.
    /// </summary>
    public sealed class DiagnosticsBuiltInSection
    {
        /// <summary>Stable key — used for test overrides and package-manifest lines.</summary>
        public string Id { get; }

        /// <summary>Folder inside the ZIP (e.g. <c>AgentLogs</c>, <c>RealmJoinLogs/Windows</c>).</summary>
        public string ZipFolder { get; }

        /// <summary>
        /// UNEXPANDED source folder: may contain <c>%ProgramData%</c> or the
        /// <see cref="DiagnosticsBuiltInSections.UserProfileToken"/>.
        /// </summary>
        public string SourceFolder { get; }

        /// <summary>File-name patterns collected from <see cref="SourceFolder"/>.</summary>
        public IReadOnlyList<string> Patterns { get; }

        /// <summary>When true, subdirectories are walked recursively (tree preserved in the ZIP).</summary>
        public bool IncludeSubfolders { get; }

        /// <summary>Human-readable description shown in the portal.</summary>
        public string Description { get; }

        public DiagnosticsSectionCondition Condition { get; }

        public DiagnosticsBuiltInSection(
            string id,
            string zipFolder,
            string sourceFolder,
            string[] patterns,
            bool includeSubfolders,
            string description,
            DiagnosticsSectionCondition condition = DiagnosticsSectionCondition.Always)
        {
            Id = id;
            ZipFolder = zipFolder;
            SourceFolder = sourceFolder;
            Patterns = patterns;
            IncludeSubfolders = includeSubfolders;
            Description = description;
            Condition = condition;
        }
    }

    /// <summary>
    /// The single source of truth for what a diagnostics package collects BEFORE any
    /// admin-configured path: the agent iterates <see cref="All"/> in order when building the
    /// archive, and the backend serves the same list to the portal (GET diagnostics/paths) so
    /// what administrators see is exactly what the agent does. Built-in sections are reviewed
    /// code and therefore bypass the configured-path guards (DiagnosticsPathGuards), which exist
    /// to validate admin-typed input.
    /// </summary>
    public static class DiagnosticsBuiltInSections
    {
        /// <summary>
        /// Custom token resolved by the agent to the signed-in user's profile folder
        /// (the agent runs as SYSTEM, so <c>%USERPROFILE%</c> would point at the SYSTEM profile).
        /// </summary>
        public const string UserProfileToken = "%LOGGED_ON_USER_PROFILE%";

        /// <summary>
        /// File extensions collected from log folders (Agent + IME): standard logs, structured
        /// data, traces, event logs, and diagnostics archives.
        /// </summary>
        public static readonly string[] LogFilePatterns =
        {
            "*.log", "*.txt", "*.json", "*.jsonl",
            "*.etl", "*.evtx", "*.xml", "*.csv", "*.cab",
        };

        /// <summary>
        /// Patterns collected from the agent state folder: the log patterns plus completion
        /// markers (<c>enrollment-complete.marker</c>, <c>whiteglove-backfill-state.json</c>).
        /// </summary>
        public static readonly string[] StateFilePatterns =
        {
            "*.log", "*.txt", "*.json", "*.jsonl",
            "*.etl", "*.evtx", "*.xml", "*.csv", "*.cab",
            "*.complete", "*.marker",
        };

        /// <summary>
        /// Marker patterns from the top-level data directory. Kept tight on purpose: only
        /// completion/exit markers, no session.id / bootstrap.json (config, not forensic state).
        /// </summary>
        public static readonly string[] RootMarkerPatterns = { "*.complete", "*.marker" };

        /// <summary>Telemetry spool: only the JSON-shaped files (pending uploads + upload cursor).</summary>
        public static readonly string[] SpoolFilePatterns = { "*.jsonl", "*.json" };

        /// <summary>
        /// Ordered catalog — archive order equals catalog order. The Always sections form the
        /// historical package layout and are lock-tested; the conditional sections were added
        /// 2026-08-23 (RealmJoin logs behind the tenant's RealmJoin Watcher toggle, the Device
        /// Preparation bootstrapper event log behind the deterministic WDP marker).
        /// </summary>
        public static readonly IReadOnlyList<DiagnosticsBuiltInSection> All = new[]
        {
            new DiagnosticsBuiltInSection(
                "AgentLogs", "AgentLogs", Constants.LogDirectory, LogFilePatterns, false,
                "Autopilot Monitor agent logs"),
            new DiagnosticsBuiltInSection(
                "ImeLogs", "ImeLogs", @"%ProgramData%\Microsoft\IntuneManagementExtension\Logs", LogFilePatterns, false,
                "Intune Management Extension logs"),
            // Active event-log channels hold their .evtx exclusively locked; the agent exports
            // the channel via wevtutil, which costs seconds — only worth it on WDP enrollments,
            // where this channel IS the provisioning record (batches, workloads, errors).
            new DiagnosticsBuiltInSection(
                "ImeBootstrapperEventLog", "ImeLogs", @"C:\Windows\System32\winevt\Logs",
                new[] { "BootstrapperAgentServiceLogProvider.evtx" }, false,
                "Device Preparation event log (IME bootstrapper agent)",
                DiagnosticsSectionCondition.DevicePreparation),
            new DiagnosticsBuiltInSection(
                "AgentState", "AgentState", Constants.StateDirectory, StateFilePatterns, true,
                "Agent decision-engine state, journal and markers (incl. quarantine and WhiteGlove Part-1 buckets)"),
            new DiagnosticsBuiltInSection(
                "AgentSpool", "AgentSpool", Constants.SpoolDirectory, SpoolFilePatterns, false,
                "Pending telemetry uploads and upload cursor"),
            new DiagnosticsBuiltInSection(
                "AgentMarkers", "AgentMarkers", Constants.AgentDataDirectory, RootMarkerPatterns, false,
                "Top-level completion and clean-exit markers"),
            // RealmJoin — opt-in per tenant (RealmJoin Watcher). ZIP layout mirrors the disk:
            // C:\Windows\Logs\{realmjoin*.log, RealmJoin\...} → RealmJoinLogs/Windows/...,
            // %LOCALAPPDATA%\RealmJoin\{tray*.log, Logs\...} → RealmJoinLogs/User/...
            new DiagnosticsBuiltInSection(
                "RealmJoinWindows", "RealmJoinLogs/Windows", @"C:\Windows\Logs",
                new[] { "realmjoin*.log" }, false,
                "RealmJoin client logs",
                DiagnosticsSectionCondition.RealmJoinWatcher),
            new DiagnosticsBuiltInSection(
                "RealmJoinPackages", "RealmJoinLogs/Windows/RealmJoin", @"C:\Windows\Logs\RealmJoin",
                new[] { "*.log" }, true,
                "RealmJoin package logs",
                DiagnosticsSectionCondition.RealmJoinWatcher),
            new DiagnosticsBuiltInSection(
                "RealmJoinChoco", "RealmJoinLogs/Choco", @"%ProgramData%\RealmJoin\choco\logs",
                new[] { "*.log" }, true,
                "RealmJoin Chocolatey install logs",
                DiagnosticsSectionCondition.RealmJoinWatcher),
            new DiagnosticsBuiltInSection(
                "RealmJoinUserTray", "RealmJoinLogs/User", UserProfileToken + @"\AppData\Local\RealmJoin",
                new[] { "tray*.log" }, false,
                "RealmJoin tray logs (signed-in user)",
                DiagnosticsSectionCondition.RealmJoinWatcher),
            new DiagnosticsBuiltInSection(
                "RealmJoinUserLogs", "RealmJoinLogs/User/Logs", UserProfileToken + @"\AppData\Local\RealmJoin\Logs",
                new[] { "*.log" }, true,
                "RealmJoin per-user logs (signed-in user)",
                DiagnosticsSectionCondition.RealmJoinWatcher),
        };
    }
}
