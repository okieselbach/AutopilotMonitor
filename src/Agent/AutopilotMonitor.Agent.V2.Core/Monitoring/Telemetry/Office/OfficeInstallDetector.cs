#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Surfaces the REAL Microsoft 365 Apps (Office Click-to-Run / C2R) background install lifecycle
    /// — even when the Intune "integrated" Office app reports done to IME within 1-2 minutes while C2R
    /// keeps streaming/laying down the product for many more minutes.
    /// <para>
    /// <b>Event-driven, no idle polling</b> (Rev 3). This class is the pure decision core; the
    /// <c>OfficeInstallDetectorHost</c> owns the OS watchers and drives it. The lifecycle anchor is no
    /// longer the late, transient <c>OfficeC2RClient.exe</c> worker — whichever Office signal arrives
    /// first opens the single install window (idempotent):
    /// <list type="bullet">
    ///   <item><see cref="OnOfficeDoSample"/> — aggregated Office-CDN Delivery-Optimization stats. The
    ///     download is visible here long before the worker process, so the first sample carrying jobs is
    ///     the EARLIEST start trigger; later samples fold a real download-% into progress.</item>
    ///   <item><see cref="OnRegistryChanged"/> — <c>RegNotifyChangeKeyValue</c> push on the ClickToRun
    ///     key. When idle and a <c>Scenario\INSTALL</c> key is present → start; when active → progress on
    ///     real movement only (no heartbeat) or an explicit error code.</item>
    ///   <item><see cref="OnWorkerStarted"/> — Office C2R worker started (WMI push / startup probe); an
    ///     idempotent start trigger (no-op if the lifecycle already began).</item>
    ///   <item><see cref="TryFinalizeCompletion"/> — called by the host after the download ended AND the
    ///     worker is gone, on a bounded probe schedule. Completion is proven by the core Office binaries
    ///     on disk under <c>{InstallationPath}\root\*</c> (the integrate phase lays them down after the
    ///     stream ends). An explicit error code → failed; no proof after the probe budget →
    ///     <see cref="AbandonSilently"/> (no false terminal).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Registry reads use the forced Registry64 view (AnyCPU net48 may otherwise read the stale
    /// WOW6432Node mirror). One install lifecycle per detector (latches Terminal); a later C2R update
    /// in the same session is out of scope for v1.
    /// </para>
    /// </summary>
    public class OfficeInstallDetector
    {
        internal const string SourceName = "OfficeInstallDetector";
        internal const string AppName = "Microsoft 365 Apps";

        private const string ConfigurationSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        private const string ScenarioSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Scenario";

        // Value-name substrings scanned (case-insensitive) for a best-effort failure code in the
        // undocumented Scenario value set. Deliberately NARROW (no bare "result" — that would match a
        // benign "Result=Success"); a hit is only treated as a failure when the VALUE is a non-zero
        // numeric/hex code (see IsNonZeroNumericCode). TODO(office-followup): validate the real value
        // names against a field enrollment and widen only if needed.
        private static readonly string[] ErrorValueHints = { "errorcode", "hresult", "exitcode", "lasterror", "failurecode" };

        // Install-class C2R scenario subkey names. Only these anchor an error code (a failure in a
        // CLIENTUPDATE / UPDATE / maintenance scenario must not fail an install). Deliberately narrow
        // — the Autopilot enrollment cares about the fresh product lay-down, not client self-updates.
        private static readonly string[] InstallScenarioNames = { "INSTALL" };

        private static bool IsInstallScenario(string? name)
            => name != null && InstallScenarioNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

        // Core Office app binaries used as the on-disk completion proof. ANY of these present under
        // {InstallationPath}\root\* means the C2R lay-down finished — "any of", because a deployment can
        // exclude products (e.g. no Outlook), so requiring all would miss legitimate completions.
        internal static readonly string[] CoreBinaries = { "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "OUTLOOK.EXE" };

        // While Active, a grown DO peak is re-persisted at most this often — keeps the doSummary
        // data of a mid-install restart fresh without writing the state file on every 3s DO poll.
        private const int PeakPersistThrottleSeconds = 30;

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly IClock _clock;
        private readonly Action<string?>? _onInstallationPathObserved;
        private readonly OfficeInstallStatePersistence? _statePersistence;
        private readonly object _lock = new object();
        private bool _pathObservedRaised;
        private DateTime? _lastStatePersistUtc;

        // Test seam: when set, used instead of reading the live registry/process state. Production
        // never assigns it. Surfaced to the test assembly via InternalsVisibleTo.
        internal Func<OfficeC2RSnapshot>? SnapshotProvider { get; set; }

        // Test seam: when set, used instead of the real filesystem check for the core-binary completion
        // proof. Receives the InstallationPath; returns true when a core Office binary exists on disk.
        internal Func<string?, bool>? CoreBinariesProbe { get; set; }

        private enum DetectorState { Idle, Active, Terminal }

        /// <summary>Outcome of a completion attempt — drives the host's bounded post-download probe.</summary>
        internal enum CompletionOutcome { Completed, Failed, NotYet }

        private DetectorState _state = DetectorState.Idle;
        private bool _preinstalledReported; // office_preinstalled_detected already emitted (emit-once guard)
        private DateTime? _startedAtUtc;
        private string? _startedTrigger;
        private OfficeDoSample? _lastDo;
        private OfficeDoSample? _peakDo; // highest-bytes sample seen — basis for the completed doSummary

        public OfficeInstallDetector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IClock clock,
            Action<string?>? onInstallationPathObserved = null,
            OfficeInstallStatePersistence? statePersistence = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _onInstallationPathObserved = onInstallationPathObserved;
            _statePersistence = statePersistence;
        }

        /// <summary>
        /// Restores a persisted open lifecycle after an agent restart (an enrollment commonly spans
        /// several reboots): started was already emitted in a previous run, so none is re-emitted
        /// here. The original start time and DO peak are restored, so a completion proven in THIS
        /// run reports the true total duration and doSummary. A fresh snapshot is read to surface
        /// the InstallationPath, which lets the host arm the binary watcher — when Office finished
        /// installing while the agent was down, that completes the lifecycle synchronously.
        /// </summary>
        internal void ResumeActive(OfficeInstallStateData state)
        {
            if (state == null || state.State != OfficeInstallStateData.StateActive) return;
            lock (_lock)
            {
                if (_state != DetectorState.Idle) return;
                _state = DetectorState.Active;
                _startedAtUtc = state.StartedAtUtc ?? _clock.UtcNow;
                _startedTrigger = state.StartedTrigger;
                if (state.PeakDo != null) _peakDo = state.PeakDo.ToSample();
                _logger.Info($"[{SourceName}] resumed open install lifecycle from persisted state (started {_startedAtUtc:O}, trigger={_startedTrigger ?? "?"})");

                var snap = ReadSnapshotSafe();
                if (snap != null) ObserveInstallationPath(snap);
            }
        }

        /// <summary>
        /// Restores the "preinstalled already reported" guard after an agent restart so resident inbox
        /// Office does not re-emit <c>office_preinstalled_detected</c> every reboot — while leaving the
        /// detector Idle/armed so a later fresh (enterprise) install in this enrollment is still caught.
        /// </summary>
        internal void ResumePreinstalled(OfficeInstallStateData state)
        {
            if (state == null || state.State != OfficeInstallStateData.StatePreinstalled) return;
            lock (_lock)
            {
                if (_state != DetectorState.Idle) return;
                _preinstalledReported = true;
                _logger.Info($"[{SourceName}] resumed preinstalled-reported guard (armed for a later fresh install)");
            }
        }

        /// <summary>
        /// Surfaces the C2R <c>InstallationPath</c> to the host (once) as soon as it is known, so the
        /// host can arm the <see cref="OfficeBinaryWatcher"/> on the correct tree. Caller holds the lock.
        /// </summary>
        private void ObserveInstallationPath(OfficeC2RSnapshot snap)
        {
            if (_pathObservedRaised || string.IsNullOrEmpty(snap.InstallationPath)) return;
            _pathObservedRaised = true;
            try { _onInstallationPathObserved?.Invoke(snap.InstallationPath); }
            catch (Exception ex) { _logger.Warning($"[{SourceName}] onInstallationPathObserved threw: {ex.Message}"); }
        }

        // -----------------------------------------------------------------------
        // Event entry points (driven by the host's process + registry watchers)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Office C2R worker (OfficeC2RClient.exe) started. In Rev 3 this is no longer the primary
        /// anchor — the worker is a late, transient launcher (it ran ~10 min AFTER the real download
        /// began in field sessions 8353e03b / 58d52632). It is one of three idempotent start triggers;
        /// whichever fires first opens the single install window.
        /// </summary>
        public void OnWorkerStarted()
        {
            lock (_lock)
            {
                if (_state != DetectorState.Idle) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) return;
                BeginIfIdle("process", snap);
            }
        }

        /// <summary>
        /// ClickToRun registry changed. When idle and a Scenario subkey (INSTALL/…) is present, this is
        /// the earliest registry start signal; when already active it drives progress or surfaces an
        /// explicit error code.
        /// </summary>
        public void OnRegistryChanged()
        {
            lock (_lock)
            {
                if (_state == DetectorState.Terminal) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) return;

                if (_state == DetectorState.Idle)
                {
                    // Start from the registry ONLY for an install-class scenario. A CLIENTUPDATE / UPDATE
                    // scenario key (the C2R client self-update / maintenance) must not open an install
                    // lifecycle. The early DO-job trigger (OnOfficeDoSample) still catches a real install
                    // before its INSTALL key is written — the common case (47% of field starts).
                    if (snap.InstallScenarioPresent) BeginIfIdle("registry", snap);
                    return;
                }
                EvaluateActive(snap);
            }
        }

        /// <summary>
        /// Latest aggregated Office DO stats. The first sample carrying jobs is the EARLIEST and most
        /// reliable start trigger (the Office-CDN download is visible long before the worker process).
        /// Later samples do NOT emit progress events — a per-sample download-% is noise for Office
        /// (Connected-Cache delivery is near-instant and the multi-job aggregate never yields a clean
        /// 0→100 bar — field session 7da7dead). They only update the DO data that is summarized once on
        /// the completed event (bytes + Cache-Server / Peer / CDN split + duration).
        /// </summary>
        public void OnOfficeDoSample(OfficeDoSample sample)
        {
            if (sample == null) return;
            lock (_lock)
            {
                if (_state == DetectorState.Terminal) return;

                if (_state == DetectorState.Idle)
                {
                    if (sample.JobCount <= 0) return; // a zero-job sample is not a start signal
                    var snapStart = ReadSnapshotSafe();
                    if (snapStart == null) return;
                    BeginIfIdle("do", snapStart);
                    // Seed the DO summary from the triggering sample ONLY if a real lifecycle actually
                    // opened. A preinstalled short-circuit stays Idle — capturing its CLIENTUPDATE bytes
                    // here would leak them into a LATER real install's doSummary (which is built from
                    // _peakDo). The seed is persisted directly (it is the lifecycle's first DO sample).
                    if (_state == DetectorState.Active)
                    {
                        _lastDo = sample;
                        _peakDo = sample;
                        PersistState(OfficeInstallStateData.StateActive);
                    }
                    return;
                }

                // Active: no progress event — the sample only updates the DO summary data. A grown
                // peak is re-persisted (throttled) so a mid-install agent restart keeps the doSummary.
                _lastDo = sample;
                var peakGrew = _peakDo == null || sample.TotalBytesDownloaded > _peakDo.TotalBytesDownloaded;
                if (peakGrew) _peakDo = sample;
                if (peakGrew && _statePersistence != null
                    && (!_lastStatePersistUtc.HasValue
                        || (_clock.UtcNow - _lastStatePersistUtc.Value).TotalSeconds >= PeakPersistThrottleSeconds))
                {
                    PersistState(OfficeInstallStateData.StateActive);
                }
            }
        }

        /// <summary>
        /// Attempt to finalize the lifecycle after the install window has closed (download ended AND the
        /// worker is gone — orchestrated by the host with a settle/probe schedule). Completion is proven
        /// by the core Office binaries existing on disk under <c>{InstallationPath}\root\*</c> (C2R lays
        /// them down in the integrate phase, which can lag the download end), so the host calls this on a
        /// bounded retry schedule. An explicit error code finalizes as failed. While neither holds, the
        /// outcome is <see cref="CompletionOutcome.NotYet"/> and the lifecycle stays open.
        /// </summary>
        internal CompletionOutcome TryFinalizeCompletion()
        {
            lock (_lock)
            {
                if (_state != DetectorState.Active) return CompletionOutcome.NotYet;

                var snap = ReadSnapshotSafe();
                if (snap == null) return CompletionOutcome.NotYet;

                if (!string.IsNullOrEmpty(snap.ErrorCode))
                {
                    FinalizeFailed(snap);
                    return CompletionOutcome.Failed;
                }

                if (ProbeCoreBinaries(snap.InstallationPath))
                {
                    _state = DetectorState.Terminal;
                    EmitLifecycle(Constants.EventTypes.OfficeInstallCompleted, snap, EventSeverity.Info, "Completed", isTerminal: true, coreBinariesPresent: true);
                    PersistState(OfficeInstallStateData.StateCompleted);
                    return CompletionOutcome.Completed;
                }

                return CompletionOutcome.NotYet;
            }
        }

        /// <summary>
        /// Stop tracking without emitting a terminal event. Called by the host when the bounded
        /// completion-probe schedule is exhausted with no on-disk proof — conservative: we never emit a
        /// false completed/failed when we cannot positively verify the outcome.
        /// </summary>
        public void AbandonSilently()
        {
            lock (_lock)
            {
                if (_state == DetectorState.Active)
                {
                    _state = DetectorState.Terminal;
                    _logger.Info($"[{SourceName}] install window closed without on-disk completion proof — stopping silently (no terminal event)");
                }
            }
        }

        // -----------------------------------------------------------------------
        // State machine
        // -----------------------------------------------------------------------

        /// <summary>Opens the single install window from whichever signal arrives first. Caller holds the lock.</summary>
        private void BeginIfIdle(string trigger, OfficeC2RSnapshot snap)
        {
            if (_state != DetectorState.Idle) return;

            // Already-resident detection — the precision gate. When the core Office binaries are
            // ALREADY on disk at the very first signal, this C2R activity is NOT a fresh install:
            // Office was laid down before the detector opened. Two flavours:
            //   * consumer / OEM-inbox product set → surface an informational office_preinstalled_detected
            //     (the C2R client running a background CLIENTUPDATE on pre-provisioned Office — field
            //     session fa526757), NOT a started/failed pair.
            //   * enterprise product set → fall through to the existing started→completed path (Office
            //     CSP / Win32-wrapper re-run on already-present Office — field session a7525e97).
            // Anchored on the filesystem, NOT the StreamingFinished registry value (absent on some
            // machines → reads false), so it holds even when that value is missing.
            //
            // Crucially this does NOT latch a terminal: an enrollment commonly UNINSTALLS the inbox
            // Office and lays down a fresh (enterprise) Microsoft 365 Apps afterwards, which we must
            // still report. So we emit the informational fact ONCE (idempotent guard) and stay Idle —
            // a later fresh install (enterprise product set, or a real download with no binaries on
            // disk yet) opens a normal started→completed lifecycle from here.
            //
            // Accepted risk (L18, delta review 2026-07-02): while inbox binaries are on disk AND
            // ProductReleaseIds still lists only consumer SKUs, every start trigger short-circuits
            // here. An enterprise install that fails BEFORE C2R rewrites ProductReleaseIds to a
            // managed SKU (or before the inbox binaries are removed) therefore never opens a
            // lifecycle and its INSTALL-scenario error is silently missed. This is silence, never
            // a false office_install_failed — the trade-off was chosen deliberately over risking
            // false failure verdicts on routine inbox-Office CLIENTUPDATE churn (session fa526757).
            if (ProbeCoreBinaries(snap.InstallationPath)
                && OfficeProductClassifier.IsConsumerInboxProductSet(snap.Products))
            {
                if (!_preinstalledReported)
                {
                    _preinstalledReported = true;
                    _startedAtUtc = _clock.UtcNow;
                    _startedTrigger = trigger;
                    EmitPreinstalled(snap);
                    PersistState(OfficeInstallStateData.StatePreinstalled);
                }
                return; // stay Idle (armed) — never open an install lifecycle for resident inbox Office
            }

            _state = DetectorState.Active;
            _startedAtUtc = _clock.UtcNow;
            _startedTrigger = trigger;
            // Emit started BEFORE surfacing the InstallationPath. The host arms the binary watcher in
            // that callback, and when Office is ALREADY on disk (installed via the Office CSP / a Win32
            // wrapper that re-runs C2R) the watcher's initial scan completes the lifecycle synchronously.
            // Emitting started first guarantees the correct order (started → completed); otherwise a
            // pre-installed Office produced completed-before-started (field session a7525e97).
            EmitLifecycle(Constants.EventTypes.OfficeInstallStarted, snap, EventSeverity.Info, PhaseOf(snap), isTerminal: false);
            // Persist Active BEFORE ObserveInstallationPath: when Office is already on disk the
            // host's binary watcher completes the lifecycle synchronously from inside that callback
            // (and persists Completed) — persisting Active afterwards would overwrite the terminal.
            PersistState(OfficeInstallStateData.StateActive);
            ObserveInstallationPath(snap);
        }

        private void EvaluateActive(OfficeC2RSnapshot snap)
        {
            // Surface a late-arriving InstallationPath (e.g. when started via a DO job before the
            // Configuration key was populated) so the host can arm the binary watcher.
            ObserveInstallationPath(snap);

            // An explicit numeric error code is the one in-flight terminal we trust. There is no progress
            // event — started + completed/failed only (a per-poll download-% is noise for Office).
            if (!string.IsNullOrEmpty(snap.ErrorCode))
                FinalizeFailed(snap);
        }

        private void FinalizeFailed(OfficeC2RSnapshot snap)
        {
            if (_state == DetectorState.Terminal) return;
            _state = DetectorState.Terminal;
            EmitLifecycle(Constants.EventTypes.OfficeInstallFailed, snap, EventSeverity.Error, "Failed", isTerminal: true);
            PersistState(OfficeInstallStateData.StateFailed);
        }

        /// <summary>
        /// Emits the informational <c>office_preinstalled_detected</c> for an Office that was already
        /// resident on disk at the first signal (OEM/consumer inbox Office running a background
        /// CLIENTUPDATE/maintenance scenario). This is NOT an enrollment install — Info severity, no
        /// started/failed pair. The caller has set the emit-once guard + start metadata and deliberately
        /// leaves the detector Idle/armed (no terminal latch) so a later fresh install is still detected.
        /// </summary>
        private void EmitPreinstalled(OfficeC2RSnapshot snap)
        {
            var data = BuildPayload(snap, "Preinstalled", isTerminal: true);
            if (!string.IsNullOrEmpty(_startedTrigger)) data["startedTrigger"] = _startedTrigger!;
            // Why we classified it as preinstalled — core binaries already on disk at first signal.
            data["reason"] = "office_already_resident";

            var product = snap.Products != null && snap.Products.Count > 0 ? string.Join(",", snap.Products) : AppName;
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.OfficePreinstalledDetected,
                Severity = EventSeverity.Info,
                Source = SourceName,
                Phase = EnrollmentPhase.Unknown,
                Message = $"{SourceName}: {product} already present (pre-installed OEM/consumer Office — no enrollment install)",
                Data = data,
                ImmediateUpload = true,
            });
        }

        /// <summary>
        /// Persists the lifecycle state (fail-soft). Active and the two terminals are persisted;
        /// <see cref="AbandonSilently"/> deliberately is NOT — it fires on every dispose/shutdown
        /// and persisting it as terminal would block the next run from resuming the open lifecycle
        /// (the resume is what delivers a completion missed across a mid-install reboot).
        /// Caller holds the lock.
        /// </summary>
        private void PersistState(string state)
        {
            if (_statePersistence == null) return;
            _lastStatePersistUtc = _clock.UtcNow;
            _statePersistence.Save(new OfficeInstallStateData
            {
                State = state,
                StartedAtUtc = _startedAtUtc,
                StartedTrigger = _startedTrigger,
                PeakDo = OfficeDoPeakData.FromSample(_peakDo),
            });
        }

        /// <summary>
        /// True when a core Office binary exists on disk under <c>{InstallationPath}\root\*</c> (the C2R
        /// version folder, e.g. <c>Office16</c>, is enumerated rather than hardcoded — a version literal
        /// would be fragile). Fail-soft: any error returns false (no positive proof). The
        /// <see cref="CoreBinariesProbe"/> seam overrides this in tests.
        /// </summary>
        private bool ProbeCoreBinaries(string? installationPath)
        {
            if (CoreBinariesProbe != null)
            {
                try { return CoreBinariesProbe(installationPath); }
                catch { return false; }
            }
            return CoreBinariesPresentOnDisk(installationPath, _logger);
        }

        internal static bool CoreBinariesPresentOnDisk(string? installationPath, AgentLogger logger)
        {
            if (string.IsNullOrWhiteSpace(installationPath)) return false;
            try
            {
                var root = System.IO.Path.Combine(installationPath!, "root");
                if (!System.IO.Directory.Exists(root)) return false;

                // Enumerate the version folder(s) under root (OfficeNN); avoids hardcoding the version.
                foreach (var versionDir in System.IO.Directory.GetDirectories(root))
                {
                    foreach (var binary in CoreBinaries)
                    {
                        try
                        {
                            if (System.IO.File.Exists(System.IO.Path.Combine(versionDir, binary))) return true;
                        }
                        catch { /* probe the next candidate */ }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"[{SourceName}] core-binary probe failed for '{installationPath}': {ex.Message}");
            }
            return false;
        }

        // StreamingFinished does not exist in the C2R registry, so the in-flight phase is reported as a
        // single "Installing"; the terminal completion uses "Completed".
        private static string PhaseOf(OfficeC2RSnapshot snap) => "Installing";

        // -----------------------------------------------------------------------
        // Payload
        // -----------------------------------------------------------------------

        private void EmitLifecycle(string eventType, OfficeC2RSnapshot snap, EventSeverity severity, string phase, bool isTerminal, bool coreBinariesPresent = false)
        {
            var data = BuildPayload(snap, phase, isTerminal);
            if (!string.IsNullOrEmpty(_startedTrigger)) data["startedTrigger"] = _startedTrigger!;
            if (isTerminal && eventType == Constants.EventTypes.OfficeInstallCompleted)
            {
                data["coreBinariesPresent"] = coreBinariesPresent;
                // One-time Delivery-Optimization summary (how Office was delivered) — far more useful than
                // a streaming download-% (which is noise under Connected-Cache + multi-job churn).
                if (_peakDo != null) data["doSummary"] = BuildDoSummary(_peakDo, snap.StreamingFinished);
            }
            var message = BuildMessage(eventType, snap);

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = SourceName,
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = true,
            });
        }

        internal Dictionary<string, object> BuildPayload(OfficeC2RSnapshot snap, string phase, bool isTerminal)
        {
            var elapsedSeconds = _startedAtUtc.HasValue
                ? Math.Max(0, (int)(_clock.UtcNow - _startedAtUtc.Value).TotalSeconds)
                : 0;

            var data = new Dictionary<string, object>
            {
                { "appName", AppName },
                { "products", snap.Products ?? new List<string>() },
                { "channel", snap.Channel ?? string.Empty },
                { "platform", snap.Platform ?? string.Empty },
                { "scenario", snap.ActiveScenarioName ?? string.Empty },
                { "phase", phase },
                { "versionReached", snap.VersionToReport ?? string.Empty },
                { "streamingFinished", snap.StreamingFinished },
                { "officeC2RClientRunning", snap.OfficeC2RClientRunning },
                { "errorCode", (object?)snap.ErrorCode ?? string.Empty },
                { "errorSource", (object?)snap.ErrorSource ?? string.Empty },
                { "installationPath", snap.InstallationPath ?? string.Empty },
                { "scanErrors", (snap.Errors ?? new List<string>()).ToList() },
            };

            // No per-event download-% (it is noise for Office). The Delivery-Optimization breakdown is
            // attached once to the completed event as doSummary (see EmitLifecycle / BuildDoSummary).

            if (isTerminal)
                data["durationSeconds"] = elapsedSeconds;
            else
                data["elapsedSeconds"] = elapsedSeconds;

            return data;
        }

        /// <summary>
        /// One-time Delivery-Optimization delivery breakdown for the completed event, built from the
        /// highest-bytes sample seen. Splits the bytes into Connected-Cache-Server / peer / pure-CDN: on
        /// MCC-enabled networks <c>BytesFromHttp</c> includes the cache-server bytes, so pure CDN is
        /// <c>BytesFromHttp - BytesFromCacheServer</c> (see project DO-telemetry notes).
        ///
        /// <para><c>aggregateFileSize</c> is the sum of the DO jobs' declared target sizes at the peak
        /// instant — NOT a stable "total Office payload". It commonly exceeds <c>totalBytesDownloaded</c>
        /// because the completed event fires at core-binary completion while background streaming of the
        /// remaining declared content is still in flight (<c>streamingFinished == false</c>). The mirrored
        /// <c>downloadPercent</c> + <c>streamingFinished</c> make that gap self-explanatory.</para>
        /// </summary>
        private static Dictionary<string, object> BuildDoSummary(OfficeDoSample d, bool streamingFinished)
        {
            long total = d.TotalBytesDownloaded;
            long cacheServer = d.BytesFromCacheServer;
            long peers = d.BytesFromPeers;
            long cdn = Math.Max(0, d.BytesFromHttp - d.BytesFromCacheServer);
            int Pct(long part) => total > 0 ? (int)Math.Min(100, (part * 100) / total) : 0;

            return new Dictionary<string, object>
            {
                { "totalBytesDownloaded", total },
                { "aggregateFileSize", d.FileSize },
                { "downloadPercent", (object?)d.DownloadPercent ?? 0 },
                { "streamingFinished", streamingFinished },
                { "jobCount", d.JobCount },
                { "bytesFromCacheServer", cacheServer },
                { "bytesFromPeers", peers },
                { "bytesFromCdn", cdn },
                { "percentFromCacheServer", Pct(cacheServer) },
                { "percentFromPeers", Pct(peers) },
                { "percentFromCdn", Pct(cdn) },
                { "downloadMode", d.DownloadMode },
            };
        }

        private static string BuildMessage(string eventType, OfficeC2RSnapshot snap)
        {
            var product = snap.Products != null && snap.Products.Count > 0 ? string.Join(",", snap.Products) : AppName;
            if (eventType == Constants.EventTypes.OfficeInstallStarted)
                return $"{SourceName}: {product} install detected";
            if (eventType == Constants.EventTypes.OfficeInstallCompleted)
                return $"{SourceName}: {product} install completed{(string.IsNullOrEmpty(snap.VersionToReport) ? string.Empty : $" (v{snap.VersionToReport})")}";
            return $"{SourceName}: {product} install failed{(string.IsNullOrEmpty(snap.ErrorCode) ? string.Empty : $" (code {snap.ErrorCode})")}";
        }

        // -----------------------------------------------------------------------
        // Registry + process read. Fail-soft — errors captured, never thrown.
        // -----------------------------------------------------------------------

        private OfficeC2RSnapshot? ReadSnapshotSafe()
        {
            try { return SnapshotProvider != null ? SnapshotProvider() : ReadSnapshot(); }
            catch (Exception ex) { _logger.Warning($"[{SourceName}] snapshot read failed: {ex.Message}"); return null; }
        }

        internal virtual OfficeC2RSnapshot ReadSnapshot()
        {
            var snap = new OfficeC2RSnapshot();
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    ReadConfiguration(hklm, snap);
                    ReadScenarios(hklm, snap);
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"registry:{ex.GetType().Name}: {ex.Message}");
            }

            snap.OfficeC2RClientRunning = IsOfficeC2RClientRunning(snap);
            return snap;
        }

        private static void ReadConfiguration(RegistryKey hklm, OfficeC2RSnapshot snap)
        {
            try
            {
                using (var key = hklm.OpenSubKey(ConfigurationSubKey, writable: false))
                {
                    if (key == null) return;
                    snap.ConfigurationKeyPresent = true;

                    var products = ReadString(key, "ProductReleaseIds");
                    if (!string.IsNullOrEmpty(products))
                    {
                        snap.Products = products!
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim())
                            .Where(p => p.Length > 0)
                            .ToList();
                    }

                    snap.Platform = ReadString(key, "Platform");
                    snap.Channel = ReadString(key, "UpdateChannel") ?? ReadString(key, "CDNBaseUrl");
                    snap.VersionToReport = ReadString(key, "VersionToReport") ?? ReadString(key, "ClientVersionToReport");
                    snap.StreamingFinished = IsTruthy(ReadString(key, "StreamingFinished"));
                    snap.InstallationPath = ReadString(key, "InstallationPath");
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"configuration:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ReadScenarios(RegistryKey hklm, OfficeC2RSnapshot snap)
        {
            try
            {
                using (var scenarioRoot = hklm.OpenSubKey(ScenarioSubKey, writable: false))
                {
                    if (scenarioRoot == null) return;

                    var scenarioNames = scenarioRoot.GetSubKeyNames();
                    if (scenarioNames.Length == 0) return;

                    snap.ActiveScenarioPresent = true;
                    snap.ActiveScenarioName = scenarioNames[0]; // primary scenario (INSTALL / FIRSTRUN / UPDATE …)

                    foreach (var scenarioName in scenarioNames)
                    {
                        // Attribute an error code ONLY from an install-class scenario. A failure value
                        // in a CLIENTUPDATE / UPDATE / maintenance scenario (the C2R client self-update
                        // and similar) must never fail an Office *install* — field session fa526757 /
                        // the CLIENTUPDATE errorCode "2" false positive.
                        var isInstallScenario = IsInstallScenario(scenarioName);
                        if (isInstallScenario && snap.InstallScenarioName == null)
                        {
                            snap.InstallScenarioPresent = true;
                            snap.InstallScenarioName = scenarioName;
                        }

                        try
                        {
                            using (var scenarioKey = scenarioRoot.OpenSubKey(scenarioName, writable: false))
                            {
                                if (scenarioKey == null) continue;
                                foreach (var valueName in scenarioKey.GetValueNames())
                                {
                                    var value = ReadString(scenarioKey, valueName);
                                    if (value == null) continue;
                                    snap.ScenarioValues[$"{scenarioName}\\{valueName}"] = value;

                                    if (isInstallScenario && snap.ErrorCode == null
                                        && ErrorValueHints.Any(h => valueName.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                                        && IsNonZeroNumericCode(value))
                                    {
                                        snap.ErrorCode = value;
                                        snap.ErrorSource = $"{scenarioName}\\{valueName}";
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            snap.Errors.Add($"scenario[{scenarioName}]:{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"scenario:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsOfficeC2RClientRunning(OfficeC2RSnapshot snap)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName("OfficeC2RClient");
                return procs.Length > 0;
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"process:{ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
        }

        private static string? ReadString(RegistryKey key, string valueName)
        {
            try
            {
                var v = key.GetValue(valueName);
                if (v == null) return null;
                var s = v.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        /// <summary>StreamingFinished is REG_SZ "True"/"False" or REG_DWORD 0/1 across versions.</summary>
        private static bool IsTruthy(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value!.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // Below this, a bare positive decimal is treated as an implausible failure code (a state /
        // phase / mode / retry-count enum, not an error). Real C2R failures are HRESULTs (hex or
        // negative decimal) or 5-digit C2R codes (17002, 30015, 30088…) — all >= this floor. Field
        // session fa526757 / the CLIENTUPDATE "2" false positive motivated this.
        private const long MinPlausibleDecimalErrorCode = 1000;

        /// <summary>
        /// True only when <paramref name="value"/> is a PLAUSIBLE non-zero error code: a non-zero hex
        /// (0x…) value, a negative decimal HRESULT, or a positive decimal at/above
        /// <see cref="MinPlausibleDecimalErrorCode"/>. Textual values ("Success" / "Completed" /
        /// "InProgress") and implausibly small positive decimals (e.g. "2") return false, so a benign
        /// status/enum value never masquerades as a failure.
        /// </summary>
        internal static bool IsNonZeroNumericCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex != 0;
            }
            // Decimal: reject non-numeric, zero, and implausibly small positive values; keep negative
            // HRESULTs and large positive C2R codes.
            if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) || dec == 0) return false;
            return dec < 0 || dec >= MinPlausibleDecimalErrorCode;
        }
    }

    // --------------------------------------------------------------------------------------
    // Raw scan model (surfaced to the test assembly via InternalsVisibleTo).
    // --------------------------------------------------------------------------------------

    /// <summary>One read's worth of Office C2R facts (registry + process).</summary>
    public sealed class OfficeC2RSnapshot
    {
        public bool ConfigurationKeyPresent { get; set; }
        public List<string> Products { get; set; } = new List<string>();
        public string? Channel { get; set; }
        public string? Platform { get; set; }
        public string? VersionToReport { get; set; }
        public string? InstallationPath { get; set; }
        public bool StreamingFinished { get; set; }
        public bool ActiveScenarioPresent { get; set; }
        public string? ActiveScenarioName { get; set; }
        /// <summary>True when an install-class scenario (INSTALL) subkey is present.</summary>
        public bool InstallScenarioPresent { get; set; }
        /// <summary>The install-class scenario subkey name, when present (else null).</summary>
        public string? InstallScenarioName { get; set; }
        public Dictionary<string, string> ScenarioValues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string? ErrorCode { get; set; }
        /// <summary>Which <c>scenario\value</c> produced <see cref="ErrorCode"/> — for diagnosis.</summary>
        public string? ErrorSource { get; set; }
        public bool OfficeC2RClientRunning { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// Aggregated Delivery-Optimization stats across all Office CDN download jobs in one DO poll,
    /// produced by the extended DeliveryOptimizationCollector and folded into the office_install_* events.
    /// </summary>
    public sealed class OfficeDoSample
    {
        public int JobCount { get; set; }
        public long FileSize { get; set; }
        public long TotalBytesDownloaded { get; set; }
        public long BytesFromPeers { get; set; }
        public long BytesFromHttp { get; set; }
        public long BytesFromCacheServer { get; set; }
        public int PercentPeerCaching { get; set; }
        public int DownloadMode { get; set; }

        /// <summary>Whole-percent download progress, or null when total file size is unknown.</summary>
        public int? DownloadPercent => FileSize > 0 ? (int?)Math.Min(100, (int)((TotalBytesDownloaded * 100) / FileSize)) : null;
    }
}
