using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// The production factory hands its state directory to the gather executor. Without it every
    /// unit test of the gather state passes while the agent keeps replaying logs after each reboot —
    /// the one line in <see cref="DefaultComponentFactory"/> is the whole feature.
    /// </summary>
    [Collection("SerialThreading")] // the startup rule executes on the shared ThreadPool
    public sealed class DefaultComponentFactoryGatherStateTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly TempDirectory _stateDir = new TempDirectory();

        // %TEMP% lives under C:\Users (hard-blocked) — the log sits beside the test assembly.
        private readonly string _logDir = Path.Combine(
            AppContext.BaseDirectory, "factory-gather-state-" + Guid.NewGuid().ToString("N"));

        public DefaultComponentFactoryGatherStateTests() => Directory.CreateDirectory(_logDir);

        public void Dispose()
        {
            _tmp.Dispose();
            _stateDir.Dispose();
            try { Directory.Delete(_logDir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void Gather_host_built_by_the_factory_persists_logparser_positions_in_the_state_directory()
        {
            var log = Path.Combine(_logDir, "vendor.log");
            File.WriteAllText(log, "Update one\n");

            var factory = new DefaultComponentFactory(
                agentConfig: new AgentConfiguration
                {
                    ApiBaseUrl = "https://example",
                    TenantId = "t",
                    SessionId = "s",
                    UnrestrictedMode = true,
                },
                remoteConfig: new AgentConfigResponse
                {
                    Collectors = CollectorConfiguration.CreateDefault(),
                    GatherRules = new List<GatherRule>
                    {
                        new GatherRule
                        {
                            RuleId = "vendor-log",
                            Title = "vendor log",
                            CollectorType = "logparser",
                            Target = log,
                            Parameters = new Dictionary<string, string> { ["pattern"] = "^Update", ["format"] = "text" },
                            Trigger = "startup",
                            OutputEventType = "vendor_update",
                            Enabled = true,
                        },
                    },
                },
                networkMetrics: null,
                agentVersion: "test",
                stateDirectory: _stateDir.Path,
                startupEventGate: null);

            var hosts = factory.CreateCollectorHosts(
                sessionId: "s",
                tenantId: "t",
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                whiteGloveSealingPatternIds: Array.Empty<string>(),
                ingress: new FakeSignalIngressSink(),
                clock: new VirtualClock(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc)),
                telemetrySpool: null,
                timelineEvents: null).Hosts;
            try
            {
                hosts.OfType<GatherRuleExecutorHost>().Single().Start();

                var stateFile = Path.Combine(_stateDir.Path, GatherRuleStatePersistence.FileName);
                Assert.True(SpinWait.SpinUntil(() => File.Exists(stateFile), TimeSpan.FromSeconds(10)),
                    "the gather host did not write its state into the factory's state directory");
            }
            finally
            {
                foreach (var host in hosts)
                {
                    try { host.Dispose(); } catch { /* best-effort cleanup */ }
                }
            }
        }
    }
}
