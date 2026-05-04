#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="DesktopArrivalDetector"/> → <see cref="DecisionSignalKind.DesktopArrived"/>.
    /// Plan §2.1a / §2.2.
    /// <para>
    /// Fire-once by design (the detector itself guards against duplicate fires; the adapter
    /// also guards defensively in case the detector is restarted).
    /// </para>
    /// <para>
    /// <b>Dual emission</b> (first-session fix PR 2 / Fix 3): the adapter posts (a) the
    /// specific <see cref="DecisionSignalKind"/> consumed by the reducer AND (b) an
    /// <see cref="DecisionSignalKind.InformationalEvent"/> for the Events-table timeline.
    /// Without (b) the signal reached the reducer only, leaving the UI and MCP
    /// <c>query_raw_events</c> blind to desktop arrival.
    /// </para>
    /// </summary>
    internal sealed class DesktopArrivalDetectorAdapter : IDisposable
    {
        private const string SourceLabel = "DesktopArrivalDetector";

        private readonly DesktopArrivalDetector _detector;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly InformationalEventPost _post;
        private bool _fired;

        public DesktopArrivalDetectorAdapter(
            DesktopArrivalDetector detector,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _post = new InformationalEventPost(ingress, clock);

            _detector.DesktopArrived += OnDesktopArrived;
        }

        public void Dispose()
        {
            _detector.DesktopArrived -= OnDesktopArrived;
        }

        private void OnDesktopArrived(object sender, EventArgs e) => EmitInternal();

        /// <summary>Test hook — triggers the same emit-logic bypassing the event plumbing.</summary>
        internal void TriggerFromTest() => EmitInternal();

        private void EmitInternal()
        {
            if (_fired) return;
            _fired = true;

            var now = _clock.UtcNow;
            const string summary = "Desktop arrival observed (explorer.exe under real user)";

            _ingress.Post(
                kind: DecisionSignalKind.DesktopArrived,
                occurredAtUtc: now,
                sourceOrigin: SourceLabel,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "desktop-arrival-detector-v1",
                    summary: summary,
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detectionSource"] = "explorer.exe process poll",
                    }));

            // Parity with V1: also emit a `desktop_arrived` InformationalEvent so the Events
            // table, UI timeline and MCP `query_raw_events` see the transition. Flush
            // immediately — desktop arrival is a terminal completion gate (Fix 1 policy).
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detectedAt"] = now.ToString("o"),
                ["detectionSource"] = "explorer.exe process poll",
            };

            _post.Emit(
                eventType: SharedEventTypes.DesktopArrived,
                source: SourceLabel,
                message: summary,
                immediateUpload: true,
                data: data,
                occurredAtUtc: now);
        }
    }
}
