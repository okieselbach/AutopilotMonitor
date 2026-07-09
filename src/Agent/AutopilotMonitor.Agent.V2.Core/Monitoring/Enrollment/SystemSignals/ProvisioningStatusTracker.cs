using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches ESP provisioning category status from the registry to detect failures
    /// that Shell-Core event 62407 patterns may miss (e.g. Certificate provisioning failures).
    ///
    /// Registry path: HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings
    /// Values: DevicePreparationCategory.Status, DeviceSetupCategory.Status, AccountSetupCategory.Status
    ///
    /// JSON format varies across Windows versions:
    ///   Flat:   { "CertificatesSubcategory": "Certificates (1 of 1 applied)", "categorySucceeded": true, ... }
    ///   Nested: { "CertificatesSubcategory": { "subcategoryState": "succeeded", "subcategoryStatusText": "..." }, ... }
    ///
    /// Event emission strategy:
    ///   - Emit once when a category first appears (initial snapshot)
    ///   - Emit on every JSON change (progress updates, state transitions)
    ///   - Emit once when categorySucceeded resolves to true/false (final outcome)
    ///   - Fire <see cref="EspFailureDetected"/> on subcategory failure even if categorySucceeded is null
    ///   - Fire <see cref="DeviceSetupProvisioningComplete"/> on DeviceSetup success (or fallback)
    ///
    /// Uses RegistryWatcher (RegNotifyChangeKeyValue) for instant registry change detection.
    /// </summary>
    internal sealed class ProvisioningStatusTracker : IDisposable
    {
        internal const string ProvisioningStatusRegistryPath = @"SOFTWARE\Microsoft\Provisioning\AutopilotSettings";
        internal const int DeviceSetupFallbackDelaySeconds = 30;
        // Same fallback window as DeviceSetup, applied to AccountSetup. Session 330f73f3 fix:
        // some Windows builds never set AccountSetupCategory.Status.categorySucceeded even when
        // all subcategories resolve to succeeded/notRequired; without a fallback the strong
        // post-AccountSetup gate (AccountSetupProvisioningSucceededUtc) would never be set and
        // the reducer would never promote to AwaitingHello.
        internal const int AccountSetupFallbackDelaySeconds = 30;
        internal const int ProvisioningDebounceMilliseconds = 1000;

        // Session 9d052230 fix: synthetic delay between "ESP subcategory failed detected" and the
        // EspFailureDetected event fire. Gives ImeLogTracker a window to surface a matching
        // app_install_failed event (with hresult capture) so the session timeline carries the
        // app-level failure that ESP aggregated into the registry. Without this window the
        // DecisionEngine transitions to Failed on the first registry observation, the agent
        // terminates, and any post-ESP IME log entries are lost (race between ESP registry
        // write and IME workload log).
        internal const int ProvisioningFailureSettleWindowSeconds = 30;

        // Session 2bc884b6 enrichment: MSIX/Store app failures never touch the IME Win32 sidecar
        // logs, so an ESP "Apps (0x80073cf9)" failure with all tracked apps green is a blackbox.
        // When an Apps-subcategory failure arms the settle window, a one-shot scan of the AppX
        // deployment event log runs on the threadpool and emits esp_appx_failure_analysis with
        // scored package candidates. Lookback is capped so resumed/long sessions don't correlate
        // against hours of unrelated servicing noise.
        internal const int AppxScanLookbackCapHours = 4;

        // Regex for HRESULT extraction from subcategory statusText (e.g. "Apps (0x87d1041c)").
        // Language-invariant: the parenthesised hex tail is consistent across Windows locales.
        private static readonly Regex HResultPattern = new Regex(
            @"\((0[xX][0-9a-fA-F]{8})\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] ProvisioningCategoryNames =
        {
            "DevicePreparationCategory.Status",
            "DeviceSetupCategory.Status",
            "AccountSetupCategory.Status"
        };

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;

        private RegistryWatcher _provisioningWatcher;
        private Timer _provisioningWatcherRetryTimer;
        private Timer _provisioningDebounceTimer;
        private Timer _deviceSetupFallbackTimer;
        private Timer _accountSetupFallbackTimer;

        // Track the raw JSON per category — used to detect any changes at all
        private Dictionary<string, string> _lastProvisioningJson;
        // Track which categories have been seen (for first-seen event)
        private HashSet<string> _provisioningCategorySeen;
        // Track the last known categorySucceeded per category (null = not yet resolved)
        private Dictionary<string, bool?> _lastCategorySucceeded;
        // Fire-once guard per category — prevent duplicate EspFailureDetected calls
        private HashSet<string> _provisioningFailureFired;
        // Track which categories have reported a final categorySucceeded value
        private HashSet<string> _provisioningCategoriesResolved;
        // Track subcategory states per category — detect meaningful state transitions
        private Dictionary<string, Dictionary<string, string>> _lastSubcategoryStates;
        // Fire-once guard for DeviceSetupProvisioningComplete event
        private bool _deviceSetupProvisioningCompleteFired;
        // Fire-once guard for AccountSetupProvisioningComplete event (session 330f73f3 fix)
        private bool _accountSetupProvisioningCompleteFired;
        // WhiteGlove confirmation: DeviceSetup registry contains SaveWhiteGloveSuccessResult=succeeded
        private bool _saveWhiteGloveSuccessResultSeen;
        // Track SaveWhiteGloveSuccessResult state transitions for observability (null → notStarted → succeeded)
        private string _lastSaveWhiteGloveState;
        // Settle-window timers + pending args per category (session 9d052230 fix). Keyed by
        // category-name (e.g. "DeviceSetupCategory.Status"). Pending args carry the enriched
        // failure details that fire after the settle window expires.
        private Dictionary<string, Timer> _provisioningFailureSettleTimers;
        private Dictionary<string, EspFailureDetectedEventArgs> _provisioningFailureSettleArgs;
        // Session 2bc884b6: AppX enrichment scan seams + fire-once guard. The scanner and the
        // dispatcher are injectable so tests never touch the real event log or the threadpool.
        private readonly IAppxDeploymentFailureScanner _appxScanner;
        private readonly Action<Action> _backgroundDispatcher;
        // Session c071e92b enrichment: IME package-state probe (wired to
        // ImeLogHost.AllKnownPackageStates by the factory chain) so an Apps-subcategory
        // failure can name the tracked apps that never completed — e.g. a Store app
        // (Company Portal) that starved the ESP into its sync-failure timeout without
        // ever producing an IME install event. Defaults to empty for single-tracker wiring.
        private readonly Func<IReadOnlyList<AppPackageState>> _packageStatesProbe;
        private DateTime _monitoringStartUtc;
        private bool _appxScanStarted;
        private readonly object _stateLock = new object();

        public event EventHandler<EspFailureDetectedEventArgs> EspFailureDetected;
        public event EventHandler DeviceSetupProvisioningComplete;
        /// <summary>
        /// Fires once when <c>AccountSetupCategory.Status</c> resolves to
        /// <c>categorySucceeded=true</c>, or when the fallback fires (all subcategories
        /// succeeded/notRequired but Windows never set the boolean — same fallback shape as
        /// DeviceSetup). Session 330f73f3 fix: this is the strong "User-ESP truly finished"
        /// signal that gates the reducer's promotion to <c>AwaitingHello</c>; without it the
        /// reducer ignores intermediate Shell-Core 62407 events.
        /// </summary>
        public event EventHandler AccountSetupProvisioningComplete;

        public ProvisioningStatusTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IAppxDeploymentFailureScanner appxScanner = null,
            Action<Action> backgroundDispatcher = null,
            Func<IReadOnlyList<AppPackageState>> packageStatesProbe = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appxScanner = appxScanner ?? new AppxDeploymentFailureScanner();
            _backgroundDispatcher = backgroundDispatcher ?? (action => Task.Run(action));
            _packageStatesProbe = packageStatesProbe ?? (() => Array.Empty<AppPackageState>());
        }

        // =====================================================================
        // Public snapshot / state API (forwarded by coordinator)
        // =====================================================================

        /// <summary>
        /// Snapshot of current provisioning-category state for consumers like the signal-correlated
        /// WhiteGlove detection. Thread-safe.
        /// </summary>
        public Dictionary<string, bool?> GetProvisioningCategorySnapshot()
        {
            lock (_stateLock)
            {
                return _lastCategorySucceeded == null
                    ? new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool?>(_lastCategorySucceeded, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// True when DeviceSetupCategory.Status has resolved categorySucceeded=true OR when the
        /// fallback (all subcategories succeeded, categorySucceeded null) has been confirmed.
        /// </summary>
        public bool DeviceSetupCategorySucceeded
        {
            get
            {
                lock (_stateLock)
                {
                    if (_deviceSetupProvisioningCompleteFired)
                        return true;
                    return _lastCategorySucceeded != null
                        && _lastCategorySucceeded.TryGetValue("DeviceSetupCategory.Status", out var v)
                        && v == true;
                }
            }
        }

        /// <summary>
        /// True when any AccountSetup subcategory has been tracked (resolved or in progress).
        /// </summary>
        public bool HasAccountSetupActivity
        {
            get
            {
                lock (_stateLock)
                {
                    if (_lastSubcategoryStates == null)
                        return false;
                    return _lastSubcategoryStates.TryGetValue("AccountSetupCategory.Status", out var subs)
                        && subs != null
                        && subs.Count > 0;
                }
            }
        }

        /// <summary>
        /// True when DeviceSetup registry JSON contains a SaveWhiteGloveSuccessResult property
        /// with subcategoryState=succeeded. This is a definitive WhiteGlove (Pre-Provisioning)
        /// confirmation signal — Windows only writes this property during White Glove flows.
        /// </summary>
        public bool HasSaveWhiteGloveSuccessResult
        {
            get { lock (_stateLock) { return _saveWhiteGloveSuccessResultSeen; } }
        }

        /// <summary>
        /// Returns a thread-safe snapshot of the current ESP provisioning category status.
        /// All data is deep-copied under the lock so the caller can use it freely.
        /// Returns null if no provisioning data has been observed yet.
        /// </summary>
        public EspProvisioningSnapshot GetProvisioningSnapshot()
        {
            lock (_stateLock)
            {
                if (_provisioningCategorySeen == null || _provisioningCategorySeen.Count == 0)
                    return null;

                var outcomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cat in _provisioningCategorySeen)
                {
                    var label = cat.Replace("Category.Status", "");
                    if (_lastCategorySucceeded.TryGetValue(cat, out var succeeded))
                    {
                        outcomes[label] = succeeded == true ? "success"
                                        : succeeded == false ? "failed"
                                        : "in_progress";
                    }
                    else
                    {
                        outcomes[label] = "in_progress";
                    }
                }

                var subcats = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _lastSubcategoryStates)
                {
                    var label = kvp.Key.Replace("Category.Status", "");
                    subcats[label] = new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
                }

                return new EspProvisioningSnapshot
                {
                    CategoryOutcomes = outcomes,
                    SubcategoryStates = subcats,
                    CategoriesSeen = _provisioningCategorySeen.Count,
                    CategoriesResolved = _provisioningCategoriesResolved.Count,
                    AllResolved = _provisioningCategoriesResolved.Count >= _provisioningCategorySeen.Count
                };
            }
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Start()
        {
            _lastProvisioningJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategorySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastCategorySucceeded = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            _provisioningFailureFired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategoriesResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastSubcategoryStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _provisioningFailureSettleTimers = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
            _provisioningFailureSettleArgs = new Dictionary<string, EspFailureDetectedEventArgs>(StringComparer.OrdinalIgnoreCase);
            _deviceSetupProvisioningCompleteFired = false;
            _deviceSetupFallbackTimer = null;
            _accountSetupProvisioningCompleteFired = false;
            _accountSetupFallbackTimer = null;
            _monitoringStartUtc = DateTime.UtcNow;
            _appxScanStarted = false;

            // Try to start immediately; if key doesn't exist yet, retry every 2s
            if (!TryStartWatcher())
            {
                _logger.Info("Provisioning status registry key not yet present — retrying every 2s");
                _provisioningWatcherRetryTimer = new Timer(
                    _ =>
                    {
                        if (TryStartWatcher())
                        {
                            _provisioningWatcherRetryTimer?.Dispose();
                            _provisioningWatcherRetryTimer = null;
                        }
                    },
                    null,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2));
            }
        }

        public void Stop(string reason = "tracker_stopped")
        {
            try
            {
                _provisioningWatcherRetryTimer?.Dispose();
                _provisioningWatcherRetryTimer = null;

                _provisioningDebounceTimer?.Dispose();
                _provisioningDebounceTimer = null;

                _deviceSetupFallbackTimer?.Dispose();
                _deviceSetupFallbackTimer = null;

                _accountSetupFallbackTimer?.Dispose();
                _accountSetupFallbackTimer = null;

                if (_provisioningFailureSettleTimers != null)
                {
                    foreach (var t in _provisioningFailureSettleTimers.Values)
                    {
                        try { t?.Dispose(); } catch { /* swallow */ }
                    }
                    _provisioningFailureSettleTimers.Clear();
                }

                if (_provisioningWatcher != null)
                {
                    _provisioningWatcher.Dispose();
                    _provisioningWatcher = null;
                    _logger.Info($"Provisioning status watcher stopped: {reason}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping provisioning status watcher", ex);
            }
        }

        public void Dispose() => Stop();

        private bool TryStartWatcher()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath))
                {
                    if (key == null)
                        return false;
                }

                // Capture initial state before watcher starts
                _logger.Trace("ProvisioningWatcher: capturing initial state before watcher starts");
                CheckProvisioningStatus();

                // Debounce timer — coalesces rapid-fire registry notifications into a single check
                _provisioningDebounceTimer = new Timer(
                    _ => CheckProvisioningStatus(),
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);

                _provisioningWatcher = new RegistryWatcher(
                    RegistryHive.LocalMachine,
                    ProvisioningStatusRegistryPath,
                    watchSubtree: true,
                    filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet,
                    trace: msg => _logger.Trace($"RegistryWatcher: {msg}"));

                _provisioningWatcher.Changed += (s, e) =>
                {
                    _logger.Trace("ProvisioningWatcher: Changed event fired — debouncing CheckProvisioningStatus");
                    _provisioningDebounceTimer?.Change(ProvisioningDebounceMilliseconds, Timeout.Infinite);
                };
                _provisioningWatcher.Error += (s, ex) => _logger.Warning($"Provisioning watcher handler error: {ex.Message}");

                _provisioningWatcher.Start();
                _logger.Info("Provisioning status registry watcher started");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to start provisioning watcher: {ex.Message}");
                return false;
            }
        }

        // =====================================================================
        // Registry polling + processing
        // =====================================================================

        private void CheckProvisioningStatus()
        {
            try
            {
                lock (_stateLock)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath, writable: false))
                    {
                        if (key == null)
                        {
                            _logger.Trace("CheckProvisioningStatus: registry key not found");
                            return;
                        }

                        int changedCount = 0, unchangedCount = 0, missingCount = 0;

                        foreach (var categoryName in ProvisioningCategoryNames)
                        {
                            var jsonValue = key.GetValue(categoryName)?.ToString();
                            if (string.IsNullOrEmpty(jsonValue))
                            {
                                missingCount++;
                                _logger.Trace($"CheckProvisioningStatus: {categoryName} — not present in registry");
                                continue;
                            }

                            if (_lastProvisioningJson.TryGetValue(categoryName, out var lastJson) && lastJson == jsonValue)
                            {
                                unchangedCount++;
                                continue;
                            }

                            changedCount++;
                            bool isNew = !_lastProvisioningJson.ContainsKey(categoryName);
                            // Guard: the raw-JSON interpolation is materialized regardless of log
                            // level (Trace() only drops the string after it is built), and this runs
                            // per changed category every poll — skip the alloc when Trace is off.
                            if (_logger.LogLevel >= AgentLogLevel.Trace)
                            {
                                _logger.Trace($"CheckProvisioningStatus: {categoryName} — {(isNew ? "NEW" : "CHANGED")} (json length={jsonValue.Length})");
                                _logger.Trace($"CheckProvisioningStatus: {categoryName} — raw JSON: {jsonValue}");
                            }

                            _lastProvisioningJson[categoryName] = jsonValue;
                            // TryFireProvisioningFailure (inside ProcessCategoryStatus) arms the
                            // 30 s settle timer when a failure is detected — the actual
                            // EspFailureDetected fire happens in OnProvisioningFailureSettleExpired.
                            ProcessCategoryStatus(categoryName, jsonValue);
                        }

                        _logger.Trace($"CheckProvisioningStatus: summary — changed={changedCount}, unchanged={unchangedCount}, missing={missingCount}, " +
                                     $"seen={_provisioningCategorySeen.Count}, resolved={_provisioningCategoriesResolved.Count}/{_lastProvisioningJson.Count}, " +
                                     $"failuresFired={_provisioningFailureFired.Count}");

                        // Self-termination: only auto-stop when all categories resolved with success (no failures).
                        if (_provisioningCategoriesResolved.Count > 0
                            && _lastProvisioningJson.Count > 0
                            && _provisioningCategoriesResolved.Count >= _lastProvisioningJson.Count
                            && !_provisioningFailureFired.Any())
                        {
                            _logger.Info("Provisioning status watcher: all categories resolved with success — requesting stop");
                            _provisioningWatcher?.RequestStop();
                        }

                        // Fire DeviceSetupProvisioningComplete when DeviceSetup resolves with success.
                        if (!_deviceSetupProvisioningCompleteFired
                            && _lastCategorySucceeded.TryGetValue("DeviceSetupCategory.Status", out var dsSucceeded)
                            && dsSucceeded == true)
                        {
                            _deviceSetupProvisioningCompleteFired = true;
                            _logger.Info("Provisioning status: DeviceSetup succeeded — firing DeviceSetupProvisioningComplete");
                            try { DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
                            catch (Exception ex) { _logger.Error("DeviceSetupProvisioningComplete handler failed", ex); }
                        }

                        // Fallback: all subcategories succeeded but categorySucceeded was never set by Windows.
                        // Some Windows builds (e.g. 25H2/26200) don't set the boolean even after all subcategories succeed.
                        if (!_deviceSetupProvisioningCompleteFired
                            && _deviceSetupFallbackTimer == null
                            && _lastCategorySucceeded.TryGetValue("DeviceSetupCategory.Status", out var dsFallbackState)
                            && dsFallbackState == null
                            && _lastSubcategoryStates.TryGetValue("DeviceSetupCategory.Status", out var dsFallbackSubStates)
                            && dsFallbackSubStates.Count > 0
                            && dsFallbackSubStates.Values.All(s =>
                                string.Equals(s, "succeeded", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s, "notRequired", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.Info($"Provisioning status: DeviceSetup — all {dsFallbackSubStates.Count} subcategories succeeded " +
                                         $"but categorySucceeded not set by Windows — starting {DeviceSetupFallbackDelaySeconds}s fallback timer");
                            _deviceSetupFallbackTimer = new Timer(
                                OnDeviceSetupFallbackTimerExpired,
                                null,
                                TimeSpan.FromSeconds(DeviceSetupFallbackDelaySeconds),
                                Timeout.InfiniteTimeSpan);
                        }

                        // Session 330f73f3 fix: same flow for AccountSetup. Fires
                        // AccountSetupProvisioningComplete when the category resolves with
                        // success, or when the fallback confirms all subcategories
                        // succeeded/notRequired but categorySucceeded was never set.
                        if (!_accountSetupProvisioningCompleteFired
                            && _lastCategorySucceeded.TryGetValue("AccountSetupCategory.Status", out var asSucceeded)
                            && asSucceeded == true)
                        {
                            _accountSetupProvisioningCompleteFired = true;
                            _logger.Info("Provisioning status: AccountSetup succeeded — firing AccountSetupProvisioningComplete");
                            try { AccountSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
                            catch (Exception ex) { _logger.Error("AccountSetupProvisioningComplete handler failed", ex); }
                        }

                        if (!_accountSetupProvisioningCompleteFired
                            && _accountSetupFallbackTimer == null
                            && _lastCategorySucceeded.TryGetValue("AccountSetupCategory.Status", out var asFallbackState)
                            && asFallbackState == null
                            && _lastSubcategoryStates.TryGetValue("AccountSetupCategory.Status", out var asFallbackSubStates)
                            && asFallbackSubStates.Count > 0
                            && asFallbackSubStates.Values.All(s =>
                                string.Equals(s, "succeeded", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s, "notRequired", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.Info($"Provisioning status: AccountSetup — all {asFallbackSubStates.Count} subcategories succeeded " +
                                         $"but categorySucceeded not set by Windows — starting {AccountSetupFallbackDelaySeconds}s fallback timer");
                            _accountSetupFallbackTimer = new Timer(
                                OnAccountSetupFallbackTimerExpired,
                                null,
                                TimeSpan.FromSeconds(AccountSetupFallbackDelaySeconds),
                                Timeout.InfiniteTimeSpan);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Provisioning status check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Session 330f73f3 fix: AccountSetup-side mirror of <see cref="OnDeviceSetupFallbackTimerExpired"/>.
        /// Confirms via a fresh registry read that all subcategories are still
        /// succeeded/notRequired and categorySucceeded is still unset, then fires
        /// <see cref="AccountSetupProvisioningComplete"/>. If Windows wrote a real
        /// categorySucceeded value in the meantime, the normal path re-runs and handles it.
        /// <para>
        /// On every abort path the timer field is reset to <c>null</c> so a later
        /// <c>CheckProvisioningStatus</c> can re-arm the fallback when the "all subcategories
        /// succeeded" condition is re-satisfied (transient flicker race: a subcategory briefly
        /// drops to in_progress between fallback arm and timer fire, the callback aborts —
        /// without resetting the field, a re-stabilised state could never re-arm and the
        /// strong-gate fact never gets posted).
        /// </para>
        /// </summary>
        private void OnAccountSetupFallbackTimerExpired(object state)
        {
            try
            {
                lock (_stateLock)
                {
                    if (_accountSetupProvisioningCompleteFired)
                    {
                        _logger.Debug("AccountSetup fallback timer expired but AccountSetupProvisioningComplete already fired — ignoring");
                        // No timer-field reset needed: re-arm is gated on _accountSetupProvisioningCompleteFired too.
                        return;
                    }

                    string registryJson = null;
                    using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath, writable: false))
                    {
                        registryJson = key?.GetValue("AccountSetupCategory.Status")?.ToString();
                    }

                    if (string.IsNullOrEmpty(registryJson))
                    {
                        _logger.Warning("AccountSetup fallback timer: registry value not found — aborting fallback");
                        ResetAccountSetupFallbackTimer();
                        return;
                    }

                    bool? categorySucceeded = null;
                    List<SubcategoryInfo> subcategories = null;
                    try
                    {
                        using (var doc = JsonDocument.Parse(registryJson))
                        {
                            categorySucceeded = SafeGetBool(doc.RootElement, "categorySucceeded");
                            subcategories = ParseSubcategories(doc.RootElement);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.Warning($"AccountSetup fallback timer: failed to parse registry JSON — aborting fallback: {ex.Message}");
                        ResetAccountSetupFallbackTimer();
                        return;
                    }

                    if (categorySucceeded.HasValue)
                    {
                        _logger.Info($"AccountSetup fallback timer: categorySucceeded is now {categorySucceeded.Value} — normal path will handle this");
                        ResetAccountSetupFallbackTimer();
                        CheckProvisioningStatus();
                        return;
                    }

                    if (subcategories == null || subcategories.Count == 0)
                    {
                        _logger.Warning("AccountSetup fallback timer: no subcategories found — aborting fallback");
                        ResetAccountSetupFallbackTimer();
                        return;
                    }

                    var nonSucceeded = subcategories.Where(s =>
                        !string.Equals(s.State, "succeeded", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(s.State, "notRequired", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (nonSucceeded.Count > 0)
                    {
                        _logger.Warning($"AccountSetup fallback timer: {nonSucceeded.Count} subcategory/ies not succeeded " +
                                        $"({string.Join(", ", nonSucceeded.Select(s => $"{s.Name}={s.State}"))}) — aborting fallback");
                        ResetAccountSetupFallbackTimer();
                        return;
                    }

                    EmitRawRegistryDump("AccountSetup", registryJson, "fallback_confirmed");

                    _logger.Warning($"AccountSetup fallback: all {subcategories.Count} subcategories succeeded but " +
                                    $"categorySucceeded was not set by Windows after {AccountSetupFallbackDelaySeconds}s — treating as complete");

                    var subcatData = new Dictionary<string, object>();
                    foreach (var sub in subcategories)
                    {
                        subcatData[sub.Name] = new Dictionary<string, string>
                        {
                            { "state", sub.State },
                            { "statusText", sub.StatusText }
                        };
                    }

                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.EspProvisioningStatus,
                        Severity = EventSeverity.Warning,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"ESP provisioning status: AccountSetup — all {subcategories.Count} subcategories succeeded " +
                                  $"but categorySucceeded was not confirmed by Windows — treating as complete (fallback after {AccountSetupFallbackDelaySeconds}s)",
                        Data = new Dictionary<string, object>
                        {
                            { "category", "AccountSetup" },
                            { "categorySucceeded", "in_progress" },
                            { "fallbackApplied", true },
                            { "fallbackReason", "all_subcategories_succeeded_category_unresolved" },
                            { "fallbackDelaySeconds", AccountSetupFallbackDelaySeconds },
                            { "subcategoryCount", subcategories.Count },
                            { "subcategories", subcatData }
                        }
                    });

                    // Fired success path: dispose + null the timer so Stop() doesn't redundantly
                    // dispose it. Re-arm is also gated on _accountSetupProvisioningCompleteFired so
                    // null-ing here is correctness-neutral on the start-condition side.
                    ResetAccountSetupFallbackTimer();
                    _accountSetupProvisioningCompleteFired = true;
                    try { AccountSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
                    catch (Exception ex) { _logger.Error("AccountSetupProvisioningComplete handler failed (fallback path)", ex); }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"AccountSetup fallback timer callback failed: {ex.Message}", ex);
                // Defensive re-arm-blocker cleanup. If the inner block threw before any of the
                // explicit Reset paths ran (e.g. registry open / Emit / Invoke failure), the
                // timer field would otherwise stay non-null and CheckProvisioningStatus' start
                // condition could never re-arm a re-stabilised "all subcategories succeeded"
                // state. The lock is re-entrant — safe to re-acquire here.
                try
                {
                    lock (_stateLock)
                    {
                        ResetAccountSetupFallbackTimer();
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.Error("AccountSetup fallback timer reset on exception path failed", cleanupEx);
                }
            }
        }

        /// <summary>
        /// Reset the AccountSetup fallback timer field after the callback consumes it. Required
        /// because the start condition in <see cref="CheckProvisioningStatus"/> gates re-arming
        /// on <c>_accountSetupFallbackTimer == null</c>; without this reset a transient flicker
        /// (subcategory briefly drops to in_progress between arm and timer fire → callback
        /// aborts) would permanently prevent re-arming even when the state re-stabilises.
        /// Caller already holds <see cref="_stateLock"/>.
        /// </summary>
        private void ResetAccountSetupFallbackTimer()
        {
            _accountSetupFallbackTimer?.Dispose();
            _accountSetupFallbackTimer = null;
        }

        /// <summary>
        /// DeviceSetup mirror of <see cref="ResetAccountSetupFallbackTimer"/>. Same re-arm-blocker
        /// concern, same fix. Caller already holds <see cref="_stateLock"/>.
        /// </summary>
        private void ResetDeviceSetupFallbackTimer()
        {
            _deviceSetupFallbackTimer?.Dispose();
            _deviceSetupFallbackTimer = null;
        }

        /// <summary>
        /// Parity with <see cref="OnAccountSetupFallbackTimerExpired"/> — every abort path
        /// resets <see cref="_deviceSetupFallbackTimer"/> to <c>null</c> so a transient
        /// subcategory flicker that aborts the callback cannot permanently block a re-arm
        /// from <see cref="CheckProvisioningStatus"/>.
        /// </summary>
        private void OnDeviceSetupFallbackTimerExpired(object state)
        {
            try
            {
                lock (_stateLock)
                {
                    if (_deviceSetupProvisioningCompleteFired)
                    {
                        _logger.Debug("DeviceSetup fallback timer expired but DeviceSetupProvisioningComplete already fired — ignoring");
                        // No timer-field reset needed: re-arm is gated on _deviceSetupProvisioningCompleteFired too.
                        return;
                    }

                    string registryJson = null;
                    using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath, writable: false))
                    {
                        registryJson = key?.GetValue("DeviceSetupCategory.Status")?.ToString();
                    }

                    if (string.IsNullOrEmpty(registryJson))
                    {
                        _logger.Warning("DeviceSetup fallback timer: registry value not found — aborting fallback");
                        ResetDeviceSetupFallbackTimer();
                        return;
                    }

                    bool? categorySucceeded = null;
                    List<SubcategoryInfo> subcategories = null;
                    try
                    {
                        using (var doc = JsonDocument.Parse(registryJson))
                        {
                            categorySucceeded = SafeGetBool(doc.RootElement, "categorySucceeded");
                            subcategories = ParseSubcategories(doc.RootElement);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.Warning($"DeviceSetup fallback timer: failed to parse registry JSON — aborting fallback: {ex.Message}");
                        ResetDeviceSetupFallbackTimer();
                        return;
                    }

                    if (categorySucceeded.HasValue)
                    {
                        _logger.Info($"DeviceSetup fallback timer: categorySucceeded is now {categorySucceeded.Value} — normal path will handle this");
                        ResetDeviceSetupFallbackTimer();
                        CheckProvisioningStatus();
                        return;
                    }

                    if (subcategories == null || subcategories.Count == 0)
                    {
                        _logger.Warning("DeviceSetup fallback timer: no subcategories found — aborting fallback");
                        ResetDeviceSetupFallbackTimer();
                        return;
                    }

                    var nonSucceeded = subcategories.Where(s =>
                        !string.Equals(s.State, "succeeded", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(s.State, "notRequired", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (nonSucceeded.Count > 0)
                    {
                        _logger.Warning($"DeviceSetup fallback timer: {nonSucceeded.Count} subcategory/ies not succeeded " +
                                        $"({string.Join(", ", nonSucceeded.Select(s => $"{s.Name}={s.State}"))}) — aborting fallback");
                        ResetDeviceSetupFallbackTimer();
                        return;
                    }

                    EmitRawRegistryDump("DeviceSetup", registryJson, "fallback_confirmed");

                    _logger.Warning($"DeviceSetup fallback: all {subcategories.Count} subcategories succeeded but " +
                                    $"categorySucceeded was not set by Windows after {DeviceSetupFallbackDelaySeconds}s — treating as complete");

                    var subcatData = new Dictionary<string, object>();
                    foreach (var sub in subcategories)
                    {
                        subcatData[sub.Name] = new Dictionary<string, string>
                        {
                            { "state", sub.State },
                            { "statusText", sub.StatusText }
                        };
                    }

                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.EspProvisioningStatus,
                        Severity = EventSeverity.Warning,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"ESP provisioning status: DeviceSetup — all {subcategories.Count} subcategories succeeded " +
                                  $"but categorySucceeded was not confirmed by Windows — treating as complete (fallback after {DeviceSetupFallbackDelaySeconds}s)",
                        Data = new Dictionary<string, object>
                        {
                            { "category", "DeviceSetup" },
                            { "categorySucceeded", "in_progress" },
                            { "fallbackApplied", true },
                            { "fallbackReason", "all_subcategories_succeeded_category_unresolved" },
                            { "fallbackDelaySeconds", DeviceSetupFallbackDelaySeconds },
                            { "subcategoryCount", subcategories.Count },
                            { "subcategories", subcatData }
                        }
                    });

                    // Fired success path: dispose + null the timer so Stop() doesn't redundantly
                    // dispose it. Re-arm is also gated on _deviceSetupProvisioningCompleteFired so
                    // null-ing here is correctness-neutral on the start-condition side.
                    ResetDeviceSetupFallbackTimer();
                    _deviceSetupProvisioningCompleteFired = true;
                    try { DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
                    catch (Exception ex) { _logger.Error("DeviceSetupProvisioningComplete handler failed (fallback path)", ex); }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"DeviceSetup fallback timer callback failed: {ex.Message}", ex);
                // Mirror of AccountSetup defensive cleanup: if the inner block threw before any
                // of the explicit Reset paths ran, the timer field would otherwise stay non-null
                // and CheckProvisioningStatus' start condition could never re-arm. Lock is
                // re-entrant — safe to re-acquire.
                try
                {
                    lock (_stateLock)
                    {
                        ResetDeviceSetupFallbackTimer();
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.Error("DeviceSetup fallback timer reset on exception path failed", cleanupEx);
                }
            }
        }

        /// <summary>
        /// Processes a single category status JSON value.
        /// Emits events on first-seen, subcategory state transitions, and categorySucceeded resolution.
        /// </summary>
        internal ProvisioningResult ProcessCategoryStatus(string categoryName, string jsonValue)
        {
            var categoryLabel = categoryName.Replace("Category.Status", "");

            try
            {
                using (var doc = JsonDocument.Parse(jsonValue))
                {
                    var root = doc.RootElement;

                    bool? categorySucceeded = SafeGetBool(root, "categorySucceeded");
                    string categoryStatusMessage = SafeGetString(root, "categoryStatusMessage");
                    var subcategories = ParseSubcategories(root);

                    // Detect WhiteGlove signal in DeviceSetup category.
                    // SaveWhiteGloveSuccessResult is NOT a *Subcategory-suffixed property, so
                    // ParseSubcategories skips it. We scan the raw JSON explicitly.
                    // Track state transitions for full observability (notStarted → succeeded).
                    if (string.Equals(categoryLabel, "DeviceSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Name.IndexOf("SaveWhiteGloveSuccessResult", StringComparison.OrdinalIgnoreCase) >= 0
                                && prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                var state = SafeGetString(prop.Value, "subcategoryState") ?? "unknown";

                                if (!string.Equals(state, _lastSaveWhiteGloveState, StringComparison.OrdinalIgnoreCase))
                                {
                                    var previousState = _lastSaveWhiteGloveState ?? "not_seen";
                                    _lastSaveWhiteGloveState = state;

                                    _logger.Info($"ProvisioningStatusTracker: SaveWhiteGloveSuccessResult state " +
                                                 $"transition: {previousState} -> {state}");

                                    EmitRawRegistryDump(categoryLabel, jsonValue,
                                        $"whiteglove_signal_{state}");

                                    if (string.Equals(state, "succeeded", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _saveWhiteGloveSuccessResultSeen = true;
                                        _logger.Info("ProvisioningStatusTracker: SaveWhiteGloveSuccessResult=succeeded " +
                                                     "— WhiteGlove confirmation signal");
                                    }
                                }
                                break;
                            }
                        }
                    }

                    string statusText;
                    if (categoryStatusMessage != null)
                        statusText = categoryStatusMessage;
                    else if (categorySucceeded == true)
                        statusText = "Complete";
                    else if (categorySucceeded == false)
                        statusText = "Failed";
                    else
                        statusText = BuildProgressSummary(subcategories);

                    bool isFirstSeen = !_provisioningCategorySeen.Contains(categoryName);
                    bool categorySucceededChanged = HasCategorySucceededChanged(categoryName, categorySucceeded);

                    _logger.Trace($"ProcessCategory: {categoryLabel} — succeeded={categorySucceeded?.ToString() ?? "null"}, " +
                                 $"isFirstSeen={isFirstSeen}, succeededChanged={categorySucceededChanged}, " +
                                 $"subcategories={subcategories.Count} [{string.Join(", ", subcategories.Select(s => $"{s.Name}={s.State}"))}]");

                    if (isFirstSeen)
                    {
                        _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING first-seen event");
                        _provisioningCategorySeen.Add(categoryName);
                        _lastCategorySucceeded[categoryName] = categorySucceeded;
                        StoreSubcategoryStates(categoryName, subcategories);
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info);
                        EmitRawRegistryDump(categoryLabel, jsonValue, "first_seen");
                    }
                    else if (categorySucceededChanged)
                    {
                        _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING categorySucceeded change ({_lastCategorySucceeded[categoryName]?.ToString() ?? "null"} → {categorySucceeded?.ToString() ?? "null"})");
                        _lastCategorySucceeded[categoryName] = categorySucceeded;
                        StoreSubcategoryStates(categoryName, subcategories);
                        var severity = categorySucceeded == false ? EventSeverity.Warning : EventSeverity.Info;
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, severity);
                        EmitRawRegistryDump(categoryLabel, jsonValue, $"category_resolved_{(categorySucceeded == true ? "success" : "failed")}");
                    }
                    else
                    {
                        var transitions = DetectSubcategoryTransitions(categoryName, subcategories);
                        StoreSubcategoryStates(categoryName, subcategories);

                        var failureTransitions = transitions.Where(t => t.IsFailure).ToList();
                        if (transitions.Count > 0)
                        {
                            _logger.Trace($"ProcessCategory: {categoryLabel} — subcategory transitions: " +
                                         string.Join(", ", transitions.Select(t => $"{t.SubcategoryName}: {t.OldState}→{t.NewState} (failure={t.IsFailure})")));
                        }

                        if (failureTransitions.Count > 0)
                        {
                            _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING failure transition event");
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Warning, failureTransitions);
                        }
                        else if (transitions.Count > 0)
                        {
                            _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING subcategory transition event");
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info, transitions);
                        }
                        else
                        {
                            _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING progress update (JSON changed, no state transitions)");
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info);
                        }
                    }

                    if (categorySucceeded.HasValue)
                        _provisioningCategoriesResolved.Add(categoryName);

                    if (categorySucceeded == false)
                    {
                        return TryFireProvisioningFailure(categoryName, categoryLabel, subcategories);
                    }

                    var failedSub = FindFailedSubcategory(subcategories);
                    if (failedSub != null && categorySucceeded == null)
                    {
                        return TryFireProvisioningFailure(categoryName, categoryLabel, subcategories);
                    }

                    return ProvisioningResult.NoAction;
                }
            }
            catch (JsonException ex)
            {
                _logger.Warning($"Failed to parse provisioning status JSON for {categoryLabel}: {ex.Message}");
                return ProvisioningResult.NoAction;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Unexpected error processing provisioning status for {categoryLabel}: {ex.Message}");
                return ProvisioningResult.NoAction;
            }
        }

        private ProvisioningResult TryFireProvisioningFailure(string categoryName, string categoryLabel, List<SubcategoryInfo> subcategories)
        {
            if (_provisioningFailureFired.Contains(categoryName))
                return ProvisioningResult.NoAction;

            _provisioningFailureFired.Add(categoryName);

            var failedSubcategoryInfo = FindFailedSubcategoryInfo(subcategories);
            var failedSubcategoryName = failedSubcategoryInfo?.Name;
            var failureTypeName = failedSubcategoryName != null
                ? $"Provisioning_{categoryLabel}_{failedSubcategoryName}_Failed"
                : $"Provisioning_{categoryLabel}_Failed";

            var errorCode = TryExtractErrorCode(failedSubcategoryInfo?.StatusText);

            _logger.Info(
                $"Provisioning subcategory failure: {categoryLabel}/{failedSubcategoryName ?? "category-level"} — " +
                $"escalating as {failureTypeName} (errorCode={errorCode ?? "n/a"})");
            _logger.Warning($"Provisioning failure detected: {failureTypeName}");

            // Caller already holds _stateLock.
            ArmFailureSettleTimer(
                categoryName: categoryName,
                args: new EspFailureDetectedEventArgs(
                    failureType: failureTypeName,
                    errorCode: errorCode,
                    failedSubcategory: failedSubcategoryName,
                    category: categoryLabel));

            // Settle-window owns the actual EspFailureDetected fire. The legacy "fire-after-emit"
            // path in CheckProvisioningStatus is now a no-op for this code-path; it stays in place
            // only for direct callers that may still expect a synchronous failure-type string.
            return ProvisioningResult.NoAction;
        }

        /// <summary>
        /// Session 9d052230 fix: arms a 30 s settle timer per category. While the timer is
        /// pending, ImeLogTracker can still surface app_install_failed events with hresult
        /// capture so the session timeline carries the underlying app-level failure that ESP
        /// aggregated into a single "Apps: failed (0x…)" registry write. On expiry the
        /// EspFailureDetected event is fired with the enriched args.
        /// <para>
        /// Caller holds <see cref="_stateLock"/>. Pending args are stored under the same lock
        /// so the timer callback re-acquires it before invocation. A single per-session emit
        /// of <c>esp_failure_settle_started</c> is sufficient — fire-once is gated by
        /// <see cref="_provisioningFailureFired"/> which TryFireProvisioningFailure checks.
        /// </para>
        /// </summary>
        private void ArmFailureSettleTimer(string categoryName, EspFailureDetectedEventArgs args)
        {
            _provisioningFailureSettleArgs[categoryName] = args;

            EmitFailureSettleStarted(args);

            var timer = new Timer(
                _ => OnProvisioningFailureSettleExpired(categoryName),
                state: null,
                dueTime: TimeSpan.FromSeconds(ProvisioningFailureSettleWindowSeconds),
                period: Timeout.InfiniteTimeSpan);

            _provisioningFailureSettleTimers[categoryName] = timer;

            _logger.Info(
                $"Provisioning failure settle window armed: category={args.Category}, " +
                $"failedSubcategory={args.FailedSubcategory ?? "n/a"}, errorCode={args.ErrorCode ?? "n/a"}, " +
                $"settleSeconds={ProvisioningFailureSettleWindowSeconds}");

            MaybeStartAppxFailureScan(args);
        }

        /// <summary>
        /// Session 2bc884b6 enrichment: on an Apps-subcategory failure, queue a one-shot scan of
        /// the AppX deployment event log so MSIX/Store failures — invisible to ImeLogTracker —
        /// get named while the settle window is still open (the enrichment event lands before
        /// enrollment_failed in the timeline). Fire-once per session: a second Apps failure in
        /// the other category would rescan the same window for no new information.
        /// <para>
        /// Caller holds <see cref="_stateLock"/> — only the QUEUING happens here; the scan body
        /// runs lock-free on the threadpool against an immutable request. The settle-window
        /// expiry never waits for the scan (worst case the enrichment is lost at shutdown —
        /// degraded enrichment only, the failure path is unchanged).
        /// </para>
        /// </summary>
        private void MaybeStartAppxFailureScan(EspFailureDetectedEventArgs args)
        {
            if (!string.Equals(args.FailedSubcategory, "Apps", StringComparison.OrdinalIgnoreCase))
                return;
            if (_appxScanStarted)
                return;
            _appxScanStarted = true;

            var now = DateTime.UtcNow;
            var lookbackCap = now.AddHours(-AppxScanLookbackCapHours);
            var windowStart = _monitoringStartUtc > lookbackCap ? _monitoringStartUtc : lookbackCap;
            var request = new AppxFailureScanRequest(
                windowStartUtc: windowStart,
                windowEndUtc: now,
                espErrorCode: args.ErrorCode,
                espCategory: args.Category,
                failedSubcategory: args.FailedSubcategory);

            _logger.Info(
                $"AppX failure scan queued: category={args.Category ?? "n/a"}, " +
                $"errorCode={args.ErrorCode ?? "n/a"}, windowStartUtc={windowStart:o}");

            _backgroundDispatcher(() => RunAppxFailureScan(request));
        }

        private void RunAppxFailureScan(AppxFailureScanRequest request)
        {
            try
            {
                var scan = _appxScanner.Scan(request);
                var analysis = AppxFailureAnalyzer.Analyze(request, scan);
                EmitAppxFailureAnalysis(request, scan, analysis);
            }
            catch (Exception ex)
            {
                _logger.Error("AppX failure scan failed", ex);
                CollectorDegradationReporter.Report(
                    _post, _sessionId, _tenantId, "AppxDeploymentFailureScanner", "scan_failed", ex);
            }
        }

        /// <summary>
        /// Emits <c>esp_appx_failure_analysis</c>. Fields the RuleEngine matches on are FLAT
        /// top-level (it cannot traverse nested collections — see the failedSubcategories
        /// precedent in EmitProvisioningEvent); the <c>candidates</c> list is UI/MCP detail.
        /// ImmediateUpload: the agent terminates ~30s after the settle window, same reasoning
        /// as esp_apps_failure_correlation in EnrollmentTerminationHandler.
        /// </summary>
        private void EmitAppxFailureAnalysis(
            AppxFailureScanRequest request,
            AppxFailureScanResult scan,
            AppxFailureAnalysis analysis)
        {
            var data = new Dictionary<string, object>
            {
                { "verdict", analysis.Verdict },
                { "confidence", analysis.Confidence },
                { "espCategory", request.EspCategory ?? "unknown" },
                { "failedSubcategory", request.FailedSubcategory ?? "Apps" },
                { "candidateCount", analysis.TotalCandidateCount },
                { "errorEventCount", analysis.ErrorEventCount },
                { "windowStartUtc", request.WindowStartUtc.ToString("o") },
                { "windowEndUtc", request.WindowEndUtc.ToString("o") },
                { "scanDurationMs", scan.ScanDurationMs },
                { "channel", AppxDeploymentFailureScanner.Channel }
            };
            if (request.EspErrorCode != null)
                data["espErrorCode"] = request.EspErrorCode;
            if (analysis.ScanUnavailableReason != null)
                data["scanError"] = analysis.ScanUnavailableReason;
            if (scan.Truncated)
                data["scanTruncated"] = true;
            if (analysis.OtherHresultsSeen.Count > 0)
                data["otherHresultsSeen"] = string.Join(",", analysis.OtherHresultsSeen);

            var top = analysis.Candidates.FirstOrDefault();
            if (top != null)
            {
                data["topCandidatePackage"] = top.PackageFullName;
                data["topCandidatePackageName"] = top.PackageName;
                data["topCandidateMatchType"] = top.MatchType;
                data["topCandidateEventId"] = top.EventId;
                data["topCandidateTimeUtc"] = top.TimeUtc.ToString("o");
                if (top.Hresults.Count > 0)
                    data["topCandidateHresult"] = top.Hresults[0];

                var candidateList = new List<object>();
                foreach (var c in analysis.Candidates)
                {
                    candidateList.Add(new Dictionary<string, object>
                    {
                        { "packageFullName", c.PackageFullName },
                        { "packageName", c.PackageName },
                        { "score", c.Score },
                        { "matchType", c.MatchType },
                        { "hresults", string.Join(",", c.Hresults) },
                        { "eventId", c.EventId },
                        { "timeUtc", c.TimeUtc.ToString("o") },
                        { "occurrences", c.Occurrences },
                        { "messageExcerpt", c.MessageExcerpt }
                    });
                }
                data["candidates"] = candidateList;
            }

            string message;
            var severity = EventSeverity.Info;
            switch (analysis.Verdict)
            {
                case AppxFailureAnalyzer.VerdictCandidateIdentified:
                    severity = EventSeverity.Warning;
                    message = $"ESP Apps failure enrichment: suspected MSIX/Store package '{top.PackageName}'" +
                              (request.EspErrorCode != null ? $" matches ESP error {request.EspErrorCode}" : "") +
                              $" — {analysis.TotalCandidateCount} candidate(s) in AppX deployment log " +
                              "(assessment, not a confirmed root cause)";
                    break;
                case AppxFailureAnalyzer.VerdictActivityNoHresultMatch:
                    message = $"ESP Apps failure enrichment: AppX deployment errors found (top package '{top.PackageName}') " +
                              $"but none match ESP error {request.EspErrorCode ?? "n/a"} — weak correlation " +
                              "(assessment, not a confirmed root cause)";
                    break;
                case AppxFailureAnalyzer.VerdictErrorsNoPackage:
                    message = "ESP Apps failure enrichment: AppX deployment errors in window but no package name " +
                              "extractable — inconclusive";
                    break;
                case AppxFailureAnalyzer.VerdictScanUnavailable:
                    message = $"ESP Apps failure enrichment: AppX deployment log unavailable " +
                              $"({analysis.ScanUnavailableReason}) — no assessment possible";
                    break;
                default: // no_appx_candidates
                    message = "ESP Apps failure enrichment: no AppX deployment errors in window — failure likely " +
                              "originates outside the MSIX/Store pipeline (check Win32/IME apps)";
                    break;
            }

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.EspAppxFailureAnalysis,
                Severity = severity,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = true
            });

            _logger.Info(
                $"AppX failure analysis emitted: verdict={analysis.Verdict}, confidence={analysis.Confidence}, " +
                $"candidates={analysis.TotalCandidateCount}/{analysis.ErrorEventCount} records, " +
                $"durationMs={scan.ScanDurationMs}");
        }

        private void OnProvisioningFailureSettleExpired(string categoryName)
        {
            EspFailureDetectedEventArgs args = null;
            try
            {
                bool recovered;
                string observedState = null;
                lock (_stateLock)
                {
                    if (_provisioningFailureSettleArgs == null
                        || !_provisioningFailureSettleArgs.TryGetValue(categoryName, out args))
                    {
                        _logger.Debug($"Failure settle timer expired but no pending args for {categoryName} — ignoring");
                        return;
                    }
                    _provisioningFailureSettleArgs.Remove(categoryName);

                    if (_provisioningFailureSettleTimers != null
                        && _provisioningFailureSettleTimers.TryGetValue(categoryName, out var timer))
                    {
                        try { timer?.Dispose(); } catch { /* swallow */ }
                        _provisioningFailureSettleTimers.Remove(categoryName);
                    }

                    // Session c071e92b fix: re-check CURRENT registry-derived state before
                    // committing the latched failure. ESP failures can be retracted inside the
                    // window (user clicks "Try again" / IME re-sync flips the subcategory back
                    // to inProgress) — firing the stale args would terminate the agent on a
                    // failure Windows no longer reports, and the retry outcome is lost.
                    recovered = IsSettledFailureRecovered(categoryName, args);
                    if (recovered)
                    {
                        if (args.FailedSubcategory != null
                            && _lastSubcategoryStates.TryGetValue(categoryName, out var subs)
                            && subs.TryGetValue(args.FailedSubcategory, out var current))
                        {
                            observedState = current;
                        }

                        // Clear the fire-once gate so a subsequent re-failure arms a FRESH
                        // settle window (the retry may fail again — that one is terminal).
                        _provisioningFailureFired.Remove(categoryName);
                        // The category is back in progress — it must not count towards the
                        // "all categories resolved with success" self-termination check.
                        if (_lastCategorySucceeded.TryGetValue(categoryName, out var succeededNow)
                            && succeededNow == null)
                        {
                            _provisioningCategoriesResolved.Remove(categoryName);
                        }
                    }
                }

                if (recovered)
                {
                    _logger.Info(
                        $"Provisioning failure settle window elapsed but the failure was retracted — " +
                        $"suppressing EspFailureDetected: category={args.Category}, " +
                        $"failedSubcategory={args.FailedSubcategory ?? "n/a"}, observedState={observedState ?? "n/a"}");
                    EmitFailureSettleRecovered(args, observedState);
                    return;
                }

                _logger.Info(
                    $"Provisioning failure settle window elapsed — firing EspFailureDetected: " +
                    $"category={args.Category}, failedSubcategory={args.FailedSubcategory ?? "n/a"}, " +
                    $"errorCode={args.ErrorCode ?? "n/a"}, failureType={args.FailureType}");

                try { EspFailureDetected?.Invoke(this, args); }
                catch (Exception ex) { _logger.Error($"EspFailureDetected handler failed for '{args.FailureType}'", ex); }
            }
            catch (Exception ex)
            {
                _logger.Error("OnProvisioningFailureSettleExpired callback failed", ex);
            }
        }

        /// <summary>
        /// Session c071e92b fix: true when the failure that armed the settle window is no
        /// longer present in the tracked registry state — categorySucceeded is not false AND
        /// no subcategory of the category is currently "failed". The any-subcategory check
        /// (instead of only the latched one) is deliberate: if the original subcategory
        /// recovered but a different one failed inside the window, the failure is still real
        /// and must fire (with the latched args — statusText/HRESULT of the new subcategory
        /// were never captured). Caller holds <see cref="_stateLock"/>.
        /// </summary>
        private bool IsSettledFailureRecovered(string categoryName, EspFailureDetectedEventArgs args)
        {
            if (_lastCategorySucceeded != null
                && _lastCategorySucceeded.TryGetValue(categoryName, out var succeeded)
                && succeeded == false)
            {
                return false;
            }

            if (_lastSubcategoryStates == null
                || !_lastSubcategoryStates.TryGetValue(categoryName, out var subs)
                || subs == null)
            {
                // No tracked state — conservative: treat as still failed and fire.
                return false;
            }

            foreach (var state in subs.Values)
            {
                if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private void EmitFailureSettleRecovered(EspFailureDetectedEventArgs args, string observedState)
        {
            var data = new Dictionary<string, object>
            {
                { "category", args.Category ?? "unknown" },
                { "failedSubcategory", args.FailedSubcategory ?? "category-level" },
                { "failureType", args.FailureType },
                { "settleSeconds", ProvisioningFailureSettleWindowSeconds },
                { "observedState", observedState ?? "unknown" },
                { "note", "ESP retracted the failure during the settle window (e.g. a 'Try again' retry) — terminal failure suppressed, monitoring continues; a subsequent failure re-arms a fresh settle window." }
            };
            if (!string.IsNullOrEmpty(args.ErrorCode))
                data["errorCode"] = args.ErrorCode;

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.EspFailureSettleRecovered,
                Severity = EventSeverity.Info,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"ESP failure retracted during settle window — {args.Category}/{args.FailedSubcategory ?? "category-level"} " +
                          $"recovered (now {observedState ?? "unknown"}); terminal failure suppressed, monitoring continues.",
                Data = data,
                ImmediateUpload = true
            });
        }

        private void EmitFailureSettleStarted(EspFailureDetectedEventArgs args)
        {
            var data = new Dictionary<string, object>
            {
                { "category", args.Category ?? "unknown" },
                { "failedSubcategory", args.FailedSubcategory ?? "category-level" },
                { "failureType", args.FailureType },
                { "settleSeconds", ProvisioningFailureSettleWindowSeconds },
                { "reason", "wait_for_late_ime_signals" }
            };
            if (!string.IsNullOrEmpty(args.ErrorCode))
                data["errorCode"] = args.ErrorCode;

            var appsSuffix = string.Empty;
            if (string.Equals(args.FailedSubcategory, "Apps", StringComparison.OrdinalIgnoreCase))
            {
                var notCompleted = SnapshotTrackedAppsNotCompleted();
                if (notCompleted.Count > 0)
                {
                    data["trackedAppsNotCompletedCount"] = notCompleted.Count;
                    data["trackedAppsNotCompleted"] = notCompleted;
                    var names = notCompleted
                        .Select(a => $"{a["appName"]} ({a["state"]})");
                    appsSuffix = $" — {notCompleted.Count} tracked app(s) not completed: {string.Join(", ", names)}";
                }
            }

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.EspFailureSettleStarted,
                Severity = EventSeverity.Info,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"ESP failure settle window started ({ProvisioningFailureSettleWindowSeconds}s) — " +
                          $"{args.Category}/{args.FailedSubcategory ?? "category-level"}" +
                          (string.IsNullOrEmpty(args.ErrorCode) ? "" : $" (errorCode={args.ErrorCode})") +
                          appsSuffix,
                Data = data
            });
        }

        /// <summary>
        /// Session c071e92b enrichment: names the tracked apps that are not terminal at the
        /// moment an Apps-subcategory failure is detected. When ESP fails Apps via its
        /// sync-failure timeout, the blocker is often an app IME never surfaced an error for —
        /// e.g. a Store/WinGet app (Company Portal) stuck "pending" with zero install events.
        /// Both existing correlation paths miss exactly that shape: the starved-app probe is
        /// gated on IME-log AccountSetup phase detection + Install intent, and
        /// esp_apps_failure_correlation deliberately only blames actively-installing device
        /// apps. This is registry-driven and unconditional: whatever ESP counted as unfinished
        /// is what the admin needs to see. Best-effort; empty on any probe failure.
        /// </summary>
        private List<Dictionary<string, object>> SnapshotTrackedAppsNotCompleted()
        {
            var result = new List<Dictionary<string, object>>();
            try
            {
                var packages = _packageStatesProbe();
                if (packages == null) return result;

                foreach (var pkg in packages)
                {
                    if (pkg == null || pkg.IsCompleted || pkg.IsError) continue;

                    result.Add(new Dictionary<string, object>
                    {
                        { "appId", pkg.Id ?? string.Empty },
                        { "appName", string.IsNullOrEmpty(pkg.Name) ? (pkg.Id ?? "(unknown)") : pkg.Name },
                        { "state", pkg.InstallationState.ToString() },
                        { "intent", pkg.Intent.ToString() },
                        { "targeted", pkg.Targeted.ToString() },
                        { "everStartedInstalling", pkg.DownloadingOrInstallingSeen }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"SnapshotTrackedAppsNotCompleted probe threw: {ex.Message}");
                result.Clear();
            }
            return result;
        }

        /// <summary>
        /// Extracts a Windows HRESULT (e.g. <c>0x87d1041c</c>) from a subcategory statusText.
        /// Returns the normalised lower-case hex form with <c>0x</c> prefix, or null when no
        /// HRESULT pattern is present. Pattern is language-invariant (parenthesised hex tail).
        /// </summary>
        internal static string TryExtractErrorCode(string statusText)
        {
            if (string.IsNullOrEmpty(statusText)) return null;
            var match = HResultPattern.Match(statusText);
            if (!match.Success) return null;
            var raw = match.Groups[1].Value;
            return "0x" + raw.Substring(2).ToLowerInvariant();
        }

        /// <summary>Test seam — drive the settle-timer callback synchronously.</summary>
        internal void TriggerSettleTimerForTest(string categoryName)
            => OnProvisioningFailureSettleExpired(categoryName);

        private bool HasCategorySucceededChanged(string categoryName, bool? newValue)
        {
            if (!_lastCategorySucceeded.TryGetValue(categoryName, out var oldValue))
                return false;
            return oldValue != newValue;
        }

        private void StoreSubcategoryStates(string categoryName, List<SubcategoryInfo> subcategories)
        {
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subcategories)
                states[sub.Name] = sub.State;
            _lastSubcategoryStates[categoryName] = states;
        }

        private List<SubcategoryTransition> DetectSubcategoryTransitions(string categoryName, List<SubcategoryInfo> subcategories)
        {
            var transitions = new List<SubcategoryTransition>();

            if (!_lastSubcategoryStates.TryGetValue(categoryName, out var lastStates))
                return transitions;

            foreach (var sub in subcategories)
            {
                if (!lastStates.TryGetValue(sub.Name, out var oldState))
                    continue;

                if (string.Equals(oldState, sub.State, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isFailure = string.Equals(sub.State, "failed", StringComparison.OrdinalIgnoreCase);
                bool isCompletion = string.Equals(sub.State, "succeeded", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(oldState, "succeeded", StringComparison.OrdinalIgnoreCase);

                bool isNoise = string.Equals(oldState, "notStarted", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sub.State, "in_progress", StringComparison.OrdinalIgnoreCase);

                if (isFailure || isCompletion)
                {
                    transitions.Add(new SubcategoryTransition(sub.Name, oldState, sub.State, isFailure));
                }
                else if (!isNoise)
                {
                    // Any other real state change (e.g. succeeded -> in_progress retry,
                    // failed -> in_progress recovery, in_progress -> notStarted reset) is a
                    // non-failure transition. Record it so the subcategory_state_change event
                    // carries it; without this the change is silently dropped (observability gap).
                    transitions.Add(new SubcategoryTransition(sub.Name, oldState, sub.State, false));
                }
            }

            return transitions;
        }

        private void EmitProvisioningEvent(string categoryLabel, bool? succeeded, string statusText,
            List<SubcategoryInfo> subcategories, EventSeverity severity,
            List<SubcategoryTransition> transitions = null)
        {
            var eventData = new Dictionary<string, object>
            {
                { "category", categoryLabel },
                { "categorySucceeded", succeeded?.ToString() ?? "in_progress" },
                { "categoryStatusMessage", statusText }
            };

            if (subcategories.Count > 0)
            {
                var subcatData = new Dictionary<string, object>();
                foreach (var sub in subcategories)
                {
                    subcatData[sub.Name] = new Dictionary<string, string>
                    {
                        { "state", sub.State },
                        { "statusText", sub.StatusText }
                    };
                }
                eventData["subcategories"] = subcatData;
            }

            if (transitions != null && transitions.Count > 0)
            {
                eventData["changeType"] = "subcategory_state_change";
                var transitionData = new List<Dictionary<string, string>>();
                foreach (var t in transitions)
                {
                    transitionData.Add(new Dictionary<string, string>
                    {
                        { "subcategory", t.SubcategoryName },
                        { "previousState", t.OldState },
                        { "newState", t.NewState }
                    });
                }
                eventData["transitions"] = transitionData;

                // Flat top-level field for RuleEngine matching — the RuleEngine cannot
                // traverse nested collections via dataField, so we expose the names of
                // subcategories that just transitioned to "failed" as a comma-separated
                // string. Names come from registry keys and are language-invariant.
                var failedNames = transitions
                    .Where(t => t.IsFailure)
                    .Select(t => t.SubcategoryName)
                    .ToList();
                if (failedNames.Count > 0)
                {
                    eventData["failedSubcategories"] = string.Join(",", failedNames);

                    // Session 9d052230: surface HRESULT from the first failed subcategory's
                    // statusText at top-level so the UI / RuleEngine can match it without
                    // parsing nested statusText strings. Pattern is language-invariant.
                    var firstFailedSub = subcategories
                        .FirstOrDefault(s => string.Equals(s.State, "failed", StringComparison.OrdinalIgnoreCase));
                    var firstFailedCode = TryExtractErrorCode(firstFailedSub?.StatusText);
                    if (!string.IsNullOrEmpty(firstFailedCode))
                    {
                        eventData["failedSubcategoryErrorCode"] = firstFailedCode;
                    }
                }
            }

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.EspProvisioningStatus,
                Severity = severity,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"ESP provisioning status: {categoryLabel} — {statusText}",
                Data = eventData
            });

            _logger.Info($"Provisioning status event: {categoryLabel} — {statusText} (succeeded={succeeded?.ToString() ?? "in_progress"})");
        }

        private void EmitRawRegistryDump(string categoryLabel, string rawJson, string trigger)
        {
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.EspProvisioningRaw,
                Severity = EventSeverity.Trace,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"ESP provisioning raw registry: {categoryLabel} ({trigger})",
                Data = new Dictionary<string, object>
                {
                    { "category", categoryLabel },
                    { "trigger", trigger },
                    { "registryValue", $"{categoryLabel}Category.Status" },
                    { "rawJson", rawJson }
                }
            });
        }

        // =====================================================================
        // JSON parsing helpers (static — exposed as internal for tests)
        // =====================================================================

        internal static List<SubcategoryInfo> ParseSubcategories(JsonElement root)
        {
            var result = new List<SubcategoryInfo>();

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.IndexOf("Subcategory", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var name = CleanSubcategoryName(prop.Name);

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        var text = prop.Value.GetString() ?? "";
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = InferStateFromText(text),
                            StatusText = text
                        });
                        break;

                    case JsonValueKind.Object:
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = SafeGetString(prop.Value, "subcategoryState") ?? "unknown",
                            StatusText = SafeGetString(prop.Value, "subcategoryStatusText") ?? ""
                        });
                        break;

                    default:
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = "unknown",
                            StatusText = prop.Value.ToString()
                        });
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Cleans a subcategory property name to a readable short name.
        /// "AccountSetup.CertificatesSubcategory" -> "Certificates"
        /// "SecurityPoliciesSubcategory" -> "SecurityPolicies"
        /// </summary>
        internal static string CleanSubcategoryName(string rawName)
        {
            var name = rawName;

            var idx = name.IndexOf("Subcategory", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                name = name.Substring(0, idx);

            var dotIdx = name.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx < name.Length - 1)
                name = name.Substring(dotIdx + 1);

            return string.IsNullOrEmpty(name) ? rawName : name;
        }

        internal static string InferStateFromText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "unknown";

            if (text.IndexOf("Complete", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("applied", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("installed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("added", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("No setup needed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Identified", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "succeeded";
            }

            if (text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Failure", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "failed";
            }

            return "in_progress";
        }

        internal static string FindFailedSubcategory(List<SubcategoryInfo> subcategories)
            => FindFailedSubcategoryInfo(subcategories)?.Name;

        internal static SubcategoryInfo FindFailedSubcategoryInfo(List<SubcategoryInfo> subcategories)
        {
            foreach (var sub in subcategories)
            {
                if (string.Equals(sub.State, "failed", StringComparison.OrdinalIgnoreCase))
                    return sub;
            }
            return null;
        }

        internal static string BuildProgressSummary(List<SubcategoryInfo> subcategories)
        {
            if (subcategories.Count == 0)
                return "In progress";

            var succeeded = subcategories.Count(s =>
                string.Equals(s.State, "succeeded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.State, "notRequired", StringComparison.OrdinalIgnoreCase));

            var failed = subcategories.Count(s =>
                string.Equals(s.State, "failed", StringComparison.OrdinalIgnoreCase));

            if (failed > 0)
                return $"{failed} of {subcategories.Count} subcategories failed";

            return $"{succeeded} of {subcategories.Count} subcategories completed";
        }

        internal static bool? SafeGetBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return null;

            switch (prop.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String:
                    return bool.TryParse(prop.GetString(), out var parsed) ? parsed : (bool?)null;
                default: return null;
            }
        }

        internal static string SafeGetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return null;

            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }

        // =====================================================================
        // Internal types
        // =====================================================================

        internal class SubcategoryInfo
        {
            public string Name { get; set; }
            public string State { get; set; }      // "succeeded", "failed", "in_progress", "unknown", "notRequired"
            public string StatusText { get; set; }
        }

        internal readonly struct SubcategoryTransition
        {
            public readonly string SubcategoryName;
            public readonly string OldState;
            public readonly string NewState;
            public readonly bool IsFailure;

            public SubcategoryTransition(string subcategoryName, string oldState, string newState, bool isFailure)
            {
                SubcategoryName = subcategoryName;
                OldState = oldState;
                NewState = newState;
                IsFailure = isFailure;
            }
        }

        internal readonly struct ProvisioningResult
        {
            public readonly bool IsFailed;
            public readonly string FailureType;

            private ProvisioningResult(bool isFailed, string failureType)
            {
                IsFailed = isFailed;
                FailureType = failureType;
            }

            public static ProvisioningResult NoAction => new ProvisioningResult(false, null);
            public static ProvisioningResult Failure(string failureType) => new ProvisioningResult(true, failureType);
        }

        // =====================================================================
        // Test seams
        // =====================================================================

        /// <summary>Test-only: drive ProcessCategoryStatus without registry access.</summary>
        internal ProvisioningResult ProcessCategoryStatusForTest(string categoryName, string jsonValue)
        {
            lock (_stateLock)
            {
                EnsureStateDictionariesInitialized();
                return ProcessCategoryStatus(categoryName, jsonValue);
            }
        }

        private void EnsureStateDictionariesInitialized()
        {
            if (_lastProvisioningJson == null)
                _lastProvisioningJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningCategorySeen == null)
                _provisioningCategorySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_lastCategorySucceeded == null)
                _lastCategorySucceeded = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningFailureFired == null)
                _provisioningFailureFired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningCategoriesResolved == null)
                _provisioningCategoriesResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_lastSubcategoryStates == null)
                _lastSubcategoryStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningFailureSettleTimers == null)
                _provisioningFailureSettleTimers = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningFailureSettleArgs == null)
                _provisioningFailureSettleArgs = new Dictionary<string, EspFailureDetectedEventArgs>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Immutable snapshot of ESP provisioning category status at a point in time.
    /// Used to attach to enrollment_complete events and for settle-wait decisions.
    /// </summary>
    public class EspProvisioningSnapshot
    {
        /// <summary>Per-category outcome: "success", "failed", or "in_progress".</summary>
        public Dictionary<string, string> CategoryOutcomes { get; set; }

        /// <summary>Per-category subcategory detail: subcategoryName -> state string.</summary>
        public Dictionary<string, Dictionary<string, string>> SubcategoryStates { get; set; }

        /// <summary>Number of categories seen in the registry.</summary>
        public int CategoriesSeen { get; set; }

        /// <summary>Number of categories that have a final categorySucceeded value.</summary>
        public int CategoriesResolved { get; set; }

        /// <summary>True if all seen categories are resolved (or none seen).</summary>
        public bool AllResolved { get; set; }
    }
}
