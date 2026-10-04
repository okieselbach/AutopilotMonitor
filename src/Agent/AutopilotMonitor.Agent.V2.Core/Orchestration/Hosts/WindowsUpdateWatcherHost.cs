#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Hosts the OS-update watchers (D-310): the Windows Update client on the System log
    /// (install events) and on its Operational channel (scan/download events), plus the CBS
    /// servicing steps on the Setup log. Routes every configured WU EventID to the channel the
    /// provider manifest assigns it, runs the blind-spot census across all channels, reports the
    /// counted (non-OS) activity once on stop, and — unless <c>OobeUpdatePhaseMode</c> is Off —
    /// feeds Windows/.NET update steps into the decision engine as
    /// <see cref="DecisionSignalKind.OsUpdateActivity"/>.
    /// </summary>
    internal sealed class WindowsUpdateWatcherHost : ICollectorHost
    {
        public string Name => "WindowsUpdateTracker";

        private readonly WindowsUpdateTracker? _systemTracker;
        private readonly WindowsUpdateTracker? _operationalTracker;
        private readonly ServicingTracker? _servicingTracker;
        private readonly UpdateActivityTally _tally = new UpdateActivityTally();
        private readonly InformationalEventPost _post;
        private readonly ISignalIngressSink _ingress;
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private int _summaryEmitted;
        private int _disposed;

        public WindowsUpdateWatcherHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int[]? targetedEventIds,
            int backfillLookbackMinutes,
            string stateDirectory,
            bool channelCensusEnabled = true,
            Func<bool>? osBuildChangedProvider = null,
            int[]? operationalEventIds = null,
            bool servicingWatcherEnabled = true,
            bool osUpdateSignalsEnabled = false)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = new InformationalEventPost(ingress, clock, logger);

            var (systemIds, operationalIds) = RouteEventIds(targetedEventIds, operationalEventIds);

            Action<string, string, DateTime>? wuActivity = null;
            Action<string, string, DateTime>? servicingActivity = null;
            if (osUpdateSignalsEnabled)
            {
                wuActivity = (step, update, at) => PostOsUpdateActivity(step, "wu", update, at);
                servicingActivity = (step, update, at) => PostOsUpdateActivity(step, "servicing", update, at);
            }

            if (systemIds.Length > 0)
            {
                _systemTracker = new WindowsUpdateTracker(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    post: _post,
                    logger: logger,
                    targetedEventIds: systemIds,
                    backfillEnabled: backfillLookbackMinutes > 0,
                    backfillLookbackMinutes: backfillLookbackMinutes,
                    stateDirectory: stateDirectory,
                    channelCensusEnabled: false,
                    channel: WindowsUpdateTracker.SystemChannel,
                    tally: _tally,
                    onOsUpdateActivity: wuActivity);
            }

            if (operationalIds.Length > 0)
            {
                _operationalTracker = new WindowsUpdateTracker(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    post: _post,
                    logger: logger,
                    targetedEventIds: operationalIds,
                    backfillEnabled: backfillLookbackMinutes > 0,
                    backfillLookbackMinutes: backfillLookbackMinutes,
                    stateDirectory: stateDirectory,
                    channelCensusEnabled: channelCensusEnabled,
                    osBuildChangedProvider: osBuildChangedProvider,
                    channel: WindowsUpdateTracker.Channel,
                    tally: _tally,
                    onOsUpdateActivity: wuActivity);
            }

            if (servicingWatcherEnabled)
            {
                _servicingTracker = new ServicingTracker(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    post: _post,
                    logger: logger,
                    backfillEnabled: backfillLookbackMinutes > 0,
                    backfillLookbackMinutes: backfillLookbackMinutes,
                    stateDirectory: stateDirectory,
                    tally: _tally,
                    onOsUpdateActivity: servicingActivity);
            }
        }

        // Test seams — expose the trackers so unit tests can drive their ProcessEvent seams.
        internal WindowsUpdateTracker? SystemTrackerForTest => _systemTracker;
        internal WindowsUpdateTracker? OperationalTrackerForTest => _operationalTracker;
        internal ServicingTracker? ServicingTrackerForTest => _servicingTracker;

        /// <summary>
        /// Splits the configured WU EventIDs by the channel the provider manifest writes them to.
        /// An ID configured in either list ends up on its real channel, so a misplaced ID still
        /// works instead of watching a channel that never carries it.
        /// </summary>
        internal static (int[] systemIds, int[] operationalIds) RouteEventIds(int[]? targetedEventIds, int[]? operationalEventIds)
        {
            var all = (targetedEventIds ?? Array.Empty<int>())
                .Concat(operationalEventIds ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            return (
                all.Where(WindowsUpdateTracker.IsSystemChannelEventId).ToArray(),
                all.Where(id => !WindowsUpdateTracker.IsSystemChannelEventId(id)).ToArray());
        }

        public void Start()
        {
            _systemTracker?.Start();
            _operationalTracker?.Start();
            _servicingTracker?.Start();

            // The census asks "did ANY channel see the update?" — only answerable after every
            // backfill ran.
            _operationalTracker?.EmitChannelCensusIfBlind(
                otherChannelsEmitted: _systemTracker?.EmittedThisRun ?? 0,
                otherTargetedEventIds: _systemTracker?.TargetedEventIds);

            _logger.Info("WindowsUpdateWatcherHost: started.");
        }

        public void Stop()
        {
            try { _systemTracker?.Stop(); }
            catch (Exception ex) { _logger.Warning($"WindowsUpdateWatcherHost: system watcher stop failed: {ex.Message}"); }
            try { _operationalTracker?.Stop(); }
            catch (Exception ex) { _logger.Warning($"WindowsUpdateWatcherHost: operational watcher stop failed: {ex.Message}"); }
            try { _servicingTracker?.Stop(); }
            catch (Exception ex) { _logger.Warning($"WindowsUpdateWatcherHost: servicing watcher stop failed: {ex.Message}"); }

            EmitActivitySummaryOnce();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
        }

        /// <summary>
        /// One summary of everything that was counted instead of emitted. Stop runs twice on the
        /// termination path (explicit stop, then dispose), hence the once-guard.
        /// </summary>
        internal void EmitActivitySummaryOnce()
        {
            if (_tally.IsEmpty) return;
            if (Interlocked.Exchange(ref _summaryEmitted, 1) == 1) return;

            try
            {
                var counts = _tally.Snapshot();
                var data = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var kv in counts) data[kv.Key] = kv.Value;
                var total = counts.Sum(kv => kv.Value);

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = SharedConstants.EventTypes.WindowsUpdateActivitySummary,
                    Severity = EventSeverity.Debug,
                    Source = SharedConstants.EventSources.WindowsUpdateWatcher,
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Update activity counted instead of listed: {total} event(s) " +
                        "(Store, Defender and other non-OS updates, repeated scan results, servicing of non-OS packages)",
                    Data = data,
                    ImmediateUpload = false,
                });
            }
            catch (Exception ex)
            {
                _logger.Warning($"WindowsUpdateWatcherHost: activity summary failed: {ex.Message}");
            }
        }

        private void PostOsUpdateActivity(string step, string source, string update, DateTime occurredAtUtc)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OsUpdatePayloadKeys.Step] = step,
                [OsUpdatePayloadKeys.Source] = source,
            };
            if (!string.IsNullOrEmpty(update)) payload[OsUpdatePayloadKeys.Update] = update;

            _ingress.Post(
                kind: DecisionSignalKind.OsUpdateActivity,
                occurredAtUtc: DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
                sourceOrigin: source == "servicing" ? SharedConstants.EventSources.ServicingWatcher : SharedConstants.EventSources.WindowsUpdateWatcher,
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: $"os-update-activity:{source}",
                    summary: string.IsNullOrEmpty(update) ? step : $"{step}: {update}"),
                payload: payload);
        }
    }
}
