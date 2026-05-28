#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class EspAndHelloHost : ICollectorHost, IWhiteGloveCompletedSource
    {
        public string Name => "EspAndHelloTracker";

        private readonly EspAndHelloTracker _tracker;
        private readonly EspAndHelloTrackerAdapter _adapter;
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
            string stateDirectory)
        {
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
                modernDeploymentHarmlessEventIds: modernDeploymentHarmlessEventIds);

            _tracker.WhiteGloveCompleted += OnTrackerWhiteGloveCompleted;
            _adapter = new EspAndHelloTrackerAdapter(_tracker, ingress, clock);
        }

        public void Start() => _tracker.Start();
        public void Stop() => _tracker.Stop();

        private void OnTrackerWhiteGloveCompleted(object sender, EventArgs e)
            => WhiteGloveCompleted?.Invoke(this, e);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _tracker.WhiteGloveCompleted -= OnTrackerWhiteGloveCompleted; } catch { }
            try { _adapter.Dispose(); } catch { }
            try { _tracker.Dispose(); } catch { }
        }
    }
}
