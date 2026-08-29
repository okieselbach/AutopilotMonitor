using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// on_event has no once-per-(rule, phase) dedup like the phase triggers. The per-rule
    /// execution cap (<see cref="GatherRuleExecutor.MaxOnEventExecutionsPerRule"/>) bounds any
    /// trigger cycle that survives the host's source guard (e.g. two rules chained through an
    /// intermediate non-gather event) so a mis-authored rule can never spin for a whole session.
    /// Executions are observed through the emitted events; the rule uses a registry path that
    /// exists on every Windows box so each execution emits exactly one event.
    /// </summary>
    [Collection("SerialThreading")] // rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorOnEventCapTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly object _gate = new object();
        private readonly GatherRuleExecutor _executor;

        public GatherRuleExecutorOnEventCapTests()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _executor = new GatherRuleExecutor("sess", "tenant",
                evt => { lock (_gate) _events.Add(evt); }, logger)
            {
                UnrestrictedMode = true
            };
        }

        public void Dispose()
        {
            _executor.Dispose();
            _tmp.Dispose();
        }

        private int EventCount { get { lock (_gate) return _events.Count; } }

        private bool WaitForEventCount(int count, int timeoutMs = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (EventCount >= count) return true;
                Thread.Sleep(25);
            }
            return EventCount >= count;
        }

        private static GatherRule OnEventRule(string id, string triggerEventType) => new GatherRule
        {
            RuleId = id,
            Title = id,
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
            Trigger = "on_event",
            TriggerEventType = triggerEventType,
            OutputEventType = "gather_cap_test",
            Enabled = true,
        };

        [Fact]
        public void OnEvent_ExecutesAtMostMaxOnEventExecutionsPerRule_PerSession()
        {
            _executor.UpdateRules(new List<GatherRule> { OnEventRule("GATHER-CAP-001", "ext_event") });

            var cap = GatherRuleExecutor.MaxOnEventExecutionsPerRule;
            for (var i = 0; i < cap + 25; i++)
                _executor.OnEvent("ext_event");

            Assert.True(WaitForEventCount(cap), $"expected {cap} executions, got {EventCount}");
            Thread.Sleep(500);
            Assert.Equal(cap, EventCount);

            // Further triggers stay ignored for the rest of the session.
            _executor.OnEvent("ext_event");
            Thread.Sleep(300);
            Assert.Equal(cap, EventCount);
        }

        [Fact]
        public void OnEventCap_IsPerRule_NotGlobal()
        {
            _executor.UpdateRules(new List<GatherRule>
            {
                OnEventRule("GATHER-CAP-002", "ext_event"),
                OnEventRule("GATHER-CAP-003", "ext_event"),
            });

            var cap = GatherRuleExecutor.MaxOnEventExecutionsPerRule;
            for (var i = 0; i < cap + 5; i++)
                _executor.OnEvent("ext_event");

            Assert.True(WaitForEventCount(2 * cap), $"expected {2 * cap} executions, got {EventCount}");
            Thread.Sleep(500);
            Assert.Equal(2 * cap, EventCount);
        }
    }
}
