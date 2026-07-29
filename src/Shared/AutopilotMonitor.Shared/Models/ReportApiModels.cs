using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Discriminator values for the ReportType field on <see cref="SessionReportMetadata"/>.
    /// All rows live in the same SessionReports table and the same session-reports blob
    /// container regardless of type — only the wire model and ZIP layout differ.
    /// </summary>
    public static class ReportTypes
    {
        /// <summary>Submitted from the session detail page; ZIP carries session/events/timeline exports.</summary>
        public const string Session = "session";

        /// <summary>Submitted from /settings/tenant/support; ZIP carries only user-attached files.</summary>
        public const string DiagFiles = "diagFiles";
    }

    /// <summary>
    /// Request to submit a session report for analysis by the Autopilot Monitor team.
    /// Sent as JSON from the frontend; the backend creates the ZIP and uploads to central storage.
    /// </summary>
    public class SubmitSessionReportRequest
    {
        public string TenantId { get; set; } = default!;
        public string SessionId { get; set; } = default!;
        public string Comment { get; set; } = default!;
        public string Email { get; set; } = default!;

        /// <summary>Session row as CSV (single data row with header)</summary>
        public string SessionCsv { get; set; } = default!;

        /// <summary>Pre-generated UI timeline export (TXT)</summary>
        public string TimelineExportTxt { get; set; } = default!;

        /// <summary>Pre-generated raw events table export (CSV)</summary>
        public string EventsCsv { get; set; } = default!;

        /// <summary>Pre-generated analysis rule results export (CSV)</summary>
        public string RuleResultsCsv { get; set; } = default!;

        /// <summary>Base64-encoded screenshot image (optional)</summary>
        public string ScreenshotBase64 { get; set; } = default!;

        /// <summary>Original screenshot file name for extension detection</summary>
        public string ScreenshotFileName { get; set; } = default!;

        /// <summary>Base64-encoded agent log file (optional, max 5 MB)</summary>
        public string AgentLogBase64 { get; set; } = default!;

        /// <summary>Original agent log file name</summary>
        public string AgentLogFileName { get; set; } = default!;

        /// <summary>
        /// When true and the session has an uploaded diagnostics archive, the backend copies
        /// that archive server-side into the durable session-reports container so it survives
        /// session deletion and retention cleanup.
        /// </summary>
        public bool IncludeDiagnostics { get; set; }

        /// <summary>Number of events the client had loaded when it generated the exports.</summary>
        public int? ExportedEventCount { get; set; }

        /// <summary>The session's EventCount as the client saw it — compared against
        /// <see cref="ExportedEventCount"/> this flags a partial export.</summary>
        public int? SessionEventCount { get; set; }

        /// <summary>True when the client was still streaming event pages at submit time.</summary>
        public bool? EventStreamActive { get; set; }
    }

    /// <summary>
    /// Request to submit diagnostic files for analysis without a session context.
    /// Used from /settings/tenant/support when an admin needs to ship logs/state files
    /// to the Autopilot Monitor team without binding them to a specific enrollment session.
    /// </summary>
    public class SubmitDiagFilesReportRequest
    {
        public string TenantId { get; set; } = default!;
        public string Comment { get; set; } = default!;
        public string Email { get; set; } = default!;

        /// <summary>Base64-encoded screenshot image (optional)</summary>
        public string ScreenshotBase64 { get; set; } = default!;

        /// <summary>Original screenshot file name for extension detection</summary>
        public string ScreenshotFileName { get; set; } = default!;

        /// <summary>Base64-encoded log/state payload (single file or zip of many; max ~5 MB enforced client-side)</summary>
        public string AgentLogBase64 { get; set; } = default!;

        /// <summary>Original file name (e.g. "agent.log", "state.json", "diag-files.zip")</summary>
        public string AgentLogFileName { get; set; } = default!;
    }

    /// <summary>
    /// Response from session report submission
    /// </summary>
    public class SubmitSessionReportResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public string ReportId { get; set; } = default!;
    }

    /// <summary>
    /// Session report metadata for the admin-config reports table.
    /// Both Session-context reports and Diag-Files-only reports share this row schema —
    /// the <see cref="ReportType"/> discriminator distinguishes the two, and
    /// <see cref="SessionId"/> is empty for diag-files submissions.
    /// </summary>
    public class SessionReportMetadata
    {
        public string ReportId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public string SessionId { get; set; } = default!;
        public string Comment { get; set; } = default!;
        public string Email { get; set; } = default!;
        public string BlobName { get; set; } = default!;
        public string SubmittedBy { get; set; } = default!;
        public DateTime SubmittedAt { get; set; }
        public string AdminNote { get; set; } = default!;

        /// <summary>One of <see cref="ReportTypes"/>. Defaults to "session" so legacy rows map cleanly.</summary>
        public string ReportType { get; set; } = ReportTypes.Session;

        /// <summary>
        /// Flat name of the session diagnostics archive copied into the session-reports
        /// container at submit time. Null when the copy was never requested or failed.
        /// </summary>
        public string? DiagnosticsBlobName { get; set; }

        /// <summary>
        /// Outcome of the diagnostics copy: "Copied" or one of the "Failed:*" reasons.
        /// Null when the submitter did not request the copy.
        /// </summary>
        public string? DiagnosticsCopyStatus { get; set; }
    }
}
