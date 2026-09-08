using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// <see cref="GatherRule.EnrichErrorCodes"/> is an opt-in the backend honours at response time:
    /// the agent stamps <c>enrichErrorCodes: true</c> into the event data of a rule that set it and
    /// nothing at all otherwise (the default payload stays as it was).
    /// </summary>
    [Collection("SerialThreading")] // startup rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorEnrichErrorCodesTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly object _eventsGate = new object();
        private readonly GatherRuleExecutor _executor;

        private const string AbsentPath =
            "HKLM\\SOFTWARE\\AutopilotMonitorTests\\DefinitelyAbsent_7e3a1f4b-0000-0000-0000-000000000000";

        public GatherRuleExecutorEnrichErrorCodesTests()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _executor = new GatherRuleExecutor(
                "sess", "tenant",
                evt => { lock (_eventsGate) _events.Add(evt); },
                logger)
            {
                UnrestrictedMode = true
            };
        }

        public void Dispose()
        {
            _executor.Dispose();
            _tmp.Dispose();
        }

        private EnrollmentEvent SingleEvent()
        {
            lock (_eventsGate) return Assert.Single(_events);
        }

        private bool WaitForEventCount(int count, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_eventsGate) if (_events.Count >= count) return true;
                Thread.Sleep(25);
            }
            lock (_eventsGate) return _events.Count >= count;
        }

        private static GatherRule StartupRule(bool enrichErrorCodes) => new GatherRule
        {
            RuleId = "GATHER-EEC-001",
            Title = "enrich test",
            CollectorType = "registry",
            Target = AbsentPath,
            Trigger = "startup",
            OutputEventType = "gather_test",
            Enabled = true,
            EnrichErrorCodes = enrichErrorCodes,
        };

        [Fact]
        public void StampRuleMarkers_AddsMarkerOnlyWhenOptedIn()
        {
            var optedIn = new Dictionary<string, object> { ["exitCode"] = "1603" };
            GatherRuleExecutor.StampRuleMarkers(StartupRule(enrichErrorCodes: true), optedIn);
            Assert.Equal(true, optedIn[Constants.GatherRuleDataKeys.EnrichErrorCodes]);

            var defaultRule = new Dictionary<string, object> { ["exitCode"] = "1603" };
            GatherRuleExecutor.StampRuleMarkers(StartupRule(enrichErrorCodes: false), defaultRule);
            Assert.False(defaultRule.ContainsKey(Constants.GatherRuleDataKeys.EnrichErrorCodes));
            Assert.Single(defaultRule);
        }

        [Fact]
        public void ExecuteRule_StampsMarker_WhenRuleOptsIn()
        {
            _executor.UpdateRules(new List<GatherRule> { StartupRule(enrichErrorCodes: true) });

            Assert.True(_executor.WaitForStartupRules(30));
            Assert.True(WaitForEventCount(1));
            var evt = SingleEvent();
            Assert.Equal(GatherRuleExecutor.SourceName, evt.Source);
            Assert.Equal(true, evt.Data[Constants.GatherRuleDataKeys.EnrichErrorCodes]);
        }

        [Fact]
        public void ExecuteRule_LeavesPayloadUnchanged_ByDefault()
        {
            _executor.UpdateRules(new List<GatherRule> { StartupRule(enrichErrorCodes: false) });

            Assert.True(_executor.WaitForStartupRules(30));
            Assert.True(WaitForEventCount(1));
            Assert.False(SingleEvent().Data.ContainsKey(Constants.GatherRuleDataKeys.EnrichErrorCodes));
        }
    }
}
