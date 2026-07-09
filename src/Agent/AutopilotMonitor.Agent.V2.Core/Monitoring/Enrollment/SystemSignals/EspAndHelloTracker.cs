using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Thin coordinator that composes four focused sub-trackers to deliver the ESP + Hello
    /// monitoring surface expected by <c>CollectorCoordinator</c> and <c>EnrollmentTracker</c>:
    ///
    ///   - <see cref="HelloTracker"/>              — WHfB policy, UDR 300/301/358/360/362/376,
    ///                                                HelloForBusiness 3024/6045, Hello timers
    ///   - <see cref="ShellCoreTracker"/>          — Shell-Core 62404/62407 (ESP exit / failure,
    ///                                                WhiteGlove success, Hello wizard start)
    ///   - <see cref="ProvisioningStatusTracker"/> — Registry-driven ESP provisioning category
    ///                                                tracking + DeviceSetup fallback
    ///   - <see cref="ModernDeploymentTracker"/>   — ModernDeployment-Diagnostics-Provider live
    ///                                                capture + WhiteGlove Event 509 backfill
    ///
    /// Public API (events, properties, methods) is preserved byte-for-byte from the pre-split
    /// monolith so callers don't change.
    /// </summary>
    public sealed class EspAndHelloTracker : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;

        private readonly int _helloWaitTimeoutSeconds;
        private readonly bool _modernDeploymentWatcherEnabled;
        private readonly int _modernDeploymentLogLevelMax;
        private readonly bool _modernDeploymentBackfillEnabled;
        private readonly int _modernDeploymentBackfillLookbackMinutes;
        private readonly string _stateDirectory;
        private readonly int[] _modernDeploymentHarmlessEventIds;
        private readonly Func<(bool? skipUser, bool? skipDevice)> _skipConfigProbe;
        private readonly Func<bool> _accountSetupActivityProbe;
        private readonly Func<bool> _userEspAppsSettledProbe;
        private readonly Func<System.Collections.Generic.IReadOnlyList<Ime.AppPackageState>> _starvedUserEspAppsProbe;
        private readonly Func<System.Collections.Generic.IReadOnlyList<Ime.AppPackageState>> _packageStatesProbe;

        // Session caa6cf50 gate-starvation fix (2026-06-11) — fire-once guard for the
        // user-ESP-apps-settled AccountSetup synthesis (see MaybeSynthesizeAccountSetupComplete).
        private bool _userAppsSettledSynthesisFired;

        // Liveness plan PR3 — one-shot-per-appId dedupe for app_install_starved emissions.
        // Written from the Shell-Core watcher thread (esp_exited path), read at termination by
        // the EnrollmentTerminationHandler via StarvedAppsReported — guarded by _starvedLock.
        private readonly System.Collections.Generic.HashSet<string> _starvedAppsReported =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _starvedLock = new object();

        private HelloTracker _helloTracker;
        private ShellCoreTracker _shellCoreTracker;
        private ProvisioningStatusTracker _provisioningTracker;
        private ModernDeploymentTracker _modernDeploymentTracker;

        // Plan §6 Fix 7 — lazy-cached skip-user flag from HKLM\...\FirstSync\SkipUserStatusPage.
        // Read once per tracker lifetime on first esp_exiting forward; null until read.
        private bool? _skipUserEspCached;
        private bool _skipConfigProbed;
        private readonly object _skipConfigLock = new object();

        /// <summary>
        /// Callback invoked when Hello provisioning completes (successfully, failed, skipped, or timeout).
        /// Based on events 300/301/362 only — NOT on event 360 (which is just a snapshot).
        /// </summary>
        public event EventHandler HelloCompleted;

        /// <summary>
        /// Callback invoked when ESP exit or Hello wizard start is detected.
        /// Triggers transition to FinalizingSetup phase in <c>EnrollmentTracker</c>.
        /// </summary>
        public event EventHandler<string> FinalizingSetupPhaseTriggered;

        /// <summary>
        /// Fired when WhiteGlove (Pre-Provisioning) completes successfully.
        /// The device will shut down; the agent should terminate gracefully.
        /// </summary>
        public event EventHandler WhiteGloveCompleted;

        /// <summary>
        /// Fired when an ESP failure is detected (ESPProgress_Failure, _Timeout, _Abort,
        /// WhiteGlove_Failed, Provisioning_*_Failed, etc.).
        /// <para>
        /// <see cref="EspFailureDetectedEventArgs.FailureType"/> carries the structured failure
        /// identifier. Registry-derived failures from <see cref="ProvisioningStatusTracker"/>
        /// additionally enrich <see cref="EspFailureDetectedEventArgs.ErrorCode"/>,
        /// <see cref="EspFailureDetectedEventArgs.FailedSubcategory"/>, and
        /// <see cref="EspFailureDetectedEventArgs.Category"/>; event-log-derived failures from
        /// <see cref="ShellCoreTracker"/> only set <see cref="EspFailureDetectedEventArgs.FailureType"/>.
        /// </para>
        /// </summary>
        public event EventHandler<EspFailureDetectedEventArgs> EspFailureDetected;

        /// <summary>
        /// Fired when DeviceSetup provisioning status shows categorySucceeded=true (or fallback confirmed).
        /// Used as a completion signal for Self-Deploying mode where Shell-Core ESP exit and
        /// desktop arrival signals may never arrive.
        /// </summary>
        public event EventHandler DeviceSetupProvisioningComplete;

        /// <summary>
        /// Session 330f73f3 fix: fired when AccountSetup provisioning resolves with success
        /// (or the fallback confirmed). Forwarded from <see cref="ProvisioningStatusTracker"/>;
        /// the V2 adapter posts it as <c>DecisionSignalKind.AccountSetupProvisioningComplete</c>
        /// so the reducer's <c>ShouldTransitionToAwaitingHello</c> gate has the strong
        /// post-AccountSetup fact and stops promoting on intermediate Shell-Core 62407 events.
        /// </summary>
        public event EventHandler AccountSetupProvisioningComplete;

        // PR4 (882fef64 debrief) — coordinator-forwarded copy of <see cref="HelloTracker.HelloPolicyDetected"/>.
        // Args: (helloEnabled, source). Subscribed by EspAndHelloTrackerAdapter to post a
        // DecisionSignalKind.HelloPolicyDetected signal.
        public event Action<bool, string> HelloPolicyDetected;

        // Coordinator-forwarded ESP exit (from <see cref="ShellCoreTracker.EspExited"/>). Carries
        // the source-event timestamp on the args so the adapter can post a
        // <c>DecisionSignalKind.EspExiting</c> signal with the historical instant on backfill.
        // No dedup at this layer — Shell-Core 62407 fires at every ESP phase transition
        // (Device→Account, Account→End), and the reducer's <c>ShouldTransitionToAwaitingHello</c>
        // guard distinguishes the genuine post-AccountSetup exit from intermediate ones.
        public event EventHandler<EspExitedEventArgs> EspExited;

        /// <summary>
        /// UTC timestamp of the most recent sub-tracker event currently being raised
        /// (mirrored from <see cref="ShellCoreTracker.LastEventOccurredAtUtc"/> for ShellCore-
        /// originated events). Read by <c>EspAndHelloTrackerAdapter</c> inside its synchronous
        /// event handler so the emitted DecisionSignal carries the source-event time rather
        /// than wall-clock-now — critical on the backfill path. Null when not currently
        /// inside a forwarded event invocation, or when the originating sub-tracker doesn't
        /// surface a timestamp (Hello / Provisioning trackers fall back to clock at the
        /// adapter).
        /// </summary>
        public DateTime? LastEventOccurredAtUtc { get; private set; }

        /// <summary>
        /// Session 080edee9 follow-up + Codex review (P2/P3, 2026-05-28) — last
        /// ESP failure context observed via <see cref="ProvisioningStatusTracker"/>
        /// (registry-derived; carries HRESULT, failed subcategory, and category as
        /// a single immutable snapshot). Read by <c>EnrollmentTerminationHandler</c>
        /// via <see cref="EspAndHelloHost"/> + <c>DefaultComponentFactory</c> so the
        /// late "Installing-→-Error" promotion can classify the failure correctly
        /// AND refuse to promote when the failure originated outside the Apps
        /// subcategory (a non-Apps HRESULT does not describe per-app outcome).
        /// Null when no HRESULT-carrying ESP failure has fired (or all observed
        /// failures came from ShellCoreTracker, which has no HRESULT surface).
        /// </summary>
        public Termination.EspTerminalFailureSnapshot LastEspTerminalFailure { get; private set; }

        /// <summary>
        /// Outcome of Hello provisioning. Set when Hello resolves (via events, timeout, or not configured).
        /// Values: "completed", "skipped", "timeout", "not_configured", "wizard_not_started", null (not yet resolved).
        /// </summary>
        public string HelloOutcome => _helloTracker?.HelloOutcome;

        /// <summary>
        /// True when Windows Hello for Business policy is configured (enabled or disabled).
        /// </summary>
        public bool IsPolicyConfigured => _helloTracker?.IsPolicyConfigured ?? false;

        /// <summary>
        /// True when Windows Hello provisioning has completed (successfully, failed, or skipped).
        /// </summary>
        public bool IsHelloCompleted => _helloTracker?.IsHelloCompleted ?? false;

        /// <summary>
        /// True when DeviceSetupCategory.Status has resolved categorySucceeded=true OR the fallback confirmed.
        /// </summary>
        public bool DeviceSetupCategorySucceeded => _provisioningTracker?.DeviceSetupCategorySucceeded ?? false;

        /// <summary>
        /// True when any AccountSetup subcategory has been tracked (resolved or in progress).
        /// </summary>
        public bool HasAccountSetupActivity => _provisioningTracker?.HasAccountSetupActivity ?? false;

        /// <summary>
        /// True once WhiteGlove start has been detected (EventID 509 or persisted from prior run).
        /// Forwarded from ModernDeploymentTracker.
        /// </summary>
        public bool IsWhiteGloveStartDetected => _modernDeploymentTracker?.IsWhiteGloveStartDetected ?? false;

        /// <summary>
        /// True when DeviceSetup contains a SaveWhiteGloveSuccessResult subcategory with state "succeeded".
        /// This is a definitive WhiteGlove (Pre-Provisioning) confirmation signal from the ESP registry.
        /// </summary>
        public bool HasSaveWhiteGloveSuccessResult => _provisioningTracker?.HasSaveWhiteGloveSuccessResult ?? false;

        public EspAndHelloTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int helloWaitTimeoutSeconds = 30,
            bool modernDeploymentWatcherEnabled = true,
            int modernDeploymentLogLevelMax = 3,
            bool modernDeploymentBackfillEnabled = true,
            int modernDeploymentBackfillLookbackMinutes = 30,
            string stateDirectory = null,
            int[] modernDeploymentHarmlessEventIds = null,
            Func<(bool? skipUser, bool? skipDevice)> skipConfigProbe = null,
            Func<bool> accountSetupActivityProbe = null,
            Func<bool> userEspAppsSettledProbe = null,
            Func<System.Collections.Generic.IReadOnlyList<Ime.AppPackageState>> starvedUserEspAppsProbe = null,
            Func<System.Collections.Generic.IReadOnlyList<Ime.AppPackageState>> packageStatesProbe = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloWaitTimeoutSeconds = helloWaitTimeoutSeconds;
            _modernDeploymentWatcherEnabled = modernDeploymentWatcherEnabled;
            _modernDeploymentLogLevelMax = modernDeploymentLogLevelMax;
            _modernDeploymentBackfillEnabled = modernDeploymentBackfillEnabled;
            _modernDeploymentBackfillLookbackMinutes = modernDeploymentBackfillLookbackMinutes;
            _stateDirectory = stateDirectory != null ? Environment.ExpandEnvironmentVariables(stateDirectory) : null;
            _modernDeploymentHarmlessEventIds = modernDeploymentHarmlessEventIds;
            // Test seam: allow injection of a fake skip-config reader; defaults to the real
            // registry probe so production never has to think about this parameter.
            _skipConfigProbe = skipConfigProbe ?? (() => EspSkipConfigurationProbe.Read(_logger));
            // Test seam: allow injection of a fake AccountSetup-activity probe. Production uses
            // the provisioning tracker's live registry-derived flag.
            _accountSetupActivityProbe = accountSetupActivityProbe ?? (() => _provisioningTracker?.HasAccountSetupActivity == true);
            // Session caa6cf50 gate-starvation fix: IME-side "all tracked user-ESP apps terminal"
            // probe (wired to ImeLogHost.AreUserEspAppsSettled by DefaultComponentFactory).
            // Defaults to "not settled" so single-tracker wiring scenarios keep prior behaviour.
            _userEspAppsSettledProbe = userEspAppsSettledProbe ?? (() => false);
            // Liveness plan PR3: IME-side starved-apps probe (wired to
            // ImeLogHost.GetStarvedUserEspApps by DefaultComponentFactory). Defaults to empty
            // so single-tracker wiring scenarios never emit app_install_starved.
            _starvedUserEspAppsProbe = starvedUserEspAppsProbe
                ?? (() => Array.Empty<Ime.AppPackageState>());
            // Session c071e92b enrichment: IME package-state probe (wired to
            // ImeLogHost.AllKnownPackageStates by DefaultComponentFactory) — lets the
            // provisioning tracker name not-completed apps on an Apps-subcategory failure.
            _packageStatesProbe = packageStatesProbe
                ?? (() => Array.Empty<Ime.AppPackageState>());
        }

        /// <summary>
        /// Liveness plan PR3 — appIds already reported via <c>app_install_starved</c> on the
        /// live (esp_exited) path. Read by the <c>EnrollmentTerminationHandler</c> terminal
        /// sweep so the two emission paths never double-report an app. Snapshot copy.
        /// </summary>
        public System.Collections.Generic.IReadOnlyCollection<string> StarvedAppsReported
        {
            get
            {
                lock (_starvedLock)
                {
                    return new System.Collections.Generic.List<string>(_starvedAppsReported);
                }
            }
        }

        /// <summary>
        /// L6 (delta review 2026-07-02): atomic test-and-add against the same dedupe set the
        /// live (esp_exited) path uses. The termination sweep claims each app through this
        /// instead of snapshotting <see cref="StarvedAppsReported"/> — a snapshot could race a
        /// concurrent live emission between copy and emit and double-report the app.
        /// </summary>
        public bool TryClaimStarvedAppReport(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            lock (_starvedLock)
            {
                return _starvedAppsReported.Add(appId);
            }
        }

        // =====================================================================
        // Forwarded state/snapshot methods
        // =====================================================================

        public System.Collections.Generic.Dictionary<string, bool?> GetProvisioningCategorySnapshot()
            => _provisioningTracker?.GetProvisioningCategorySnapshot()
               ?? new System.Collections.Generic.Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        public EspProvisioningSnapshot GetProvisioningSnapshot() => _provisioningTracker?.GetProvisioningSnapshot();

        // =====================================================================
        // Forwarded Hello coordination API
        // =====================================================================

        /// <summary>
        /// Force-marks Hello as completed from an external caller (e.g. safety timeout in EnrollmentTracker).
        /// Does NOT invoke the HelloCompleted event — the caller handles completion logic directly.
        /// </summary>
        public void ForceMarkHelloCompleted(string reason) => _helloTracker?.ForceMarkHelloCompleted(reason);

        /// <summary>
        /// Starts the Hello wait timer. Called by EnrollmentTracker when AccountSetup phase exits.
        /// </summary>
        public void StartHelloWaitTimer() => _helloTracker?.StartHelloWaitTimer();

        /// <summary>
        /// Resets Hello tracking state when ESP resumes after a mid-enrollment reboot (hybrid join).
        /// </summary>
        public void ResetForEspResumption() => _helloTracker?.ResetForEspResumption();

        /// <summary>
        /// Backfills recent ESP exit and failure events from Shell-Core log on startup.
        /// Secondary recovery mechanism when state persistence is unavailable.
        /// </summary>
        public void BackfillRecentEspExitEvents() => _shellCoreTracker?.BackfillRecentEspExitEvents();

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Start()
        {
            _logger.Info("Starting ESP and Hello tracker");

            _helloTracker = new HelloTracker(
                _sessionId,
                _tenantId,
                _post,
                _logger,
                _helloWaitTimeoutSeconds);
            _helloTracker.HelloCompleted += OnHelloCompleted;
            _helloTracker.HelloPolicyDetected += OnHelloPolicyDetected;
            _helloTracker.Start();

            _shellCoreTracker = new ShellCoreTracker(
                _sessionId,
                _tenantId,
                _post,
                _logger,
                _helloTracker);
            _shellCoreTracker.FinalizingSetupPhaseTriggered += OnFinalizingSetupPhaseTriggered;
            _shellCoreTracker.WhiteGloveCompleted += OnWhiteGloveCompleted;
            _shellCoreTracker.EspFailureDetected += OnShellCoreEspFailureDetected;
            _shellCoreTracker.EspExited += OnEspExited;
            _shellCoreTracker.Start();

            _provisioningTracker = new ProvisioningStatusTracker(
                _sessionId,
                _tenantId,
                _post,
                _logger,
                packageStatesProbe: _packageStatesProbe);
            _provisioningTracker.EspFailureDetected += OnProvisioningEspFailureDetected;
            _provisioningTracker.DeviceSetupProvisioningComplete += OnDeviceSetupProvisioningComplete;
            _provisioningTracker.AccountSetupProvisioningComplete += OnAccountSetupProvisioningComplete;
            _provisioningTracker.Start();

            if (_modernDeploymentWatcherEnabled)
            {
                _modernDeploymentTracker = new ModernDeploymentTracker(
                    _sessionId,
                    _tenantId,
                    _post,
                    _logger,
                    _modernDeploymentLogLevelMax,
                    _modernDeploymentBackfillEnabled,
                    _modernDeploymentBackfillLookbackMinutes,
                    _stateDirectory,
                    _modernDeploymentHarmlessEventIds);
                _modernDeploymentTracker.Start();
            }
        }

        public void Stop()
        {
            _logger.Info("Stopping ESP and Hello tracker");

            DisposeTracker(ref _modernDeploymentTracker, "ModernDeployment", t => t.Stop());
            DisposeProvisioningTracker();
            DisposeShellCoreTracker();
            DisposeHelloTracker();
        }

        public void Dispose() => Stop();

        private void DisposeHelloTracker()
        {
            if (_helloTracker == null) return;
            try
            {
                _helloTracker.HelloCompleted -= OnHelloCompleted;
                _helloTracker.HelloPolicyDetected -= OnHelloPolicyDetected;
                _helloTracker.Stop();
            }
            catch (Exception ex) { _logger.Error("Error stopping Hello tracker", ex); }
            _helloTracker = null;
        }

        private void DisposeShellCoreTracker()
        {
            if (_shellCoreTracker == null) return;
            try
            {
                _shellCoreTracker.FinalizingSetupPhaseTriggered -= OnFinalizingSetupPhaseTriggered;
                _shellCoreTracker.WhiteGloveCompleted -= OnWhiteGloveCompleted;
                _shellCoreTracker.EspFailureDetected -= OnShellCoreEspFailureDetected;
                _shellCoreTracker.EspExited -= OnEspExited;
                _shellCoreTracker.Stop();
            }
            catch (Exception ex) { _logger.Error("Error stopping Shell-Core tracker", ex); }
            _shellCoreTracker = null;
        }

        private void DisposeProvisioningTracker()
        {
            if (_provisioningTracker == null) return;
            try
            {
                _provisioningTracker.EspFailureDetected -= OnProvisioningEspFailureDetected;
                _provisioningTracker.DeviceSetupProvisioningComplete -= OnDeviceSetupProvisioningComplete;
                _provisioningTracker.AccountSetupProvisioningComplete -= OnAccountSetupProvisioningComplete;
                _provisioningTracker.Stop("tracker_stopped");
            }
            catch (Exception ex) { _logger.Error("Error stopping provisioning status tracker", ex); }
            _provisioningTracker = null;
        }

        private void DisposeTracker<T>(ref T tracker, string name, Action<T> stopper) where T : class
        {
            if (tracker == null) return;
            try { stopper(tracker); }
            catch (Exception ex) { _logger.Error($"Error stopping {name} tracker", ex); }
            tracker = null;
        }

        // =====================================================================
        // Event forwarders
        // =====================================================================

        private void OnHelloCompleted(object sender, EventArgs e)
        {
            // HelloTracker doesn't currently surface a per-event timestamp; adapter falls
            // back to clock for Hello-originated events. Set null explicitly so a stale
            // ShellCore value cannot bleed across.
            LastEventOccurredAtUtc = null;
            try { HelloCompleted?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding HelloCompleted", ex); }
        }

        private void OnHelloPolicyDetected(bool helloEnabled, string source)
        {
            LastEventOccurredAtUtc = null;
            try { HelloPolicyDetected?.Invoke(helloEnabled, source); }
            catch (Exception ex) { _logger.Error("Error forwarding HelloPolicyDetected", ex); }
        }

        private void OnFinalizingSetupPhaseTriggered(object sender, string reason)
        {
            // Plan §6 Fix 7 — on Classic V1 enrollments (SkipUser=false) Shell-Core emits TWO
            // esp_exiting (62407) events: one when Device-ESP hands off to Account-ESP, and one
            // at the true final exit. Forwarding the first one as EspPhaseChanged(Finalizing)
            // drives the reducer into AwaitingHello prematurely and arms HelloSafety from the
            // wrong baseline (see session 30410cd7 where the deadline would have fired first).
            // Swallow the intermediate forward when SkipUser is explicitly false AND the
            // provisioning tracker has not yet seen any AccountSetup activity.
            if (string.Equals(reason, "esp_exiting", StringComparison.OrdinalIgnoreCase)
                && IsIntermediateDeviceEspExit())
            {
                _logger.Info(
                    "EspAndHelloTracker: swallowing intermediate Device-ESP esp_exiting (SkipUser=false, no AccountSetup activity yet) — waiting for real final exit");
                return;
            }

            // Mirror the originating ShellCoreTracker's source-event timestamp so adapters
            // can read it via this coordinator's LastEventOccurredAtUtc. Backfill path keeps
            // the historical timestamp; live path matches record.TimeCreated.
            LastEventOccurredAtUtc = (sender as ShellCoreTracker)?.LastEventOccurredAtUtc;
            try { FinalizingSetupPhaseTriggered?.Invoke(this, reason); }
            catch (Exception ex) { _logger.Error("Error forwarding FinalizingSetupPhaseTriggered", ex); }
            finally { LastEventOccurredAtUtc = null; }
        }

        /// <summary>
        /// Plan §6 Fix 7 — returns <c>true</c> when the current <c>esp_exiting</c> forward
        /// should be suppressed because it is the Device-ESP intermediate exit on a Classic
        /// (SkipUser=false) enrollment that has not yet reached the AccountSetup phase.
        /// <para>
        /// Returns <c>false</c> (i.e. "forward the signal") when any of the following hold:
        /// </para>
        /// <list type="bullet">
        ///   <item>SkipUser is unknown or explicitly <c>true</c> (device-only / SkipUser flow —
        ///         AwaitingHello is legitimately reachable directly after Device-ESP).</item>
        ///   <item>The provisioning tracker has observed AccountSetup activity (registry JSON
        ///         under <c>AccountSetupCategory.Status</c> has surfaced at least one subcategory),
        ///         i.e. the current exit is the final post-Account-ESP one.</item>
        /// </list>
        /// </summary>
        private bool IsIntermediateDeviceEspExit()
        {
            // If AccountSetup activity has appeared, we're at the true final exit.
            bool accountSetupSeen;
            try { accountSetupSeen = _accountSetupActivityProbe(); }
            catch (Exception ex)
            {
                _logger.Debug($"EspAndHelloTracker: account-setup-activity probe threw: {ex.Message}");
                accountSetupSeen = false;
            }
            if (accountSetupSeen) return false;

            var skipUser = GetSkipUserEspCached();

            // Unknown or explicitly-skipping => forward as before. Only block when we are
            // certain the enrollment does NOT skip the user ESP (i.e. a second esp_exiting is
            // expected).
            return skipUser == false;
        }

        private bool? GetSkipUserEspCached()
        {
            lock (_skipConfigLock)
            {
                if (_skipConfigProbed) return _skipUserEspCached;
                _skipConfigProbed = true;
                try
                {
                    var (skipUser, _) = _skipConfigProbe();
                    _skipUserEspCached = skipUser;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"EspAndHelloTracker: skip-config probe threw: {ex.Message}");
                    _skipUserEspCached = null;
                }
                return _skipUserEspCached;
            }
        }

        // Test seam — invokes the guarded forward path without driving the underlying ShellCore
        // event-log watcher. Mirrors the arg signature of the live event handler. Internal so
        // only the in-repo test project (InternalsVisibleTo) can use it.
        internal void TriggerFinalizingSetupPhaseForTest(string reason) =>
            OnFinalizingSetupPhaseTriggered(this, reason);

        // Test seam for EspExited — invokes the inner-handler with the exact signature the live
        // ShellCoreTracker.EspExited event raises. Drives the full coordinator re-raise path so
        // tests can assert LastEventOccurredAtUtc mirroring + the public EspExited event fire
        // without needing a real ShellCoreTracker + Event-Log watcher.
        internal void TriggerEspExitedForTest(DateTime occurredAtUtc) =>
            OnEspExited(this, new EspExitedEventArgs(occurredAtUtc));

        private void OnWhiteGloveCompleted(object sender, EventArgs e)
        {
            LastEventOccurredAtUtc = (sender as ShellCoreTracker)?.LastEventOccurredAtUtc;
            try { WhiteGloveCompleted?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding WhiteGloveCompleted", ex); }
            finally { LastEventOccurredAtUtc = null; }
        }

        // Forwarder for ShellCoreTracker (event-log-derived ESP failures, e.g. Shell-Core 62407
        // failure descriptions). Source-event timestamp is mirrored so the adapter can stamp the
        // DecisionSignal with the historical instant on backfill. ShellCoreTracker has no HRESULT
        // surface, so only FailureType is set on the args.
        private void OnShellCoreEspFailureDetected(object sender, string failureType)
        {
            LastEventOccurredAtUtc = (sender as ShellCoreTracker)?.LastEventOccurredAtUtc;
            try
            {
                EspFailureDetected?.Invoke(this, new EspFailureDetectedEventArgs(failureType));
            }
            catch (Exception ex) { _logger.Error($"Error forwarding ShellCore EspFailureDetected for '{failureType}'", ex); }
            finally { LastEventOccurredAtUtc = null; }
        }

        // Forwarder for ProvisioningStatusTracker (registry-derived ESP failures). Args carry the
        // full failure detail (FailureType, ErrorCode, FailedSubcategory, Category) extracted
        // from the failed subcategory's statusText. Provisioning has no source-event timestamp
        // surfaced today, so the adapter falls back to clock.
        private void OnProvisioningEspFailureDetected(object sender, EspFailureDetectedEventArgs args)
        {
            LastEventOccurredAtUtc = null;
            // Session 080edee9 follow-up + Codex review (P2/P3) — snapshot the full
            // failure context (HRESULT + failedSubcategory + category) before
            // forwarding so EnrollmentTerminationHandler can read it on the terminal-
            // failure pathway. Only update when the args carry at least one of the
            // three fields, so a later ShellCore-derived failure (which has none of
            // them) cannot wipe out the registry snapshot. We deliberately do NOT
            // gate on ErrorCode alone — a non-Apps subcategory failure without
            // HRESULT is still useful for downstream "should we promote?" gating.
            if (!string.IsNullOrEmpty(args?.ErrorCode)
                || !string.IsNullOrEmpty(args?.FailedSubcategory)
                || !string.IsNullOrEmpty(args?.Category))
            {
                LastEspTerminalFailure = new Termination.EspTerminalFailureSnapshot(
                    errorCode: args!.ErrorCode,
                    failedSubcategory: args.FailedSubcategory,
                    category: args.Category);
            }
            try
            {
                EspFailureDetected?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger.Error(
                    $"Error forwarding Provisioning EspFailureDetected for '{args?.FailureType ?? "n/a"}'",
                    ex);
            }
        }

        private void OnDeviceSetupProvisioningComplete(object sender, EventArgs e)
        {
            // Provisioning tracker doesn't surface a timestamp; adapter falls back to clock.
            LastEventOccurredAtUtc = null;
            try { DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding DeviceSetupProvisioningComplete", ex); }
        }

        private void OnAccountSetupProvisioningComplete(object sender, EventArgs e)
        {
            // Mirror of DeviceSetup path. Adapter falls back to clock for the signal timestamp.
            LastEventOccurredAtUtc = null;
            try { AccountSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding AccountSetupProvisioningComplete", ex); }
        }

        private void OnEspExited(object sender, EspExitedEventArgs args)
        {
            // EspExited carries its source-event timestamp on the args (set by ShellCoreTracker
            // for both live and backfill records). Mirror onto LastEventOccurredAtUtc so the
            // adapter's ResolveOccurredAt helper picks it up the same way it does for the other
            // ShellCore-originated forwards. Unlike FinalizingSetupPhaseTriggered("esp_exiting")
            // this path is NOT filtered by IsIntermediateDeviceEspExit — the reducer-side
            // ShouldTransitionToAwaitingHello guard decides whether HelloSafety arms.
            LastEventOccurredAtUtc = args?.OccurredAtUtc;
            try
            {
                try { EspExited?.Invoke(this, args); }
                catch (Exception ex) { _logger.Error("Error forwarding EspExited", ex); }

                // Session caa6cf50 gate-starvation fix: a Shell-Core normal exit while IME's
                // user-ESP app tracking is fully settled is alternative evidence that
                // AccountSetup completed. Raised AFTER the EspExited forward so the reducer
                // records EspFinalExitUtc first and the deferred-promote path in
                // HandleAccountSetupProvisioningCompleteV1 takes over.
                MaybeSynthesizeAccountSetupCompleteFromSettledUserApps();
            }
            finally { LastEventOccurredAtUtc = null; }
        }

        /// <summary>
        /// Session caa6cf50 gate-starvation fix (2026-06-11). When a user-ESP app is
        /// policy-skipped by IME, Windows leaves the ESP registry's Apps subcategory stuck at
        /// <c>inProgress</c> ("Apps (N of M installed)") and never writes
        /// <c>AccountSetupCategory.Status.categorySucceeded</c> before tearing down the ESP page
        /// — after the page exits those values are never updated again. Both registry-driven
        /// <see cref="ProvisioningStatusTracker.AccountSetupProvisioningComplete"/> paths
        /// (normal + all-subcategories fallback) are then permanently starved, the reducer's
        /// strong post-AccountSetup gate never opens, and the session stalls despite a fully
        /// successful enrollment.
        /// <para>
        /// This synthesis accepts the equivalent evidence pair instead: Shell-Core 62407 normal
        /// exit (the caller) + all tracked user-ESP apps terminal with zero failures (the
        /// injected IME probe). Fire-once; the registry path's tracker-level dedup and the
        /// adapter's fire-once flag make a duplicate raise benign either way. Never fires while
        /// any user-phase app is failed or still in flight — those sessions keep today's
        /// conservative stall behaviour.
        /// </para>
        /// </summary>
        private void MaybeSynthesizeAccountSetupCompleteFromSettledUserApps()
        {
            if (_userAppsSettledSynthesisFired) return;

            bool settled;
            try { settled = _userEspAppsSettledProbe(); }
            catch (Exception ex)
            {
                _logger.Debug($"EspAndHelloTracker: user-ESP-apps-settled probe threw: {ex.Message}");
                return;
            }
            if (!settled)
            {
                // Liveness plan PR3: the ESP page is gone but the user-apps gate is NOT settled
                // — name the app(s) that never started installing instead of leaving the
                // operator with an anonymous "session hangs in AccountSetup". One-shot per
                // appId; apps that are alive (Downloading/Installing) or failed are excluded
                // by the probe itself.
                EmitStarvedUserEspApps(trigger: "esp_exited_user_apps_not_settled");
                return;
            }

            _userAppsSettledSynthesisFired = true;
            _logger.Warning(
                "EspAndHelloTracker: ESP exited normally with all tracked user-ESP apps terminal (0 failed) " +
                "but AccountSetupCategory.Status never confirmed categorySucceeded — treating AccountSetup as complete " +
                "(user-apps-settled synthesis)");

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.EspProvisioningStatus,
                Severity = EventSeverity.Warning,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = "ESP provisioning status: AccountSetup — ESP page exited normally and all tracked " +
                          "user-ESP apps reached a terminal state (0 failed) but categorySucceeded was never " +
                          "confirmed by Windows — treating as complete (user-apps-settled synthesis)",
                Data = new Dictionary<string, object>
                {
                    { "category", "AccountSetup" },
                    { "categorySucceeded", "in_progress" },
                    { "fallbackApplied", true },
                    { "fallbackReason", "esp_exited_user_apps_settled_category_unresolved" },
                }
            });

            try { AccountSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("AccountSetupProvisioningComplete handler failed (user-apps-settled synthesis)", ex); }
        }

        /// <summary>
        /// Liveness plan PR3 — emit a one-shot <c>app_install_starved</c> Warning per starved
        /// user-ESP app (required, never started installing, not failed). Best-effort: probe or
        /// emit failures are logged and swallowed; a failed emit keeps the appId out of the
        /// dedupe set so the termination sweep can retry.
        /// </summary>
        private void EmitStarvedUserEspApps(string trigger)
        {
            System.Collections.Generic.IReadOnlyList<Ime.AppPackageState> starved;
            try { starved = _starvedUserEspAppsProbe(); }
            catch (Exception ex)
            {
                _logger.Debug($"EspAndHelloTracker: starved-apps probe threw: {ex.Message}");
                return;
            }
            if (starved == null || starved.Count == 0) return;

            foreach (var app in starved)
            {
                if (app?.Id == null) continue;
                lock (_starvedLock)
                {
                    if (!_starvedAppsReported.Add(app.Id)) continue;
                }

                try
                {
                    var name = string.IsNullOrEmpty(app.Name) ? app.Id : app.Name;
                    _logger.Warning(
                        $"EspAndHelloTracker: required user-ESP app '{name}' ({app.Id}) never started installing " +
                        $"(state={app.InstallationState}) — starving the AccountSetup apps gate (trigger={trigger}).");

                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.AppInstallStarved,
                        Severity = EventSeverity.Warning,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Required app '{name}' never started installing while the ESP AccountSetup " +
                                  "apps gate waited on it — the app is starving the enrollment completion.",
                        Data = new Dictionary<string, object>
                        {
                            { "appId", app.Id },
                            { "appName", name },
                            { "state", app.InstallationState.ToString() },
                            { "intent", app.Intent.ToString() },
                            { "targeted", app.Targeted.ToString() },
                            { "trigger", trigger },
                        },
                        ImmediateUpload = true,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error($"EspAndHelloTracker: app_install_starved emit failed for '{app.Id}'", ex);
                    lock (_starvedLock) { _starvedAppsReported.Remove(app.Id); }
                }
            }
        }

        // Test seam for the user-apps-settled synthesis — drives OnEspExited with the live
        // handler signature, mirroring TriggerEspExitedForTest's contract.
        internal bool UserAppsSettledSynthesisFiredForTest => _userAppsSettledSynthesisFired;
    }
}
