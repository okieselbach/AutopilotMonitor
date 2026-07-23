using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// Shared fixture for every <c>ImeLogTrackerAdapter*Tests</c> file. Previously each of the
    /// five test files redeclared the same inner Fixture class (~30 LOC × 5 files of boilerplate);
    /// consolidating keeps the setup uniform and cuts duplication without collapsing the
    /// feature-separated test files (navigation via filename still works).
    /// <para>
    /// Ctor parameter <paramref name="clockStart"/> is optional — tests that don't care pass
    /// nothing and get <see cref="DefaultClockStart"/>; tests that drive time-sensitive behaviour
    /// (Timing) pass their own start.
    /// </para>
    /// </summary>
    internal sealed class ImeLogTrackerAdapterFixture : IDisposable
    {
        public static readonly DateTime DefaultClockStart = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        public TempDirectory Tmp { get; } = new TempDirectory();
        public AgentLogger Logger { get; }
        public ImeLogTracker Tracker { get; }
        public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
        public VirtualClock Clock { get; }

        public ImeLogTrackerAdapterFixture(DateTime? clockStart = null)
        {
            Clock = new VirtualClock(clockStart ?? DefaultClockStart);
            Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
            Tracker = new ImeLogTracker(
                logFolder: Tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: Logger);
            // Mirror ImeLogHost wiring: the historic-replay guard judges staleness against the
            // (virtual) agent clock so tests stay deterministic.
            Tracker.UtcNowProvider = () => Clock.UtcNow;
        }

        public IReadOnlyList<FakeSignalIngressSink.PostedSignal> DecisionSignals(DecisionSignalKind kind) =>
            Ingress.Posted.Where(p => p.Kind == kind).ToList();

        public IReadOnlyList<FakeSignalIngressSink.PostedSignal> InfoEvents(string eventType) =>
            Ingress.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent
                            && p.Payload != null
                            && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                            && et == eventType)
                .ToList();

        public FakeSignalIngressSink.PostedSignal InfoEvent(string eventType) =>
            InfoEvents(eventType).Single();

        public IReadOnlyList<FakeSignalIngressSink.PostedSignal> AllInfoEvents() =>
            Ingress.Posted.Where(p => p.Kind == DecisionSignalKind.InformationalEvent).ToList();

        public IReadOnlyList<FakeSignalIngressSink.PostedSignal> NonInfoSignals() =>
            Ingress.Posted.Where(p => p.Kind != DecisionSignalKind.InformationalEvent).ToList();

        public void Dispose()
        {
            Tracker.Dispose();
            Tmp.Dispose();
        }
    }
}
