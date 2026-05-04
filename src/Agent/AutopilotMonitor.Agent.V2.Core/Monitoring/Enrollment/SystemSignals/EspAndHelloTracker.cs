using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
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
        /// WhiteGlove_Failed, Provisioning_*_Failed, etc.). The string is the structured failure type.
        /// </summary>
        public event EventHandler<string> EspFailureDetected;

        /// <summary>
        /// Fired when DeviceSetup provisioning status shows categorySucceeded=true (or fallback confirmed).
        /// Used as a completion signal for Self-Deploying mode where Shell-Core ESP exit and
        /// desktop arrival signals may never arrive.
        /// </summary>
        public event EventHandler DeviceSetupProvisioningComplete;

        // PR4 (882fef64 debrief) — coordinator-forwarded copy of <see cref="HelloTracker.HelloPolicyDetected"/>.
        // Args: (helloEnabled, source). Subscribed by EspAndHelloTrackerAdapter to post a
        // DecisionSignalKind.HelloPolicyDetected signal.
        public event Action<bool, string> HelloPolicyDetected;

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
            Func<bool> accountSetupActivityProbe = null)
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
            _shellCoreTracker.EspFailureDetected += OnEspFailureDetected;
            _shellCoreTracker.Start();

            _provisioningTracker = new ProvisioningStatusTracker(
                _sessionId,
                _tenantId,
                _post,
                _logger);
            _provisioningTracker.EspFailureDetected += OnEspFailureDetected;
            _provisioningTracker.DeviceSetupProvisioningComplete += OnDeviceSetupProvisioningComplete;
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
                _shellCoreTracker.EspFailureDetected -= OnEspFailureDetected;
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
                _provisioningTracker.EspFailureDetected -= OnEspFailureDetected;
                _provisioningTracker.DeviceSetupProvisioningComplete -= OnDeviceSetupProvisioningComplete;
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

        private void OnWhiteGloveCompleted(object sender, EventArgs e)
        {
            LastEventOccurredAtUtc = (sender as ShellCoreTracker)?.LastEventOccurredAtUtc;
            try { WhiteGloveCompleted?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding WhiteGloveCompleted", ex); }
            finally { LastEventOccurredAtUtc = null; }
        }

        private void OnEspFailureDetected(object sender, string failureType)
        {
            // EspFailureDetected fires from ShellCoreTracker (carries timestamp) AND
            // ProvisioningStatusTracker (no timestamp surfaced today — adapter falls back
            // to clock). Cast attempt below picks up the timestamp on the ShellCore path.
            LastEventOccurredAtUtc = (sender as ShellCoreTracker)?.LastEventOccurredAtUtc;
            try { EspFailureDetected?.Invoke(this, failureType); }
            catch (Exception ex) { _logger.Error($"Error forwarding EspFailureDetected for '{failureType}'", ex); }
            finally { LastEventOccurredAtUtc = null; }
        }

        private void OnDeviceSetupProvisioningComplete(object sender, EventArgs e)
        {
            // Provisioning tracker doesn't surface a timestamp; adapter falls back to clock.
            LastEventOccurredAtUtc = null;
            try { DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding DeviceSetupProvisioningComplete", ex); }
        }
    }
}
