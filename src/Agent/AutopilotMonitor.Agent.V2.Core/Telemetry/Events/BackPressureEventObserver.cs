#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Events
{
    /// <summary>
    /// Konkrete <see cref="IBackPressureObserver"/>-Implementierung. Plan §2.1a.
    /// <para>
    /// Mapping eines Back-Pressure-Ereignisses auf ein <c>agent_trace</c>-Event
    /// mit <c>EventType="ingress_backpressure"</c> (<see cref="EventSeverity.Warning"/>). Delegiert an
    /// <see cref="TelemetryEventEmitter"/>. Die Throttling-Logik (1×/min/origin) liegt in
    /// <see cref="SignalIngress"/> — hier wird jedes reingereichte Ereignis emittiert.
    /// </para>
    /// </summary>
    internal sealed class BackPressureEventObserver : IBackPressureObserver
    {
        internal const string EventType = Constants.EventTypes.IngressBackpressure;
        internal const string SourceId = "signal_ingress";

        private readonly TelemetryEventEmitter _emitter;
        private readonly IClock _clock;

        public BackPressureEventObserver(TelemetryEventEmitter emitter, IClock clock)
        {
            _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public void OnBackPressure(string origin, int channelCapacity, int queueLength, TimeSpan blockDuration)
        {
            var evt = new EnrollmentEvent
            {
                EventType = EventType,
                Severity = EventSeverity.Warning,
                Source = SourceId,
                Phase = EnrollmentPhase.Unknown,
                Message = $"Signal ingress back-pressure on origin '{origin}' (queue {queueLength}/{channelCapacity}, blocked {blockDuration.TotalMilliseconds:F0}ms)",
                Timestamp = _clock.UtcNow,
                ImmediateUpload = false,
                Data = new Dictionary<string, object>(capacity: 4, StringComparer.Ordinal)
                {
                    ["origin"] = origin,
                    ["channelCapacity"] = channelCapacity,
                    ["queueLength"] = queueLength,
                    ["blockDurationMs"] = (long)blockDuration.TotalMilliseconds,
                },
            };

            _emitter.Emit(evt);
        }
    }
}
