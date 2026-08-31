using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.DataAccess;

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
    // Declaration order == wire order.
    public class SubmitSessionReportResponse : IApiResponse
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

    /// <summary>
    /// One serial-number bucket in the GetDeviceNotRegistered aggregation. All values are
    /// self-reported by devices through the unauthenticated distress channel — UNVERIFIED.
    /// </summary>
    // Declaration order == wire order.
    public class DeviceNotRegisteredItem
    {
        public string SerialNumber { get; set; } = default!;
        public string Manufacturer { get; set; } = default!;
        public string Model { get; set; } = default!;

        /// <summary>Sticky-true across the bucket: once any report carried the W365 marker.</summary>
        public bool IsCloudPc { get; set; }
        public int AttemptCount { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    /// <summary>
    /// Success body of GET audit/device-not-registered: unregistered-device rejections
    /// aggregated by serial number over the distress-report retention window.
    /// </summary>
    // Declaration order == wire order.
    public class DeviceNotRegisteredResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<DeviceNotRegisteredItem> Aggregated { get; set; } = default!;

        /// <summary>Count of raw DeviceNotRegistered distress reports before aggregation.</summary>
        public int TotalRawReports { get; set; }
        public string DataQualityNotice { get; set; } = default!;
    }

    /// <summary>
    /// One manufacturer+model bucket in the GetHardwareRejected aggregation. All values are
    /// self-reported by devices through the unauthenticated distress channel — UNVERIFIED.
    /// </summary>
    // Declaration order == wire order.
    public class HardwareRejectedItem
    {
        public string Manufacturer { get; set; } = default!;
        public string Model { get; set; } = default!;
        public int AttemptCount { get; set; }
        public int UniqueSerials { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }

        /// <summary>Up to five distinct serial numbers from the bucket.</summary>
        public IReadOnlyList<string> SampleSerialNumbers { get; set; } = default!;
    }

    /// <summary>
    /// Success body of GET audit/hardware-rejected: hardware-whitelist rejections
    /// aggregated by manufacturer+model over the distress-report retention window.
    /// </summary>
    // Declaration order == wire order.
    public class HardwareRejectedResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<HardwareRejectedItem> Aggregated { get; set; } = default!;

        /// <summary>Count of raw HardwareNotAllowed distress reports before aggregation.</summary>
        public int TotalRawReports { get; set; }
        public string DataQualityNotice { get; set; } = default!;
    }

    /// <summary>
    /// One serial-number bucket in the GetTpmPssUnsupported aggregation. All values are
    /// self-reported by devices through the unauthenticated distress channel — UNVERIFIED.
    /// </summary>
    // Declaration order == wire order.
    public class TpmPssUnsupportedItem
    {
        public string SerialNumber { get; set; } = default!;
        public string Manufacturer { get; set; } = default!;
        public string Model { get; set; } = default!;
        public int AttemptCount { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    /// <summary>
    /// Success body of GET audit/tpm-pss-unsupported: devices whose TPM cannot perform
    /// RSA-PSS signing, aggregated by serial number over the distress-report retention window.
    /// </summary>
    // Declaration order == wire order.
    public class TpmPssUnsupportedResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<TpmPssUnsupportedItem> Aggregated { get; set; } = default!;

        /// <summary>Count of raw TpmPssUnsupported distress reports before aggregation.</summary>
        public int TotalRawReports { get; set; }
        public string DataQualityNotice { get; set; } = default!;
    }

    /// <summary>
    /// Success body of GET global/session-reports — both the non-paged and the paged variant
    /// (the non-paged variant simply carries no nextLink; WhenWritingNull keeps the wire
    /// identical to the historical shape).
    /// </summary>
    // Declaration order == wire order.
    public class SessionReportListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<SessionReportMetadata> Reports { get; set; } = default!;

        /// <summary>Absolute-path link to the next page; null/absent on the last page and in non-paged responses.</summary>
        public string? NextLink { get; set; }
    }

    /// <summary>
    /// Success body of GET global/distress-reports: all pre-auth distress reports (Global Admin).
    /// </summary>
    // Declaration order == wire order.
    public class DistressReportListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<DistressReportEntry> Reports { get; set; } = default!;
    }

    /// <summary>
    /// Success body of GET global/session-reports/download-url: short-lived SAS download URL
    /// for a session report blob.
    /// </summary>
    // Declaration order == wire order.
    public class SessionReportDownloadUrlResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string DownloadUrl { get; set; } = default!;
    }
}
