using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Polls OS-level Delivery Optimization status via a persistent PowerShell Runspace.
    /// Matches DO entries to tracked IME app downloads using the intunewin-bin FileId format.
    /// Emits download_progress events on every poll (for live UI) and do_telemetry once per app
    /// when the download completes.
    ///
    /// Lifecycle: starts dormant, wakes up when ImeLogTracker detects a download,
    /// goes dormant again when all downloads are enriched.
    /// </summary>
    public class DeliveryOptimizationCollector : CollectorBase
    {
        private const string LogFileName = "do-status.jsonl";
        private const int InvokeTimeoutMs = 5000;

        private readonly Func<AppPackageStateList> _getPackageStates;
        private readonly Action<AppPackageState> _onDoTelemetryReceived;
        private readonly string _logFilePath;

        private Runspace _runspace;
        private bool _permanentlyDisabled;
        private int _consecutiveErrors;
        private volatile bool _dormant = true;
        private int _collecting; // concurrency guard (0 = idle, 1 = collecting)

        // Track which apps we already sent final do_telemetry for (prevents duplicate callbacks)
        private readonly HashSet<string> _enrichedAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Change detection per app: only emit download_progress when bytes actually changed
        private readonly Dictionary<string, long> _lastBytesPerApp = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Change detection for JSONL log: only write when overall state changed
        private long _lastSnapshotFingerprint;

        public DeliveryOptimizationCollector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int intervalSeconds,
            Func<AppPackageStateList> getPackageStates,
            Action<AppPackageState> onDoTelemetryReceived,
            string logDirectory)
            : base(sessionId, tenantId, post, logger, intervalSeconds)
        {
            _getPackageStates = getPackageStates ?? throw new ArgumentNullException(nameof(getPackageStates));
            _onDoTelemetryReceived = onDoTelemetryReceived;
            _logFilePath = Path.Combine(logDirectory, LogFileName);
        }

        /// <summary>Start dormant — timer does not fire until WakeUp() is called.</summary>
        protected override TimeSpan GetInitialDelay() => Timeout.InfiniteTimeSpan;

        protected override void OnBeforeStart()
        {
            EnsureRunspace();
        }

        protected override void OnAfterStop()
        {
            DisposeRunspace();
        }

        /// <summary>
        /// Wakes the collector from dormant state. Called by MonitoringService when
        /// ImeLogTracker detects an app entering the Downloading state.
        /// Safe to call multiple times / from any thread.
        /// </summary>
        public void WakeUp()
        {
            if (_permanentlyDisabled || !_dormant) return;
            _dormant = false;
            Logger.Info("[DeliveryOptimizationCollector] Waking up — download activity detected");
            ResumeTimer();
        }

        protected override void Collect()
        {
            if (_permanentlyDisabled || _dormant) return;

            // Concurrency guard: Runspace is not thread-safe. Skip if previous poll is still running.
            if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
            {
                Logger.Debug("[DeliveryOptimizationCollector] Skipping poll — previous invocation still running");
                return;
            }
            try
            {
                CollectCore();
            }
            finally
            {
                Interlocked.Exchange(ref _collecting, 0);
            }
        }

        private void CollectCore()
        {

            // Guard: check if there's still work to do
            var packageStates = _getPackageStates();
            if (packageStates == null || packageStates.Count == 0) return;

            var hasActiveDownloads = false;
            var hasPendingEnrichment = false;
            for (int i = 0; i < packageStates.Count; i++)
            {
                var p = packageStates[i];
                if (p.InstallationState == AppInstallationState.Downloading ||
                    p.InstallationState == AppInstallationState.Installing)
                    hasActiveDownloads = true;

                if (!p.HasDoTelemetry && !_enrichedAppIds.Contains(p.Id) &&
                    p.DownloadingOrInstallingSeen &&
                    (p.InstallationState == AppInstallationState.Installed ||
                     p.InstallationState == AppInstallationState.Error))
                    hasPendingEnrichment = true;
            }

            if (!hasActiveDownloads && !hasPendingEnrichment)
            {
                Logger.Info("[DeliveryOptimizationCollector] Going dormant — no active downloads or pending enrichment");
                _dormant = true;
                PauseTimer();
                return;
            }

            // Invoke PowerShell via persistent Runspace
            var results = InvokeDoStatus();
            if (results == null) return;

            _consecutiveErrors = 0;

            // Process results: emit progress events and match completed downloads
            ProcessResults(results, packageStates);
        }

        // -----------------------------------------------------------------------
        // PowerShell Runspace management
        // -----------------------------------------------------------------------

        private void EnsureRunspace()
        {
            if (_runspace != null && _runspace.RunspaceStateInfo.State == RunspaceState.Opened)
                return;

            DisposeRunspace();

            try
            {
                _runspace = RunspaceFactory.CreateRunspace();
                _runspace.Open();
                Logger.Info("[DeliveryOptimizationCollector] PowerShell Runspace opened");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Failed to open Runspace: {ex.Message}");
                _runspace = null;
            }
        }

        private void DisposeRunspace()
        {
            if (_runspace == null) return;
            try
            {
                _runspace.Close();
                _runspace.Dispose();
            }
            catch { /* best effort */ }
            _runspace = null;
        }

        /// <summary>
        /// Invokes Get-DeliveryOptimizationStatus in the persistent Runspace.
        /// Returns a list of PSObject results, or null on error.
        /// Self-heals: recreates the Runspace on failure.
        /// </summary>
        private List<PSObject> InvokeDoStatus()
        {
            EnsureRunspace();
            if (_runspace == null)
            {
                HandleError("Runspace not available");
                return null;
            }

            try
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddCommand("Get-DeliveryOptimizationStatus");

                    // Invoke with timeout protection
                    var asyncResult = ps.BeginInvoke();
                    if (!asyncResult.AsyncWaitHandle.WaitOne(InvokeTimeoutMs))
                    {
                        ps.Stop();
                        Logger.Warning("[DeliveryOptimizationCollector] PS invoke timed out after 5s, stopping");
                        HandleError("timeout");
                        return null;
                    }

                    var results = new List<PSObject>(ps.EndInvoke(asyncResult));

                    // Check for errors in the PS error stream
                    if (ps.HadErrors && ps.Streams.Error.Count > 0)
                    {
                        var firstError = ps.Streams.Error[0].ToString();
                        if (firstError.Contains("is not recognized as the name of a cmdlet") ||
                            firstError.Contains("CommandNotFoundException"))
                        {
                            Logger.Warning("[DeliveryOptimizationCollector] Get-DeliveryOptimizationStatus not available, disabling permanently");
                            _permanentlyDisabled = true;
                            return null;
                        }
                        Logger.Debug($"[DeliveryOptimizationCollector] PS error stream: {firstError}");
                    }

                    return results;
                }
            }
            catch (PSInvalidOperationException ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Runspace error, self-healing: {ex.Message}");
                DisposeRunspace();
                HandleError(ex.Message);
                return null;
            }
            catch (RuntimeException ex)
            {
                // Catches command-not-found and other PS runtime errors
                if (ex.ErrorRecord?.FullyQualifiedErrorId?.Contains("CommandNotFoundException") == true)
                {
                    Logger.Warning("[DeliveryOptimizationCollector] Get-DeliveryOptimizationStatus not available, disabling permanently");
                    _permanentlyDisabled = true;
                    return null;
                }
                Logger.Warning($"[DeliveryOptimizationCollector] PS runtime error: {ex.Message}");
                HandleError(ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Invoke failed: {ex.Message}");
                DisposeRunspace();
                HandleError(ex.Message);
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // Result processing: progress events + final DO telemetry
        // -----------------------------------------------------------------------

        private void ProcessResults(List<PSObject> results, AppPackageStateList packageStates)
        {
            if (results.Count == 0)
            {
                Logger.Verbose("[DeliveryOptimizationCollector] No DO entries returned");
                return;
            }

            int progressCount = 0;
            int matchCount = 0;

            // Build JSONL snapshot for log (only if fingerprint changes)
            var logEntries = new JArray();

            foreach (var result in results)
            {
                var fileId = GetPropString(result, "FileId");
                if (string.IsNullOrEmpty(fileId)) continue;

                long fileSize = GetPropLong(result, "FileSize");
                long totalBytes = GetPropLong(result, "TotalBytesDownloaded");
                long bytesFromPeers = GetPropLong(result, "BytesFromPeers");
                int peerCachingPct = GetPropInt(result, "PercentPeerCaching");
                long bytesLanPeers = GetPropLong(result, "BytesFromLanPeers");
                long bytesGroupPeers = GetPropLong(result, "BytesFromGroupPeers");
                long bytesInternetPeers = GetPropLong(result, "BytesFromInternetPeers");
                long bytesLinkLocalPeers = GetPropLong(result, "BytesFromLinkLocalPeers");
                int downloadMode = GetPropInt(result, "DownloadMode", -1);
                long bytesFromHttp = GetPropLong(result, "BytesFromHttp");
                long bytesFromCacheServer = GetPropLong(result, "BytesFromCacheServer");
                var downloadDuration = GetPropTimeSpan(result, "DownloadDuration");
                var sourceUrl = GetPropString(result, "SourceURL");
                var cacheHost = GetPropUriString(result, "CacheHost");

                // Build log entry for JSONL
                logEntries.Add(new JObject
                {
                    ["FileId"] = fileId,
                    ["FileSize"] = fileSize,
                    ["TotalBytesDownloaded"] = totalBytes,
                    ["PercentPeerCaching"] = peerCachingPct,
                    ["BytesFromPeers"] = bytesFromPeers,
                    ["BytesFromHttp"] = bytesFromHttp,
                    ["DownloadMode"] = downloadMode,
                    ["SourceURL"] = sourceUrl,
                    ["BytesFromCacheServer"] = bytesFromCacheServer
                });

                // Try to match to an IME-tracked app
                var appId = ImeLogTracker.ExtractAppIdFromDoFileId(fileId);
                if (string.IsNullOrEmpty(appId)) continue;

                var pkg = packageStates.GetPackage(appId);
                if (pkg == null) continue;

                // --- Live download_progress (every poll where bytes changed) ---
                long lastBytes;
                _lastBytesPerApp.TryGetValue(appId, out lastBytes);

                if (totalBytes != lastBytes)
                {
                    _lastBytesPerApp[appId] = totalBytes;
                    progressCount++;

                    int percentComplete = fileSize > 0 ? (int)((totalBytes * 100) / fileSize) : 0;

                    Post.Emit(new EnrollmentEvent
                    {
                        SessionId = SessionId,
                        TenantId = TenantId,
                        EventType = Constants.EventTypes.DownloadProgress,
                        Severity = EventSeverity.Debug,
                        Source = "DeliveryOptimizationCollector",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"{pkg.Name ?? appId}: {percentComplete}% ({totalBytes}/{fileSize}) peers={peerCachingPct}% mode={downloadMode}",
                        Data = BuildDoEventData(
                            appId, pkg.Name, totalBytes, fileSize, percentComplete,
                            bytesFromPeers, bytesFromHttp, peerCachingPct, downloadMode,
                            bytesLanPeers, bytesGroupPeers, bytesInternetPeers, bytesLinkLocalPeers,
                            bytesFromCacheServer, cacheHost),
                        ImmediateUpload = true
                    });
                }

                // --- Final DO telemetry (once per app when download is complete) ---
                if (!pkg.HasDoTelemetry && !_enrichedAppIds.Contains(appId) &&
                    totalBytes >= fileSize && fileSize > 0)
                {
                    var durationStr = downloadDuration?.ToString(@"hh\:mm\:ss\.fff");

                    pkg.UpdateDoTelemetry(fileSize, totalBytes, bytesFromPeers, peerCachingPct,
                        bytesLanPeers, bytesGroupPeers, bytesInternetPeers,
                        downloadMode, durationStr, bytesFromHttp,
                        bytesFromLinkLocalPeers: bytesLinkLocalPeers,
                        bytesFromCacheServer: bytesFromCacheServer,
                        cacheHost: cacheHost);

                    _enrichedAppIds.Add(appId);
                    matchCount++;

                    Logger.Info($"[DeliveryOptimizationCollector] DO matched: {pkg.Name ?? appId} — " +
                                $"size={fileSize}, peers={bytesFromPeers} ({peerCachingPct}%), " +
                                $"http={bytesFromHttp}, mode={downloadMode}, duration={durationStr}");

                    // Fire callback → EnrollmentTracker emits do_telemetry + download_progress events
                    _onDoTelemetryReceived?.Invoke(pkg);
                }
            }

            // Write JSONL log only when overall state changed
            var fingerprint = ComputeFingerprint(logEntries);
            if (fingerprint != _lastSnapshotFingerprint)
            {
                _lastSnapshotFingerprint = fingerprint;
                WriteToLogFile(logEntries);
            }

            Logger.Verbose($"[DeliveryOptimizationCollector] Poll: {results.Count} entries, " +
                           $"{progressCount} progress updates, {matchCount} new matches ({_enrichedAppIds.Count} total enriched)");
        }

        // -----------------------------------------------------------------------
        // PSObject property helpers (safe extraction, no exceptions on missing props)
        // -----------------------------------------------------------------------

        private static string GetPropString(PSObject obj, string name)
        {
            return obj.Properties[name]?.Value?.ToString();
        }

        private static long GetPropLong(PSObject obj, string name, long defaultValue = 0)
        {
            var val = obj.Properties[name]?.Value;
            if (val == null) return defaultValue;
            try { return Convert.ToInt64(val); }
            catch { return defaultValue; }
        }

        private static int GetPropInt(PSObject obj, string name, int defaultValue = 0)
        {
            var val = obj.Properties[name]?.Value;
            if (val == null) return defaultValue;
            try { return Convert.ToInt32(val); }
            catch { return defaultValue; }
        }

        private static TimeSpan? GetPropTimeSpan(PSObject obj, string name)
        {
            var val = obj.Properties[name]?.Value;
            if (val is TimeSpan ts) return ts;
            return null;
        }

        // CacheHost is exposed by the cmdlet as System.Uri (or null when no MCC was used).
        // .ToString() returns the absolute URI form (e.g. "http://72.144.231.24/").
        // Returning null on absent or empty hosts so the event omits the field instead of "/".
        private static string GetPropUriString(PSObject obj, string name)
        {
            var val = obj.Properties[name]?.Value;
            if (val == null) return null;
            if (val is Uri uri) return uri.ToString();
            var s = val.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        // Builds the Data dictionary for download_progress events. Centralised so the
        // download_progress (in-flight) and (potentially future) periodic-snapshot emissions
        // stay symmetric on the breakdown / cache fields the UI now consumes.
        private static Dictionary<string, object> BuildDoEventData(
            string appId, string appName, long totalBytes, long fileSize, int percentComplete,
            long bytesFromPeers, long bytesFromHttp, int peerCachingPct, int downloadMode,
            long bytesLanPeers, long bytesGroupPeers, long bytesInternetPeers, long bytesLinkLocalPeers,
            long bytesFromCacheServer, string cacheHost)
        {
            var data = new Dictionary<string, object>
            {
                ["appId"] = appId,
                ["appName"] = appName,
                // UI-compatible fields (DownloadProgress.tsx reads these)
                ["bytesDownloaded"] = totalBytes,
                ["bytesTotal"] = fileSize,
                ["progressPercent"] = percentComplete,
                // DO-specific fields
                ["doFileSize"] = fileSize,
                ["doTotalBytesDownloaded"] = totalBytes,
                ["doPercentComplete"] = percentComplete,
                ["doBytesFromPeers"] = bytesFromPeers,
                ["doBytesFromHttp"] = bytesFromHttp,
                ["doPercentPeerCaching"] = peerCachingPct,
                ["doDownloadMode"] = downloadMode,
                // Per-source breakdown — the customer-visible bug was these being absent on
                // download_progress while present on do_telemetry, leaving the UI at 0/0/0 mid-flight.
                ["doBytesFromLanPeers"] = bytesLanPeers,
                ["doBytesFromGroupPeers"] = bytesGroupPeers,
                ["doBytesFromInternetPeers"] = bytesInternetPeers,
                ["doBytesFromLinkLocalPeers"] = bytesLinkLocalPeers,
                ["doBytesFromCacheServer"] = bytesFromCacheServer,
                ["doSource"] = "os_cmdlet"
            };
            if (!string.IsNullOrEmpty(cacheHost))
                data["doCacheHost"] = cacheHost;
            return data;
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private void HandleError(string error)
        {
            _consecutiveErrors++;

            if (_consecutiveErrors >= 5)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] {_consecutiveErrors} consecutive errors, disabling permanently. Last: {error}");
                _permanentlyDisabled = true;
                return;
            }

            Logger.Warning($"[DeliveryOptimizationCollector] Error ({_consecutiveErrors}/5): {error}");
        }

        private static long ComputeFingerprint(JArray entries)
        {
            long sum = entries.Count;
            foreach (var e in entries)
            {
                sum += e["TotalBytesDownloaded"]?.Value<long>() ?? 0;
                // Include FileId hash to detect entry additions/removals
                var fileId = e["FileId"]?.ToString();
                if (fileId != null)
                    sum += fileId.GetHashCode();
            }
            return sum;
        }

        private void WriteToLogFile(JArray entries)
        {
            try
            {
                var snapshot = new JObject
                {
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["entryCount"] = entries.Count,
                    ["entries"] = entries
                };

                var line = snapshot.ToString(Formatting.None) + Environment.NewLine;
                File.AppendAllText(_logFilePath, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Failed to write DO log: {ex.Message}");
            }
        }
    }
}
