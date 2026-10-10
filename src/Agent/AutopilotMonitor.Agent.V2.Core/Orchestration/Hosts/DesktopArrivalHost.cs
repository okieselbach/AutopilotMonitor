#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class DesktopArrivalHost : ICollectorHost
    {
        public string Name => "DesktopArrivalDetector";

        private readonly DesktopArrivalDetector _detector;
        private readonly DesktopArrivalDetectorAdapter _adapter;
        private int _disposed;

        public DesktopArrivalHost(
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int noCandidateTimeoutMinutes = 10,
            string? sessionId = null,
            string? tenantId = null,
            Action<string>? onRealUserOwnerObserved = null)
        {
            _detector = new DesktopArrivalDetector(logger, noCandidateTimeoutMinutes);
            _adapter = new DesktopArrivalDetectorAdapter(_detector, ingress, clock);

            if (onRealUserOwnerObserved != null)
            {
                _detector.RealUserOwnerObserved += (s, owner) =>
                {
                    try { onRealUserOwnerObserved(owner); }
                    catch (Exception ex) { logger.Warning($"DesktopArrivalHost: onRealUserOwnerObserved callback threw: {ex.Message}"); }
                };
            }

            // V2 single-rail wiring (2026-05-15) — the OnTraceEvent callback is routed
            // through InformationalEventPost so the DAD lifecycle events
            // (desktop_excluded_user, desktop_real_user_detected, desktop_detector_started,
            // desktop_detector_first_poll, desktop_detector_no_candidate) appear on the
            // session timeline as standalone EventTypes. V1's CollectorCoordinator wrapped
            // them inside a generic `trace_event`; V2 surfaces them with their own type so
            // queries / dashboards / MCP can key on the specific event semantics.
            var post = new InformationalEventPost(ingress, clock, logger);
            _detector.OnTraceEvent = (eventType, message, data) =>
            {
                try
                {
                    var payload = data == null
                        ? new Dictionary<string, object>(StringComparer.Ordinal)
                        : new Dictionary<string, object>(data, StringComparer.Ordinal);

                    post.Emit(new EnrollmentEvent
                    {
                        SessionId = sessionId ?? string.Empty,
                        TenantId = tenantId ?? string.Empty,
                        EventType = eventType,
                        Severity = EventSeverity.Info,
                        Source = "DesktopArrivalDetector",
                        Phase = EnrollmentPhase.Unknown,
                        Message = message,
                        Data = payload,
                        ImmediateUpload = true,
                    });
                }
                catch (Exception ex)
                {
                    logger.Debug($"DesktopArrivalHost: trace-event emit '{eventType}' threw: {ex.Message}");
                }
            };
        }

        public void Start() => _detector.Start();
        public void Stop() => _detector.Stop();

        /// <summary>
        /// Resets desktop-arrival tracking after a placeholder→real-user transition
        /// (Hybrid User-Driven completion-gap fix, 2026-05-01). Wired by the composition
        /// root through <see cref="AadJoinHost"/>'s <c>onRealUserJoined</c> callback so the
        /// fooUser desktop the detector observed during foo-OOBE is invalidated and polling
        /// restarts to detect the AD-user desktop after the Hybrid reboot.
        /// </summary>
        public void RequestResetForRealUserSwitch() => _detector.ResetForRealUserSwitch();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _adapter.Dispose(); } catch { }
            try { _detector.Dispose(); } catch { }
        }
    }
}
