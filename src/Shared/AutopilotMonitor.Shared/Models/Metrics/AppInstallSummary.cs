using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Per-app installation summary, written during event ingestion.
    /// Enables fleet-level app metrics without scanning raw events.
    /// </summary>
    public class AppInstallSummary
    {
        public string AppName { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// Intune app identity from the agent's app events (<c>appId</c> payload field) —
        /// Win32/IME apps carry the Intune app GUID in lowercase dashed form
        /// (<c>AppPackageState.Id</c>). Empty = sentinel: written before this column existed
        /// (2026-07 F1 PR1) or the events carried no appId. The row stays name-keyed
        /// (RowKey = {SessionId}_{AppName}); this column adds identity without a key migration.
        /// </summary>
        public string AppId { get; set; } = string.Empty;

        /// <summary>
        /// Whether this app is in the ESP's own blocking/tracking set
        /// (<c>esp_config_detected</c> lists, joined by <see cref="AppId"/> at session-terminal
        /// processing). Tri-state by design (source-data audit Q2): <c>true</c> = listed
        /// (positive evidence), <c>null</c> = unknown (absent from the lists, lists never
        /// observed, or row predates the column) — NEVER <c>false</c>: the lists are read
        /// early/partially and MSI/PFN namespaces may not match, so absence is not evidence
        /// of non-blocking.
        /// </summary>
        public bool? EspBlocking { get; set; }

        /// <summary>
        /// True when the same app display name was observed with a DIFFERENT <c>appId</c> in
        /// this session (source-data audit Q3): the name-keyed row then merges two distinct
        /// apps (e.g. device- + user-scope assignment of "Company Portal") and its
        /// status/duration mix is unattributable. Flagged rows are excluded from per-app
        /// fleet aggregates (with a disclosed exclusion count) instead of attempting a RowKey
        /// migration. Sticky once set.
        /// </summary>
        public bool AppIdCollision { get; set; }

        /// <summary>
        /// Lifecycle status: Succeeded, Failed, InProgress, or empty.
        /// Empty (default) is a sentinel meaning "no status-relevant event observed in the current
        /// aggregation batch". Aggregators only set a real value when they see started / completed /
        /// failed / skipped. The storage layer omits the column from the upsert when this is empty
        /// so Merge-mode preserves any prior real value across batches that contain only progress
        /// or telemetry events. Readers fall back to "InProgress" when the column is missing on a
        /// row, so the UI/API contract remains stable.
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Terminal lifecycle state carried by the agent payload on the closing event:
        /// "Installed", "Skipped", "Postponed" (from <c>app_install_completed</c>'s
        /// <c>state</c> field / <c>app_install_skipped</c>) or "Error" (from
        /// <c>app_install_failed</c>). Empty = sentinel: no terminal event observed in the
        /// current batch, or a row written before this column existed (2026-07 PR0) —
        /// storage omits the column on empty so Merge-mode preserves a prior value.
        /// Skipped/Postponed rows are no real install attempt: they are excluded from
        /// duration statistics and from the failure/success rate (PR0 decision 2026-07-26);
        /// Status stays "Succeeded" for backward compatibility.
        /// </summary>
        public string TerminalState { get; set; } = string.Empty;

        /// <summary>Total installation duration in seconds (from start to complete/failed)</summary>
        public int DurationSeconds { get; set; }

        /// <summary>Total download size in bytes</summary>
        public long DownloadBytes { get; set; }

        /// <summary>Download duration in seconds</summary>
        public int DownloadDurationSeconds { get; set; }

        /// <summary>Error code if failed</summary>
        public string FailureCode { get; set; } = string.Empty;

        /// <summary>Error message if failed</summary>
        public string FailureMessage { get; set; } = string.Empty;

        /// <summary>When this app install started</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>When this app install completed or failed</summary>
        public DateTime? CompletedAt { get; set; }

        // Delivery Optimization telemetry
        /// <summary>DO: total file size reported by DO</summary>
        public long DoFileSize { get; set; }

        /// <summary>DO: total bytes actually downloaded (may differ from DoFileSize on partial downloads)</summary>
        public long DoTotalBytesDownloaded { get; set; }

        /// <summary>DO: bytes from all peer sources</summary>
        public long DoBytesFromPeers { get; set; }

        /// <summary>DO: bytes from HTTP (CDN)</summary>
        public long DoBytesFromHttp { get; set; }

        /// <summary>DO: percentage from P2P (0-100)</summary>
        public int DoPercentPeerCaching { get; set; }

        /// <summary>DO: download mode (0=Background, 1=Foreground, 2=Bypass/LAN, 99=Simple)</summary>
        public int DoDownloadMode { get; set; } = -1;

        /// <summary>DO: actual download duration (TimeSpan string)</summary>
        public string DoDownloadDuration { get; set; } = string.Empty;

        /// <summary>DO: bytes from LAN peers</summary>
        public long DoBytesFromLanPeers { get; set; }

        /// <summary>DO: bytes from group peers</summary>
        public long DoBytesFromGroupPeers { get; set; }

        /// <summary>DO: bytes from internet peers</summary>
        public long DoBytesFromInternetPeers { get; set; }

        /// <summary>DO: bytes from link-local peers (same subnet). Often combined with LAN in UI.</summary>
        public long DoBytesFromLinkLocalPeers { get; set; }

        /// <summary>DO: bytes served from a Microsoft Connected Cache (MCC) node — counted separately from BytesFromPeers.</summary>
        public long DoBytesFromCacheServer { get; set; }

        /// <summary>DO: URI/IP of the MCC node that served bytes (only when DoBytesFromCacheServer &gt; 0).</summary>
        public string DoCacheHost { get; set; } = string.Empty;

        // App metadata (extracted from IME logs by ImeLogTracker)
        /// <summary>App version string (e.g. "1.7.00.4472"). Emitted in app_install_started.</summary>
        public string AppVersion { get; set; } = string.Empty;

        /// <summary>App type: Win32, MSI, WinGet, Store, LOB. Emitted in app_install_started.</summary>
        public string AppType { get; set; } = string.Empty;

        /// <summary>Install attempt number (1 = first try, 2+ = retry). Emitted in app_install_started.</summary>
        public int AttemptNumber { get; set; }

        /// <summary>Installer phase where failure occurred: Download, PreInstall, Install, PostInstall, Detection. Emitted in app_install_failed.</summary>
        public string InstallerPhase { get; set; } = string.Empty;

        /// <summary>Installer exit code (nullable – not every app type emits one). Emitted in app_install_completed/failed.</summary>
        public int? ExitCode { get; set; }

        /// <summary>Detection rule result after install: Detected, NotDetected. Emitted in app_install_completed/failed.</summary>
        public string DetectionResult { get; set; } = string.Empty;
    }
}
