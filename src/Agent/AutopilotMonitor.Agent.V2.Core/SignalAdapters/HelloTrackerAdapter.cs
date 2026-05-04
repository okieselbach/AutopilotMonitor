#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="HelloTracker"/> → <see cref="DecisionSignalKind.HelloResolved"/>.
    /// Plan §2.1a / §2.2.
    /// <para>
    /// Fires genau einmal, wenn der Tracker Hello-Abschluss feststellt. Payload enthält den
    /// <c>HelloOutcome</c>-String (<c>completed | skipped | timeout | not_configured | wizard_not_started</c>),
    /// der vom Reducer für die <c>SignalFact&lt;string&gt; HelloOutcome</c>-Fakt-Update verwendet wird.
    /// </para>
    /// </summary>
    internal sealed class HelloTrackerAdapter : IDisposable
    {
        private readonly HelloTracker _tracker;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private bool _fired;

        public HelloTrackerAdapter(
            HelloTracker tracker,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            _tracker.HelloCompleted += OnHelloCompleted;
        }

        public void Dispose()
        {
            _tracker.HelloCompleted -= OnHelloCompleted;
        }

        private void OnHelloCompleted(object sender, EventArgs e) => EmitInternal(_tracker.HelloOutcome);

        internal void TriggerFromTest(string helloOutcome) => EmitInternal(helloOutcome);

        private void EmitInternal(string? helloOutcome)
        {
            if (_fired) return;
            _fired = true;

            var outcome = string.IsNullOrEmpty(helloOutcome) ? "unknown" : helloOutcome!;

            _ingress.Post(
                kind: DecisionSignalKind.HelloResolved,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "HelloTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "hello-tracker-v1",
                    summary: $"Hello resolved (outcome={outcome})",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [SignalPayloadKeys.HelloOutcome] = outcome,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.HelloOutcome] = outcome,
                });
        }
    }
}
