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
            _shellCoreTracker.EspFailureDetected += OnShellCoreEspFailureDetected;
            _shellCoreTracker.EspExited += OnEspExited;
            _shellCoreTracker.Start();

            _provisioningTracker = new ProvisioningStatusTracker(
                _sessionId,
                _tenantId,
                _post,
                _logger);
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
            try { EspExited?.Invoke(this, args); }
            catch (Exception ex) { _logger.Error("Error forwarding EspExited", ex); }
            finally { LastEventOccurredAtUtc = null; }
        }
    }
}
