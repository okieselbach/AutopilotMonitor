using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Scan request for <see cref="IAppxDeploymentFailureScanner"/>. Immutable snapshot of the
    /// ESP failure context — the scan body runs on the threadpool and must not touch tracker state.
    /// </summary>
    internal sealed class AppxFailureScanRequest
    {
        /// <summary>Earliest TimeCreated to consider (monitoring start, capped lookback).</summary>
        public DateTime WindowStartUtc { get; }
        /// <summary>Time of the ESP failure observation (recency-bonus anchor).</summary>
        public DateTime WindowEndUtc { get; }
        /// <summary>ESP subcategory HRESULT (lower-case 0x-hex) or null.</summary>
        public string EspErrorCode { get; }
        /// <summary>ESP category label (DeviceSetup / AccountSetup).</summary>
        public string EspCategory { get; }
        /// <summary>Failed subcategory name (always "Apps" for this scan).</summary>
        public string FailedSubcategory { get; }

        public AppxFailureScanRequest(
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            string espErrorCode,
            string espCategory,
            string failedSubcategory)
        {
            WindowStartUtc = windowStartUtc;
            WindowEndUtc = windowEndUtc;
            EspErrorCode = string.IsNullOrEmpty(espErrorCode) ? null : espErrorCode;
            EspCategory = string.IsNullOrEmpty(espCategory) ? null : espCategory;
            FailedSubcategory = string.IsNullOrEmpty(failedSubcategory) ? null : failedSubcategory;
        }
    }

    /// <summary>
    /// One raw record read from the AppX deployment event log channel. Message and property
    /// strings are both captured because <c>FormatDescription()</c> is unreliable under
    /// SYSTEM/OOBE (missing message resources) while <c>record.Properties</c> usually still
    /// carries the bare PackageFullName and error values.
    /// </summary>
    internal sealed class AppxLogRecord
    {
        public DateTime TimeCreatedUtc { get; set; }
        public int EventId { get; set; }
        public int Level { get; set; }
        /// <summary>Rendered message, or null when FormatDescription failed.</summary>
        public string Message { get; set; }
        /// <summary>Raw event properties as strings (may be empty, never null).</summary>
        public List<string> PropertyStrings { get; set; } = new List<string>();
    }

    /// <summary>Raw scan outcome — records plus availability/diagnostic info.</summary>
    internal sealed class AppxFailureScanResult
    {
        /// <summary>False when the channel is missing or unreadable (scan_unavailable verdict).</summary>
        public bool ChannelAvailable { get; set; } = true;
        /// <summary>Short machine-readable reason when <see cref="ChannelAvailable"/> is false.</summary>
        public string UnavailableReason { get; set; }
        public List<AppxLogRecord> Records { get; set; } = new List<AppxLogRecord>();
        public long ScanDurationMs { get; set; }
        /// <summary>True when the record/time cap stopped the scan before the window was exhausted.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Seam for the settle-window AppX enrichment scan — tests inject a fake; production uses
    /// <see cref="AppxDeploymentFailureScanner"/>.
    /// </summary>
    internal interface IAppxDeploymentFailureScanner
    {
        AppxFailureScanResult Scan(AppxFailureScanRequest request);
    }

    /// <summary>
    /// One-shot reader of <c>Microsoft-Windows-AppXDeploymentServer/Operational</c> — the channel
    /// where MSIX/Store deployment failures land. Those installs run outside the IME Win32
    /// pipeline, so ImeLogTracker never sees them; when ESP reports an Apps-subcategory failure
    /// with an AppX-class HRESULT (e.g. 0x80073cf9) this scan is the only session-local source
    /// that can name the failing package (session 2bc884b6).
    /// Query pattern follows <see cref="StallProbeCollector"/>: XPath time+level filter,
    /// ReverseDirection (newest first), hard record + wall-clock caps, per-record dispose.
    /// </summary>
    internal sealed class AppxDeploymentFailureScanner : IAppxDeploymentFailureScanner
    {
        internal const string Channel = "Microsoft-Windows-AppXDeploymentServer/Operational";
        // Critical (1) + Error (2) only — Warning-level entries in this channel are routine
        // servicing noise and would drown the correlation.
        internal const int MaxRecords = 200;
        internal const int MaxScanMilliseconds = 10000;

        public AppxFailureScanResult Scan(AppxFailureScanRequest request)
        {
            var result = new AppxFailureScanResult();
            var stopwatch = Stopwatch.StartNew();

            var sinceStr = request.WindowStartUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var xpath = $"*[System[(Level >= 1 and Level <= 2) and TimeCreated[@SystemTime >= '{sinceStr}']]]";

            try
            {
                var query = new EventLogQuery(Channel, PathType.LogName, xpath) { ReverseDirection = true };
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            if (result.Records.Count >= MaxRecords || stopwatch.ElapsedMilliseconds > MaxScanMilliseconds)
                            {
                                result.Truncated = true;
                                break;
                            }

                            var entry = new AppxLogRecord
                            {
                                TimeCreatedUtc = record.TimeCreated?.ToUniversalTime() ?? DateTime.MinValue,
                                EventId = record.Id,
                                Level = record.Level ?? 0,
                            };

                            // FormatDescription can throw under SYSTEM/OOBE (message resources
                            // not loadable) — the Properties fallback below is mandatory.
                            try { entry.Message = record.FormatDescription(); }
                            catch { entry.Message = null; }

                            try
                            {
                                foreach (var prop in record.Properties)
                                {
                                    var s = prop?.Value?.ToString();
                                    if (!string.IsNullOrEmpty(s))
                                        entry.PropertyStrings.Add(s);
                                }
                            }
                            catch { /* best-effort — keep whatever was collected */ }

                            result.Records.Add(entry);
                        }
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                result.ChannelAvailable = false;
                result.UnavailableReason = "channel_not_found";
            }
            catch (UnauthorizedAccessException)
            {
                result.ChannelAvailable = false;
                result.UnavailableReason = "access_denied";
            }

            stopwatch.Stop();
            result.ScanDurationMs = stopwatch.ElapsedMilliseconds;
            return result;
        }
    }
}
