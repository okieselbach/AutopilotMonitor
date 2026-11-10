#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

#pragma warning disable xUnit1031 // the tests wait on tasks on purpose: a call that never returns is the failure they look for

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Stopping the collector hosts is bounded per host: termination reaches the diagnostics
    /// upload and the cleanup whatever a collector does. A host that hangs is left behind, named
    /// in one <c>agent_trace</c>, and never called again; every other host is stopped and disposed
    /// as before.
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EnrollmentOrchestratorCollectorStopTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private sealed class Host : ICollectorHost
        {
            private readonly List<string> _order;
            private readonly ManualResetEventSlim? _hangUntil;
            private readonly Exception? _throwOnStop;
            private int _stopCalls;
            private int _disposeCalls;

            public Host(string name, List<string> order, ManualResetEventSlim? hangUntil = null, Exception? throwOnStop = null)
            {
                Name = name;
                _order = order;
                _hangUntil = hangUntil;
                _throwOnStop = throwOnStop;
            }

            public string Name { get; }
            public int StopCalls => Volatile.Read(ref _stopCalls);
            public int DisposeCalls => Volatile.Read(ref _disposeCalls);

            public void Start() { }

            public void Stop()
            {
                Interlocked.Increment(ref _stopCalls);
                lock (_order) _order.Add(Name);
                if (_throwOnStop != null) throw _throwOnStop;
                _hangUntil?.Wait();
            }

            public void Dispose() => Interlocked.Increment(ref _disposeCalls);
        }

        private sealed class Factory : IComponentFactory
        {
            private readonly IReadOnlyList<ICollectorHost> _hosts;
            public Factory(IReadOnlyList<ICollectorHost> hosts) { _hosts = hosts; }

            public CollectorSurfaces CreateCollectorHosts(
                string sessionId,
                string tenantId,
                AgentLogger logger,
                IReadOnlyCollection<string> whiteGloveSealingPatternIds,
                ISignalIngressSink ingress,
                IClock clock,
                ITelemetrySpool? telemetrySpool,
                TimelineEventStream? timelineEvents = null,
                Func<DecisionState?>? decisionStateProbe = null) => new CollectorSurfaces(_hosts);
        }

        private static JObject? StopTimeoutEvent(EnrollmentOrchestrator sut) =>
            sut.SpoolForTests!.Peek(1000)
                .Where(i => i.Kind == TelemetryItemKind.Event && i.PayloadJson.Contains("collector_host_stop_timeout"))
                .Select(i => JObject.Parse(i.PayloadJson))
                .FirstOrDefault();

        [Fact]
        public void A_host_that_hangs_in_Stop_is_left_behind_and_the_others_are_still_stopped()
        {
            using var rig = new EnrollmentOrchestratorRig();
            using var release = new ManualResetEventSlim(false);
            var order = new List<string>();
            var before = new Host("Before", order);
            var hanging = new Host("Hanging", order, hangUntil: release);
            var after = new Host("After", order);
            var sut = rig.Build(
                componentFactory: new Factory(new ICollectorHost[] { before, hanging, after }),
                collectorHostStopTimeout: Bound);
            sut.Start();

            try
            {
                var stop = Task.Run(() => sut.StopCollectorHosts());

                Assert.True(stop.Wait(Wait), "StopCollectorHosts never returned although one host hangs");
                Assert.Equal(new[] { "Before", "Hanging", "After" }, order.ToArray());

                JObject? evt = null;
                Assert.True(SpinWait.SpinUntil(() => (evt = StopTimeoutEvent(sut)) != null, Wait),
                    "the hanging host was not reported");
                Assert.Equal("agent_trace", (string?)evt!["EventType"]);
                var data = (JObject)evt["Data"]!;
                Assert.Equal("collector_host_stop_timeout", (string?)data["decision"]);
                Assert.Equal("Hanging", (string?)data["host"]);
                Assert.Equal(1, (int)data["timeoutSeconds"]!);

                // Stop() afterwards: the stuck host is not touched again, the others are disposed.
                var fullStop = Task.Run(() => sut.Stop());
                Assert.True(fullStop.Wait(Wait), "Stop never returned");
                Assert.Equal(1, before.DisposeCalls);
                Assert.Equal(0, hanging.DisposeCalls);
                Assert.Equal(1, after.DisposeCalls);
                Assert.Equal(1, hanging.StopCalls);
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public void Healthy_hosts_are_stopped_and_disposed_in_order_without_a_report()
        {
            using var rig = new EnrollmentOrchestratorRig();
            var order = new List<string>();
            var first = new Host("First", order);
            var throwing = new Host("Throwing", order, throwOnStop: new InvalidOperationException("boom"));
            var last = new Host("Last", order);
            var hosts = new[] { first, throwing, last };
            var sut = rig.Build(componentFactory: new Factory(hosts)); // default bound: nothing here comes near it
            sut.Start();

            sut.StopCollectorHosts();
            sut.StopCollectorHosts(); // idempotent

            Assert.Equal(new[] { "First", "Throwing", "Last" }, order.ToArray());
            Assert.All(hosts, h => Assert.Equal(1, h.StopCalls));

            Thread.Sleep(200);
            Assert.Null(StopTimeoutEvent(sut));

            sut.Stop();
            Assert.All(hosts, h => Assert.Equal(1, h.DisposeCalls));
        }
    }
}
