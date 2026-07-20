using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// EmitMode "on_change" on <see cref="GatherRuleExecutor"/>: poll on the trigger cadence but
    /// emit only when the collected result changes. The dedup state machine is asserted
    /// synchronously via the internal <c>ShouldEmitOnChange</c> / <c>ComputeCanonicalHash</c>
    /// (no timer races); the ExecuteRule wiring (hash-before-injection, empty-result no-op,
    /// emitOnlyIfExists composition) through the public startup path.
    /// </summary>
    [Collection("SerialThreading")] // startup rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorEmitModeTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly object _eventsGate = new object();
        private readonly GatherRuleExecutor _executor;

        private const string AbsentPath =
            "HKLM\\SOFTWARE\\AutopilotMonitorTests\\DefinitelyAbsent_7e3a1f4b-0000-0000-0000-000000000000";

        public GatherRuleExecutorEmitModeTests()
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

        private int EventCount { get { lock (_eventsGate) return _events.Count; } }

        private EnrollmentEvent SingleEvent()
        {
            lock (_eventsGate) return Assert.Single(_events);
        }

        private bool WaitForEventCount(int count, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (EventCount >= count) return true;
                Thread.Sleep(25);
            }
            return EventCount >= count;
        }

        private static GatherRule OnChangeRule(string id = "GATHER-OC-001") => new GatherRule
        {
            RuleId = id,
            Title = id,
            CollectorType = "registry",
            Target = AbsentPath,
            Trigger = "interval",
            IntervalSeconds = 3600,
            OutputEventType = "gather_test",
            Enabled = true,
            EmitMode = "on_change",
        };

        private static Dictionary<string, object> Result(params (string Key, object Value)[] pairs)
        {
            var d = new Dictionary<string, object>();
            foreach (var (key, value) in pairs) d[key] = value;
            return d;
        }

        // ── on_change state machine (synchronous) ──────────────────────────

        [Fact]
        public void FirstResult_AlwaysEmits_ThenUnchangedIsSuppressed_ThenChangeReemits()
        {
            var rule = OnChangeRule();

            Assert.True(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));   // first
            Assert.False(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));  // unchanged
            Assert.False(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));  // unchanged
            Assert.True(_executor.ShouldEmitOnChange(rule, Result(("exists", true), ("Version", "1")))); // changed
        }

        [Fact]
        public void EmitAfterSuppressionStreak_CarriesSuppressedPollCount_AndResets()
        {
            var rule = OnChangeRule();

            Assert.True(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));
            Assert.False(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));
            Assert.False(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));
            Assert.False(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));

            var changed = Result(("exists", true));
            Assert.True(_executor.ShouldEmitOnChange(rule, changed));
            Assert.Equal(3, changed["suppressedPolls"]);
            Assert.True(changed.ContainsKey("suppressedSinceUtc"));

            // Counter must reset: the next change emits without a suppression payload.
            var changedAgain = Result(("exists", false));
            Assert.True(_executor.ShouldEmitOnChange(rule, changedAgain));
            Assert.False(changedAgain.ContainsKey("suppressedPolls"));
            Assert.False(changedAgain.ContainsKey("suppressedSinceUtc"));
        }

        [Fact]
        public void SuppressionState_IsIsolatedPerRule()
        {
            var ruleA = OnChangeRule("GATHER-OC-A");
            var ruleB = OnChangeRule("GATHER-OC-B");
            var payload = Result(("exists", false));

            Assert.True(_executor.ShouldEmitOnChange(ruleA, new Dictionary<string, object>(payload)));
            // Same content, different rule: B has no hash yet — must emit.
            Assert.True(_executor.ShouldEmitOnChange(ruleB, new Dictionary<string, object>(payload)));
            Assert.False(_executor.ShouldEmitOnChange(ruleA, new Dictionary<string, object>(payload)));
            Assert.False(_executor.ShouldEmitOnChange(ruleB, new Dictionary<string, object>(payload)));
        }

        [Fact]
        public void DedupState_SurvivesUpdateRulesRefresh()
        {
            var rule = OnChangeRule();
            Assert.True(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));

            // Config refresh mid-session must not reset the last-emitted hash (that would
            // re-emit an unchanged result on every refresh).
            _executor.UpdateRules(new List<GatherRule> { rule });

            Assert.False(_executor.ShouldEmitOnChange(rule, Result(("exists", false))));
        }

        // ── Canonical hashing ──────────────────────────────────────────────

        [Fact]
        public void CanonicalHash_IsKeyOrderInsensitive()
        {
            var a = new Dictionary<string, object> { ["alpha"] = 1, ["beta"] = "x" };
            var b = new Dictionary<string, object> { ["beta"] = "x", ["alpha"] = 1 };

            Assert.Equal(
                GatherRuleExecutor.ComputeCanonicalHash(a),
                GatherRuleExecutor.ComputeCanonicalHash(b));
        }

        [Fact]
        public void CanonicalHash_DetectsValueChanges_IncludingNested()
        {
            var baseline = new Dictionary<string, object>
            {
                ["exists"] = true,
                ["entries"] = new List<object> { new Dictionary<string, object> { ["id"] = 1 } },
            };
            var changedScalar = new Dictionary<string, object>
            {
                ["exists"] = false,
                ["entries"] = new List<object> { new Dictionary<string, object> { ["id"] = 1 } },
            };
            var changedNested = new Dictionary<string, object>
            {
                ["exists"] = true,
                ["entries"] = new List<object> { new Dictionary<string, object> { ["id"] = 2 } },
            };

            var baseHash = GatherRuleExecutor.ComputeCanonicalHash(baseline);
            Assert.NotEqual(baseHash, GatherRuleExecutor.ComputeCanonicalHash(changedScalar));
            Assert.NotEqual(baseHash, GatherRuleExecutor.ComputeCanonicalHash(changedNested));
        }

        [Fact]
        public void CanonicalHash_DistinguishesTypeShapes()
        {
            // "1" (string) vs 1 (int) and null vs "<null>"-like strings must not collide.
            Assert.NotEqual(
                GatherRuleExecutor.ComputeCanonicalHash(new Dictionary<string, object> { ["v"] = "1" }),
                GatherRuleExecutor.ComputeCanonicalHash(new Dictionary<string, object> { ["v"] = 1 }));
            Assert.NotEqual(
                GatherRuleExecutor.ComputeCanonicalHash(new Dictionary<string, object> { ["v"] = null }),
                GatherRuleExecutor.ComputeCanonicalHash(new Dictionary<string, object> { ["v"] = "<null>" }));
        }

        // ── ExecuteRule wiring (async via startup path) ────────────────────

        [Fact]
        public void AlwaysMode_EmitsEveryCollection()
        {
            // EmitMode absent (legacy) — repeated identical results all emit. Two executions of
            // the same collector via two rules sharing the target prove no dedup is applied.
            var rule1 = OnChangeRule("GATHER-AL-1"); rule1.EmitMode = null; rule1.Trigger = "startup";
            var rule2 = OnChangeRule("GATHER-AL-2"); rule2.EmitMode = null; rule2.Trigger = "startup";

            _executor.UpdateRules(new List<GatherRule> { rule1, rule2 });

            Assert.True(_executor.WaitForStartupRules(30));
            Assert.True(WaitForEventCount(2));
        }

        [Fact]
        public void OnChange_FirstInScopePoll_EmitsExistsFalse_AsPollingIndicator()
        {
            // The first result on an absent key IS the visible "we're polling now" indicator.
            var rule = OnChangeRule();
            rule.Trigger = "startup";

            _executor.UpdateRules(new List<GatherRule> { rule });

            Assert.True(_executor.WaitForStartupRules(30));
            Assert.True(WaitForEventCount(1));
            var evt = SingleEvent();
            Assert.Equal(false, evt.Data["exists"]);
            Assert.Equal("GATHER-OC-001", evt.Data["ruleId"]); // injection happened after hashing
        }

        [Fact]
        public void OnChange_ComposedWithEmitOnlyIfExists_EmptyResultDoesNotTouchHashState()
        {
            // emitOnlyIfExists miss → empty result → no emit AND no hash update: the eventual
            // first real result must still count as "first emit".
            var rule = OnChangeRule();
            rule.Trigger = "startup";
            rule.Parameters = new Dictionary<string, string> { ["emitOnlyIfExists"] = "true" };

            _executor.UpdateRules(new List<GatherRule> { rule });
            Assert.True(_executor.WaitForStartupRules(30));
            Thread.Sleep(300);
            Assert.Equal(0, EventCount); // zero noise while the key is absent

            // When the key appears (simulated via the state machine), the result emits as first.
            Assert.True(_executor.ShouldEmitOnChange(rule, Result(("exists", true), ("Version", "1"))));
        }
    }
}
