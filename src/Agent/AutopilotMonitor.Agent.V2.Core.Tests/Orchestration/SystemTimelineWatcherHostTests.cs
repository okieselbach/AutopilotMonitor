#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// SystemTimelineWatcherHost — thin ICollectorHost wrapper over SystemTimelineTracker.
    /// Real event-log arming stays untested by convention (ConsoleBypassWatcher precedent);
    /// these tests pin the lifecycle contract only.
    /// </summary>
    public sealed class SystemTimelineWatcherHostTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();

        public void Dispose() => _tmp.Dispose();

        private SystemTimelineWatcherHost CreateHost() => new SystemTimelineWatcherHost(
            sessionId: "sess-stl",
            tenantId: "tenant-stl",
            logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
            ingress: new FakeSignalIngressSink(),
            clock: new VirtualClock(new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc)),
            backfillLookbackMinutes: 0,
            stateDirectory: _tmp.Path);

        [Fact]
        public void Stop_BeforeStart_DoesNotThrow()
        {
            using (var host = CreateHost())
            {
                host.Stop();
            }
        }

        [Fact]
        public void Dispose_Twice_DoesNotThrow()
        {
            var host = CreateHost();
            host.Dispose();
            host.Dispose();
        }

        [Fact]
        public void Name_IsStable()
        {
            using (var host = CreateHost())
            {
                Assert.Equal("SystemTimelineTracker", host.Name);
            }
        }
    }
}
