using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// Debug-trace coverage of <see cref="GatherRuleExecutor"/> (EnableGatherRuleDebugLog):
    /// every "silently produced nothing" outcome must leave an explanatory line in the trace
    /// file — registration summary, missing interval, scope skips, on_change suppression,
    /// empty collector results.
    /// </summary>
    [Collection("SerialThreading")] // startup rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorDebugLogTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly GatherRuleExecutor _executor;
        private readonly string _debugLogPath;

        private const string AbsentPath =
            "HKLM\\SOFTWARE\\AutopilotMonitorTests\\DefinitelyAbsent_7e3a1f4b-0000-0000-0000-000000000000";

        public GatherRuleExecutorDebugLogTests()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _debugLogPath = Path.Combine(_tmp.Path, "gather_rules_debug.log");
            _executor = new GatherRuleExecutor(
                "sess", "tenant", _ => { }, logger,
                debugLogPath: _debugLogPath)
            {
                UnrestrictedMode = true
            };
        }

        public void Dispose()
        {
            _executor.Dispose();
            _tmp.Dispose();
        }

        private string TraceContent() => File.Exists(_debugLogPath) ? File.ReadAllText(_debugLogPath) : string.Empty;

        private static GatherRule Rule(string id, Action<GatherRule>? mutate = null)
        {
            var rule = new GatherRule
            {
                RuleId = id,
                Title = id,
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = "startup",
                OutputEventType = "gather_test",
                Enabled = true,
            };
            mutate?.Invoke(rule);
            return rule;
        }

        [Fact]
        public void UpdateRules_writes_registration_summary_per_rule()
        {
            _executor.UpdateRules(new List<GatherRule>
            {
                Rule("GATHER-REG-001", r => { r.Trigger = "interval"; r.IntervalSeconds = 3600; r.EmitMode = "on_change"; }),
                Rule("GATHER-REG-002", r => r.Enabled = false),
            });
            _executor.WaitForStartupRules(30);

            var trace = TraceContent();
            Assert.Contains("GATHER-REG-001 | config | registered: trigger=interval, collector=registry", trace);
            Assert.Contains("emitMode=on_change", trace);
            Assert.Contains("interval=3600s", trace);
            Assert.Contains("GATHER-REG-002 | config | rule disabled — will never run", trace);
            Assert.Contains("interval timer scheduled every 3600s (first run after one full interval", trace);
        }

        [Fact]
        public void UpdateRules_flags_interval_rule_without_intervalSeconds()
        {
            _executor.UpdateRules(new List<GatherRule>
            {
                Rule("GATHER-NOINT-001", r => { r.Trigger = "interval"; r.IntervalSeconds = null; }),
            });

            Assert.Contains("GATHER-NOINT-001 | error | trigger=interval but intervalSeconds is missing — rule will NEVER run",
                TraceContent());
        }

        [Fact]
        public void ScopedRule_skip_traces_unknown_phase_reason()
        {
            var rule = Rule("GATHER-SCOPE-001", r => { r.Trigger = "interval"; r.IntervalSeconds = 3600; r.ActivePhases = new List<string> { "DeviceSetup" }; });
            _executor.UpdateRules(new List<GatherRule> { rule });

            // Synchronous internal check mirrors the timer-tick gate; the reason must name Unknown.
            string reason;
            Assert.False(_executor.IsRuleInScope(rule, out reason));
            Assert.Contains("currentPhase=Unknown", reason);
        }

        [Fact]
        public void ScopedStartupRule_deferral_is_traced()
        {
            _executor.UpdateRules(new List<GatherRule>
            {
                Rule("GATHER-DEFER-001", r => r.ActivePhases = new List<string> { "DeviceSetup" }),
            });

            Assert.Contains("GATHER-DEFER-001 | scope | startup rule deferred — currentPhase=Unknown", TraceContent());
        }

        [Fact]
        public void OnChange_suppression_and_reemission_are_traced()
        {
            var rule = Rule("GATHER-OC-001", r => { r.Trigger = "interval"; r.IntervalSeconds = 3600; r.EmitMode = "on_change"; });
            _executor.UpdateRules(new List<GatherRule> { rule });

            string hashPrefix;
            int streak;
            var first = new Dictionary<string, object> { ["exists"] = false };
            Assert.True(_executor.ShouldEmitOnChange(rule, first, out hashPrefix, out streak));
            Assert.Equal(0, streak);
            Assert.False(string.IsNullOrEmpty(hashPrefix));

            Assert.False(_executor.ShouldEmitOnChange(rule, new Dictionary<string, object> { ["exists"] = false }, out hashPrefix, out streak));
            Assert.Equal(1, streak);
            Assert.False(_executor.ShouldEmitOnChange(rule, new Dictionary<string, object> { ["exists"] = false }, out hashPrefix, out streak));
            Assert.Equal(2, streak);

            var changed = new Dictionary<string, object> { ["exists"] = true };
            Assert.True(_executor.ShouldEmitOnChange(rule, changed, out hashPrefix, out streak));
            Assert.Equal(2, streak); // the streak that just ended
            Assert.Equal(2, changed["suppressedPolls"]);
        }

        [Fact]
        public void EmptyCollectorResult_is_traced_via_startup_execution()
        {
            _executor.UpdateRules(new List<GatherRule>
            {
                Rule("GATHER-EMPTY-001", r => r.Parameters = new Dictionary<string, string> { ["emitOnlyIfExists"] = "true" }),
            });
            Assert.True(_executor.WaitForStartupRules(30));

            var trace = TraceContent();
            Assert.Contains("GATHER-EMPTY-001 | exec | executing: collector=registry", trace);
            Assert.Contains("GATHER-EMPTY-001 | collector | registry key/value not found", trace);
            Assert.Contains("GATHER-EMPTY-001 | collector | collector returned EMPTY result — nothing emitted", trace);
        }
    }
}
