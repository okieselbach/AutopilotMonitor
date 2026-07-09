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
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;

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

        // Office C2R DO support (Rev 3): the Office-CDN download is visible in DO long before the
        // OfficeC2RClient.exe worker appears, so we classify the non-IME Office-CDN jobs UNCONDITIONALLY
        // (no longer gated on the worker) and hand aggregated stats to the OfficeInstallDetector — which
        // folds them into the office_install_* events and treats the first sample as the start trigger.
        // Office is not an IME app, so we deliberately do NOT emit download_progress/do_telemetry for it
        // (that would create a phantom app in the backend AppInstallSummary).
        private readonly Action<OfficeDoSample> _onOfficeDoSample;

        // Keep-awake sources for an Office install (any keeps us polling): the worker process is up
        // (_officeActive), an Office-CDN job was seen in the last poll (_officeJobsSeenLastPoll), or the
        // registry hinted an install is imminent (_officeExpectedPolls — a bounded probe window so a
        // Scenario\INSTALL that never produces a download does not keep us awake forever).
        // Completion is NOT derived from the DO aggregate (unreliable: multi-job churn + Connected-Cache
        // — see OfficeBinaryWatcher); we only surface started + progress here.
        private volatile bool _officeActive;
        private bool _officeJobsSeenLastPoll;
        private int _officeExpectedPolls;

        // Bounded probe budget after a registry "Office expected" hint, in polls (~ this × interval).
        private const int OfficeExpectedProbePolls = 20;

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

        // Passive session-level bandwidth estimate: fed with EVERY DO job's byte counters
        // (IME apps, Office CDN, WU/Store/Defender — all real traffic) on each poll; reduced
        // to a network_bandwidth_estimate event. Emitted (at most) twice per session: an
        // interim snapshot when the ESP leaves DeviceSetup (the bulk of Win32 downloads is
        // done by then — keeps the estimate available for analysis even when the session
        // later starves in AccountSetup and the agent never stops cleanly) and the
        // authoritative final one when the collector stops at enrollment end.
        // Zero extra traffic/load — pure arithmetic on the existing poll.
        private readonly BandwidthEstimator _bandwidthEstimator;
        private readonly int _pollIntervalSeconds;
        private bool _bandwidthEstimateEmitted;
        private int _interimBandwidthEmitted; // Interlocked guard — phase callback runs on the IME tracker thread

        // Restart persistence for the estimator accumulator (one enrollment spans several
        // reboots; without it a mid-DeviceSetup reboot discards all samples collected so far).
        // Null-tolerant: tests and callers without a state directory simply run in-memory-only.
        private readonly BandwidthStatePersistence _bandwidthStatePersistence;

        public DeliveryOptimizationCollector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int intervalSeconds,
            Func<AppPackageStateList> getPackageStates,
            Action<AppPackageState> onDoTelemetryReceived,
            string logDirectory,
            Action<OfficeDoSample> onOfficeDoSample = null,
            BandwidthStatePersistence bandwidthStatePersistence = null)
            : base(sessionId, tenantId, post, logger, intervalSeconds)
        {
            _getPackageStates = getPackageStates ?? throw new ArgumentNullException(nameof(getPackageStates));
            _onDoTelemetryReceived = onDoTelemetryReceived;
            _logFilePath = Path.Combine(logDirectory, LogFileName);
            _onOfficeDoSample = onOfficeDoSample;
            _pollIntervalSeconds = intervalSeconds;
            _bandwidthEstimator = new BandwidthEstimator(intervalSeconds);
            _bandwidthStatePersistence = bandwidthStatePersistence;

            // Resume the accumulator from a previous run of the SAME session (reboot survivor):
            // samples and byte counters carry over, and the interim once-guard stays claimed so
            // device_setup_end fires once per session, not once per process.
            var persisted = _bandwidthStatePersistence?.Load(sessionId);
            if (persisted != null)
            {
                _bandwidthEstimator.ImportState(persisted.ToEstimatorState());
                if (persisted.InterimEmitted) _interimBandwidthEmitted = 1;
                Logger.Info($"[DeliveryOptimizationCollector] Resumed bandwidth state from previous run — " +
                            $"{persisted.WanSamplesMbps?.Count ?? 0} WAN samples / {persisted.WanBytesObserved} bytes, " +
                            $"interimEmitted={persisted.InterimEmitted}");
            }
        }

        /// <summary>Start dormant — timer does not fire until WakeUp() is called.</summary>
        protected override TimeSpan GetInitialDelay() => Timeout.InfiniteTimeSpan;

        protected override void OnBeforeStart()
        {
            EnsureRunspace();
        }

        protected override void OnAfterStop()
        {
            // Emit before the runspace teardown: the termination handler stops peripheral
            // collectors BEFORE the diagnostics ZIP is built, so this one-shot still lands
            // ahead of diagnostics_collecting in the timeline.
            EmitBandwidthEstimateOnce();
            SaveBandwidthState();
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

        /// <summary>
        /// Office C2R wake source: the OfficeProcessWatcher reports an Office worker started/stopped.
        /// While active, the collector keeps polling (even with no IME downloads) so it can capture
        /// Office's DO jobs. Called from any thread.
        /// </summary>
        public void NotifyOfficeActive(bool active)
        {
            _officeActive = active;
            if (active)
            {
                Logger.Info("[DeliveryOptimizationCollector] Office install active — sampling DO for Office CDN jobs");
                WakeUp();
            }
        }

        /// <summary>
        /// Office C2R early wake source (Rev 3): the RegistryChangeWatcher observed a
        /// <c>…\ClickToRun\Scenario\INSTALL</c> key — an Office install is imminent, possibly before any
        /// IME download or worker process. Wakes the collector for a bounded probe window so it can
        /// catch the Office-CDN DO job at the very start of the download. Called from any thread.
        /// </summary>
        public void NotifyOfficeExpected()
        {
            // Re-open the bounded probe window. The registry watcher can fire many times per second on
            // ClickToRun value churn, so only log (at Debug) when the window actually (re)opens from
            // closed — never a per-fire INFO line (field session 7da7dead logged ~4000 of them).
            bool windowWasClosed = _officeExpectedPolls <= 0;
            _officeExpectedPolls = OfficeExpectedProbePolls;
            if (windowWasClosed)
                Logger.Debug("[DeliveryOptimizationCollector] Office install expected (registry Scenario\\INSTALL) — probing DO for Office CDN jobs");
            WakeUp();
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

            // Guard: check if there's still work to do. Office C2R has no IME packages, so an active
            // Office install (from the OfficeProcessWatcher) keeps the collector polling on its own.
            var packageStates = _getPackageStates();

            var hasActiveDownloads = false;
            var hasPendingEnrichment = false;
            if (packageStates != null)
            {
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
            }

            // Office keep-awake: worker up, an Office-CDN job seen last poll, or a bounded registry-hint
            // probe window still open. This lets us detect Office's DO download independent of the
            // (late, transient) OfficeC2RClient.exe worker.
            var officeKeepAwake = _officeActive || _officeJobsSeenLastPoll || _officeExpectedPolls > 0;

            if (!hasActiveDownloads && !hasPendingEnrichment && !officeKeepAwake)
            {
                Logger.Info("[DeliveryOptimizationCollector] Going dormant — no active downloads, pending enrichment or Office install");
                _dormant = true;
                PauseTimer();
                // Natural persistence point: all downloads are done — which is exactly when an
                // app-forced reboot becomes possible. Saves the accumulated samples so a reboot
                // before the interim/final emission does not discard them (reboot survivor).
                SaveBandwidthState();
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

            // Office C2R DO aggregation across all Office-CDN jobs in this poll.
            int officeJobCount = 0;
            long officeFileSize = 0, officeTotalBytes = 0, officeBytesFromPeers = 0,
                 officeBytesFromHttp = 0, officeBytesFromCacheServer = 0;
            int officeDownloadMode = -1;

            // Build JSONL snapshot for log (only if fingerprint changes)
            var logEntries = new JArray();

            // Every DO job feeds the passive bandwidth estimate — including non-IME,
            // non-Office jobs (WU, Store, Defender): all of it is real line traffic.
            var bandwidthJobs = new List<BandwidthJobSample>(results.Count);

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

                bandwidthJobs.Add(new BandwidthJobSample
                {
                    FileId = fileId,
                    WanBytes = bytesFromHttp + bytesInternetPeers,
                    LanBytes = bytesLanPeers + bytesGroupPeers + bytesLinkLocalPeers + bytesFromCacheServer
                });

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
                var pkg = (!string.IsNullOrEmpty(appId) && packageStates != null)
                    ? packageStates.GetPackage(appId)
                    : null;
                if (pkg == null)
                {
                    // Not an IME app. Accumulate Office-CDN jobs UNCONDITIONALLY (Rev 3) for the
                    // OfficeInstallDetector (folded into office_install_*; NOT emitted as download_progress
                    // to avoid a phantom app in the backend AppInstallSummary). No longer gated on the
                    // worker process — the download is visible here long before OfficeC2RClient.exe runs.
                    if (IsOfficeCdnJob(sourceUrl))
                    {
                        officeJobCount++;
                        officeFileSize += fileSize;
                        officeTotalBytes += totalBytes;
                        officeBytesFromPeers += bytesFromPeers;
                        officeBytesFromHttp += bytesFromHttp;
                        officeBytesFromCacheServer += bytesFromCacheServer;
                        if (downloadMode >= 0) officeDownloadMode = downloadMode;
                    }
                    continue;
                }

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

            // Hand aggregated Office DO stats to the OfficeInstallDetector (folded into office_install_*).
            // Unconditional (Rev 3): the first sample with jobs is the detector's start trigger; later
            // samples fold a real download-% into progress. Completion is handled separately by the
            // OfficeBinaryWatcher (the DO aggregate is unreliable for it), so we do NOT signal "ended".
            var officeJobsThisPoll = officeJobCount > 0;

            if (officeJobsThisPoll && _onOfficeDoSample != null)
            {
                int officePeerPct = officeTotalBytes > 0 ? (int)((officeBytesFromPeers * 100) / officeTotalBytes) : 0;
                try
                {
                    _onOfficeDoSample(new OfficeDoSample
                    {
                        JobCount = officeJobCount,
                        FileSize = officeFileSize,
                        TotalBytesDownloaded = officeTotalBytes,
                        BytesFromPeers = officeBytesFromPeers,
                        BytesFromHttp = officeBytesFromHttp,
                        BytesFromCacheServer = officeBytesFromCacheServer,
                        PercentPeerCaching = officePeerPct,
                        DownloadMode = officeDownloadMode,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[DeliveryOptimizationCollector] onOfficeDoSample threw: {ex.Message}");
                }
            }

            _officeJobsSeenLastPoll = officeJobsThisPoll;
            // Decrement the bounded registry-hint probe window only while no Office job is being seen.
            if (!officeJobsThisPoll && _officeExpectedPolls > 0) _officeExpectedPolls--;

            _bandwidthEstimator.AddSnapshot(DateTime.UtcNow, bandwidthJobs);

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

        // Office C2R content CDN hosts — version-independent (a FileId build marker like "_16_0_" would
        // break on older/newer Office versions). Field-validated against session 8353e03b: C2R uses BOTH
        // the primary CDN officecdn.microsoft.com (registry OriginalCDNDomain) and the content CDN
        // f.c2r.ts.cdn.office.net (registry FailoverDomain / PreferredCDNPrefix).
        private static readonly string[] OfficeCdnHosts = { "cdn.office.net", "officecdn.microsoft.com" };

        /// <summary>
        /// True when a DO job's SourceURL points at an Office C2R content CDN (not an IME Win32 app).
        /// Matched purely by the stable CDN host so it survives Office version changes.
        /// </summary>
        private static bool IsOfficeCdnJob(string sourceUrl)
        {
            if (string.IsNullOrEmpty(sourceUrl)) return false;
            foreach (var host in OfficeCdnHosts)
                if (sourceUrl.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // -----------------------------------------------------------------------
        // Passive bandwidth estimate (one-shot at collector stop)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Interim snapshot trigger: the IME tracker reports an ESP phase. On the FIRST
        /// AccountSetup sighting (= DeviceSetup, and with it the bulk of Win32 downloads, is
        /// over) the current estimate is emitted with snapshotTrigger=device_setup_end — so
        /// an analysis still has a bandwidth figure when the session later starves in
        /// AccountSetup and the final collector-stop emission never happens. The tracker
        /// invokes the callback on every phase RE-match too (IME re-logs the phase string
        /// periodically), hence the Interlocked once-guard. Called from the tracker thread.
        /// </summary>
        public void NotifyEspPhaseChanged(string phase)
        {
            if (!"AccountSetup".Equals(phase, StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.CompareExchange(ref _interimBandwidthEmitted, 1, 0) != 0) return;
            EmitBandwidthEstimate("device_setup_end");
            // Persist AFTER the emission (StartupEventGate's MarkEmitted ordering): a crash
            // between emit and save re-emits the interim on the next run instead of silently
            // suppressing it for the rest of the session. A rare duplicate is harmless —
            // analyses take the latest event per trigger.
            SaveBandwidthState();
        }

        /// <summary>
        /// Persists the estimator accumulator + interim guard (no-op without a persistence
        /// instance). Called at the dormant transition, after the interim emission and at
        /// stop — never per-poll. Fail-soft: Save never throws.
        /// </summary>
        private void SaveBandwidthState()
        {
            if (_bandwidthStatePersistence == null) return;
            var state = _bandwidthEstimator.ExportState();
            _bandwidthStatePersistence.Save(new BandwidthStateData
            {
                SessionId = SessionId,
                SavedAtUtc = DateTime.UtcNow,
                InterimEmitted = _interimBandwidthEmitted != 0,
                WanSamplesMbps = state.WanSamplesMbps,
                LanSamplesMbps = state.LanSamplesMbps,
                WanBytesObserved = state.WanBytesObserved,
                LanBytesObserved = state.LanBytesObserved,
            });
        }

        /// <summary>
        /// Emits the authoritative session-level network_bandwidth_estimate once, at collector
        /// stop. Guarded against the double OnAfterStop from Stop() + Dispose().
        /// </summary>
        private void EmitBandwidthEstimateOnce()
        {
            if (_bandwidthEstimateEmitted) return;
            _bandwidthEstimateEmitted = true;
            EmitBandwidthEstimate("collector_stop");
        }

        /// <summary>
        /// Builds and emits one network_bandwidth_estimate event. Silently skipped when no
        /// valid rate sample exists yet (e.g. no DO-tracked downloads happened so far).
        /// </summary>
        private void EmitBandwidthEstimate(string trigger)
        {
            try
            {
                var estimate = _bandwidthEstimator.TryBuildEstimate();
                if (estimate == null)
                {
                    Logger.Info($"[DeliveryOptimizationCollector] No bandwidth estimate at {trigger} — no valid DO rate samples yet");
                    return;
                }

                var data = new Dictionary<string, object>
                {
                    ["wanSampleCount"] = estimate.WanSampleCount,
                    ["wanBytesObserved"] = estimate.WanBytesObserved,
                    ["lanSampleCount"] = estimate.LanSampleCount,
                    ["lanBytesObserved"] = estimate.LanBytesObserved,
                    ["bandwidthBucket"] = estimate.Bucket,
                    ["confidence"] = estimate.Confidence,
                    ["pollIntervalSeconds"] = _pollIntervalSeconds,
                    ["snapshotTrigger"] = trigger,
                    ["doSource"] = "os_cmdlet"
                };
                if (estimate.WanMbpsP90.HasValue)
                {
                    data["estimatedWanMbps"] = Math.Round(estimate.WanMbpsP90.Value, 1);
                    data["wanMbpsMax"] = Math.Round(estimate.WanMbpsMax.Value, 1);
                }
                if (estimate.LanMbpsP90.HasValue)
                {
                    data["estimatedLanMbps"] = Math.Round(estimate.LanMbpsP90.Value, 1);
                    data["lanMbpsMax"] = Math.Round(estimate.LanMbpsMax.Value, 1);
                }

                // DO throttle-policy context (read fresh — Intune policies land DURING the
                // enrollment): lets an analysis distinguish "line-limited" (no caps → the p90
                // is a real capacity lower bound) from "policy-limited" (caps present → the
                // measured rate reflects the throttle, the line is likely faster). GPO and MDM
                // are SEPARATE registry stores; both are reported when present.
                var throttle = DoThrottlePolicyReader.Read();
                data["doThrottleConfigured"] = throttle.ThrottleConfigured;
                if (throttle.ThrottleSources != null) data["doThrottleSources"] = throttle.ThrottleSources;
                if (throttle.GpoMaxForegroundKBps.HasValue) data["doPolicyGpoMaxForegroundKBps"] = throttle.GpoMaxForegroundKBps.Value;
                if (throttle.GpoMaxBackgroundKBps.HasValue) data["doPolicyGpoMaxBackgroundKBps"] = throttle.GpoMaxBackgroundKBps.Value;
                if (throttle.GpoPctMaxForeground.HasValue) data["doPolicyGpoPctMaxForeground"] = throttle.GpoPctMaxForeground.Value;
                if (throttle.GpoPctMaxBackground.HasValue) data["doPolicyGpoPctMaxBackground"] = throttle.GpoPctMaxBackground.Value;
                if (throttle.GpoDownloadMode.HasValue) data["doPolicyGpoDownloadMode"] = throttle.GpoDownloadMode.Value;
                if (throttle.MdmMaxForegroundKBps.HasValue) data["doPolicyMdmMaxForegroundKBps"] = throttle.MdmMaxForegroundKBps.Value;
                if (throttle.MdmMaxBackgroundKBps.HasValue) data["doPolicyMdmMaxBackgroundKBps"] = throttle.MdmMaxBackgroundKBps.Value;
                if (throttle.MdmPctMaxForeground.HasValue) data["doPolicyMdmPctMaxForeground"] = throttle.MdmPctMaxForeground.Value;
                if (throttle.MdmPctMaxBackground.HasValue) data["doPolicyMdmPctMaxBackground"] = throttle.MdmPctMaxBackground.Value;
                if (throttle.MdmDownloadMode.HasValue) data["doPolicyMdmDownloadMode"] = throttle.MdmDownloadMode.Value;

                var interimSuffix = trigger == "device_setup_end" ? " [interim, after DeviceSetup]" : "";
                if (throttle.ThrottleConfigured) interimSuffix += " [DO throttled by policy]";
                string message;
                if (estimate.WanMbpsP90.HasValue)
                {
                    message = $"Estimated internet bandwidth ~{estimate.WanMbpsP90.Value:0.#} Mbit/s " +
                              $"(bucket {estimate.Bucket}, confidence {estimate.Confidence}; " +
                              $"{estimate.WanSampleCount} samples / {estimate.WanBytesObserved / (1024 * 1024)} MB via internet)" +
                              interimSuffix;
                }
                else
                {
                    // All observed download traffic came from LAN peers / Connected Cache —
                    // the internet line was never exercised enough to estimate it.
                    message = $"No internet-path bandwidth estimate — downloads were LAN/cache-fed " +
                              $"(~{estimate.LanMbpsP90.Value:0.#} Mbit/s from LAN, " +
                              $"{estimate.LanBytesObserved / (1024 * 1024)} MB observed)" +
                              interimSuffix;
                }

                Post.Emit(new EnrollmentEvent
                {
                    SessionId = SessionId,
                    TenantId = TenantId,
                    EventType = Constants.EventTypes.NetworkBandwidthEstimate,
                    Severity = EventSeverity.Info,
                    Source = "DeliveryOptimizationCollector",
                    Phase = EnrollmentPhase.Unknown,
                    Message = message,
                    Data = data,
                    ImmediateUpload = true
                });

                Logger.Info($"[DeliveryOptimizationCollector] Bandwidth estimate emitted: {message}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Bandwidth estimate emission failed: {ex.Message}");
            }
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
