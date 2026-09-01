using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class EnrollmentOrchestratorWhiteGlovePatternTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class CapturingFactory : IComponentFactory
        {
            public IReadOnlyCollection<string>? CapturedPatternIds { get; private set; }

            public CollectorSurfaces CreateCollectorHosts(
                string sessionId,
                string tenantId,
                AgentLogger logger,
                IReadOnlyCollection<string> whiteGloveSealingPatternIds,
                ISignalIngressSink ingress,
                IClock clock,
                AutopilotMonitor.Agent.V2.Core.Transport.Telemetry.ITelemetrySpool? telemetrySpool,
                TimelineEventStream? timelineEvents = null,
                Func<AutopilotMonitor.DecisionCore.State.DecisionState?>? decisionStateProbe = null)
            {
                CapturedPatternIds = whiteGloveSealingPatternIds;
                return new CollectorSurfaces(Array.Empty<ICollectorHost>());
            }
        }

        [Fact]
        public void Null_pattern_ids_reach_factory_as_empty_collection()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var factory = new CapturingFactory();
            var sut = rig.Build(componentFactory: factory, whiteGloveSealingPatternIds: null);

            sut.Start();

            Assert.NotNull(factory.CapturedPatternIds);
            Assert.Empty(factory.CapturedPatternIds!);

            sut.Stop();
        }

        [Fact]
        public void Configured_pattern_ids_reach_factory_verbatim()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var factory = new CapturingFactory();
            var ids = new[] { "WG_SEAL_1", "WG_SEAL_2", "WG_SEAL_3" };
            var sut = rig.Build(componentFactory: factory, whiteGloveSealingPatternIds: ids);

            sut.Start();

            Assert.NotNull(factory.CapturedPatternIds);
            Assert.Equal(ids, factory.CapturedPatternIds);

            sut.Stop();
        }

        [Fact]
        public void Empty_pattern_ids_collection_forwards_as_empty_not_null()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var factory = new CapturingFactory();
            var sut = rig.Build(componentFactory: factory, whiteGloveSealingPatternIds: Array.Empty<string>());

            sut.Start();

            Assert.NotNull(factory.CapturedPatternIds);
            Assert.Empty(factory.CapturedPatternIds!);

            sut.Stop();
        }
    }
}
