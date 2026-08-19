#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class EspAndHelloHost : ICollectorHost, IWhiteGloveCompletedSource, IDeviceSetupCompletedSource
    {
        public string Name => "EspAndHelloTracker";

        private readonly EspAndHelloTracker _tracker;
        private readonly EspAndHelloTrackerAdapter _adapter;
        private readonly int _espExitBackfillLookbackMinutes;
        private int _disposed;

        /// <summary>
        /// Forwarded from the internal <see cref="EspAndHelloTracker.WhiteGloveCompleted"/>
        /// event so external components (e.g. <c>WhiteGloveInventoryTrigger</c>) can react
        /// to WhiteGlove pre-provisioning success without needing direct access to the
        /// (private) tracker. Fires after the agent observes Windows Event 62407
        /// ("BootstrapStatus: Exiting page due to White Glove success") — i.e. while the
        /// "Continue / Reseal" dialog is shown but BEFORE the admin clicks Reseal and the
        /// Sysprep reboot fires. This is the only window in which WhiteGlove Part 1 work
        /// (e.g. inventory snapshot for vulnerability correlation) can run.
        /// </summary>
        public event EventHandler? WhiteGloveCompleted;

        /// <summary>
        /// Forwarded from the internal <see cref="EspAndHelloTracker.DeviceSetupProvisioningComplete"/>
        /// event so external components (e.g. <c>AutoLogonDeviceSetupTrigger</c>) can react to the
        /// end of the ESP device phase without reaching into the (private) tracker. Fires once when
        /// DeviceSetup provisioning resolves with success (or the fallback confirmed).
        /// </summary>
        public event EventHandler? DeviceSetupProvisioningComplete;

        /// <summary>
        /// Session 080edee9 follow-up + Codex review (P2/P3, 2026-05-28) — forwards
        /// <see cref="EspAndHelloTracker.LastEspTerminalFailure"/>. Read by
        /// <see cref="DefaultComponentFactory.LastEspTerminalFailure"/> which is in
        /// turn queried by
        /// <c>EnrollmentTerminationHandler.MaybePromoteActiveInstallsAsStuck</c> to
        /// classify ESP failures correctly (HRESULT-based detection-failure /
        /// install-failure ONLY when the failure was raised against the Apps
        /// subcategory). Null until the first registry-derived ESP failure fires.
        /// </summary>
        public Termination.EspTerminalFailureSnapshot? LastEspTerminalFailure => _tracker.LastEspTerminalFailure;

        /// <summary>
        /// Liveness plan PR3 — appIds already reported via <c>app_install_starved</c> on the
        /// live path. Read by <c>CollectorSurfaces</c> for the termination handler's dedupe.
        /// </summary>
        public System.Collections.Generic.IReadOnlyCollection<string> StarvedAppsReported => _tracker.StarvedAppsReported;

        /// <summary>L6 — atomic claim for the termination sweep (see EspAndHelloTracker.TryClaimStarvedAppReport).</summary>
        public bool TryClaimStarvedAppReport(string appId) => _tracker.TryClaimStarvedAppReport(appId);

        public EspAndHelloHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int helloWaitTimeoutSeconds,
            bool modernDeploymentWatcherEnabled,
            int modernDeploymentLogLevelMax,
            bool modernDeploymentBackfillEnabled,
            int modernDeploymentBackfillLookbackMinutes,
            int[]? modernDeploymentHarmlessEventIds,
            string stateDirectory,
            bool isDevicePreparation = false,
            Func<bool>? userEspAppsSettledProbe = null,
            Func<System.Collections.Generic.IReadOnlyList<Monitoring.Enrollment.Ime.AppPackageState>>? starvedUserEspAppsProbe = null,
            Func<System.Collections.Generic.IReadOnlyList<Monitoring.Enrollment.Ime.AppPackageState>>? packageStatesProbe = null,
            int espExitBackfillLookbackMinutes = ShellCoreTracker.BackfillLookbackMinutes)
        {
            _espExitBackfillLookbackMinutes = espExitBackfillLookbackMinutes;
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            var post = new InformationalEventPost(ingress, clock);
            _tracker = new EspAndHelloTracker(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                helloWaitTimeoutSeconds: helloWaitTimeoutSeconds,
                modernDeploymentWatcherEnabled: modernDeploymentWatcherEnabled,
                modernDeploymentLogLevelMax: modernDeploymentLogLevelMax,
                modernDeploymentBackfillEnabled: modernDeploymentBackfillEnabled,
                modernDeploymentBackfillLookbackMinutes: modernDeploymentBackfillLookbackMinutes,
                stateDirectory: stateDirectory,
                modernDeploymentHarmlessEventIds: modernDeploymentHarmlessEventIds,
                isDevicePreparation: isDevicePreparation,
                userEspAppsSettledProbe: userEspAppsSettledProbe,
                starvedUserEspAppsProbe: starvedUserEspAppsProbe,
                packageStatesProbe: packageStatesProbe);

            _tracker.WhiteGloveCompleted += OnTrackerWhiteGloveCompleted;
            _tracker.DeviceSetupProvisioningComplete += OnTrackerDeviceSetupProvisioningComplete;
            _adapter = new EspAndHelloTrackerAdapter(_tracker, ingress, clock);
        }

        /// <summary>
        /// Starts the tracker and then replays recent Shell-Core ESP-exit / Hello-wizard records.
        /// <para>
        /// The backfill has existed since session 772fe502 but was never wired to a caller, so
        /// the Shell-Core watcher only ever saw events written AFTER <see cref="Start"/>. Any
        /// agent restart therefore lost an ESP exit (62407) or Hello-wizard start (62404) that
        /// happened while the agent was down — and after a mid-ESP reboot that is exactly the
        /// window the completion signal lands in (sits-d Cloud PCs, 2026-08-19: five sessions
        /// hung in AccountSetup until the server-side timeout while the devices were fine).
        /// </para>
        /// <para>
        /// Ordering is deliberate: Start first, then backfill. A record written between the two
        /// is observed twice, and that is by design tolerable — the tracker dedups the REPLAY
        /// (single-shot per rail) but never the live stream, because Shell-Core emits 62407 at
        /// every ESP phase transition anyway. Duplicates are absorbed downstream: the reducer
        /// (<c>ShouldTransitionToAwaitingHello</c>) picks the genuine post-AccountSetup exit, and
        /// the Hello rail has the HelloTracker once-guard plus the adapter's dedup flag. The
        /// reverse order could drop a record entirely, which costs the session its completion —
        /// a duplicate costs nothing.
        /// </para>
        /// </summary>
        public void Start()
        {
            _tracker.Start();
            _tracker.BackfillRecentEspExitEvents(_espExitBackfillLookbackMinutes);
        }

        public void Stop() => _tracker.Stop();

        /// <summary>
        /// Re-checks the user-apps-settled AccountSetup synthesis — see
        /// <see cref="EspAndHelloTracker.ReevaluateUserAppsSettledSynthesis"/>. Driven by the IME
        /// tracker's app-state-change callback (wired in <c>DefaultComponentFactory</c>).
        /// </summary>
        public void ReevaluateUserAppsSettledSynthesis() => _tracker.ReevaluateUserAppsSettledSynthesis();

        private void OnTrackerWhiteGloveCompleted(object sender, EventArgs e)
            => WhiteGloveCompleted?.Invoke(this, e);

        private void OnTrackerDeviceSetupProvisioningComplete(object sender, EventArgs e)
            => DeviceSetupProvisioningComplete?.Invoke(this, e);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _tracker.WhiteGloveCompleted -= OnTrackerWhiteGloveCompleted; } catch { }
            try { _tracker.DeviceSetupProvisioningComplete -= OnTrackerDeviceSetupProvisioningComplete; } catch { }
            try { _adapter.Dispose(); } catch { }
            try { _tracker.Dispose(); } catch { }
        }
    }
}
