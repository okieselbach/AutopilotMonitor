using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// Response of <c>GET diagnostics/paths</c>: what every diagnostics package collects
    /// before a tenant's own entries — the built-in section catalog (compiled into the agent)
    /// and the platform-wide global paths set by Global Admins. Member-readable by design.
    /// </summary>
    public class DiagnosticsPathsResponse : IApiResponse
    {
        public IReadOnlyList<DiagnosticsBuiltInSectionWire> BuiltIn { get; set; } = default!;
        public IReadOnlyList<DiagnosticsLogPath> GlobalPaths { get; set; } = default!;
    }

    /// <summary>
    /// Wire projection of one built-in diagnostics section. <c>Condition</c> travels as the
    /// enum NAME ("Always" | "RealmJoinWatcher" | "DevicePreparation"), never the integer —
    /// the web switches on the string.
    /// </summary>
    public class DiagnosticsBuiltInSectionWire
    {
        public string Id { get; set; } = string.Empty;
        public string ZipFolder { get; set; } = string.Empty;
        /// <summary>UNEXPANDED source folder (may contain %ProgramData% or the user-profile token).</summary>
        public string SourceFolder { get; set; } = string.Empty;
        public IReadOnlyList<string> Patterns { get; set; } = default!;
        public bool IncludeSubfolders { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
    }
}
