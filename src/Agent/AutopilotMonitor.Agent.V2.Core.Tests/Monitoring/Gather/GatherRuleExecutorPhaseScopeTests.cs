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
    /// Phase scoping on <see cref="GatherRuleExecutor"/> (ActivePhases / ActiveFromPhase):
    /// the scope matrix is asserted synchronously via the internal <c>IsRuleInScope</c>
    /// (no timer races); the trigger-path wiring (deferred startup, phase_change / on_event
    /// gating) is asserted through the public API with positive event waits.
    /// Rules use the absent-registry-path pattern from
    /// <see cref="RegistryCollectorEmitOnlyIfExistsTests"/> so no real system state is touched.
    /// </summary>
    [Collection("SerialThreading")] // deferred startup rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorPhaseScopeTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly object _eventsGate = new object();
        private readonly GatherRuleExecutor _executor;

        private const string AbsentPath =
            "HKLM\\SOFTWARE\\AutopilotMonitorTests\\DefinitelyAbsent_5b1c8d2e-0000-0000-0000-000000000000";

        public GatherRuleExecutorPhaseScopeTests()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _executor = new GatherRuleExecutor(
                "sess", "tenant",
                evt => { lock (_eventsGate) _events.Add(evt); },
                logger)
            {
                // Bypass the registry allowlist guard for the synthetic test path.
                UnrestrictedMode = true
            };
        }

        public void Dispose()
        {
            _executor.Dispose();
            _tmp.Dispose();
        }

        private int EventCount { get { lock (_eventsGate) return _events.Count; } }

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

        /// <summary>Interval rule with a 1h period — the timer never ticks within a test run,
        /// so these rules exist purely for synchronous scope-matrix assertions.</summary>
        private static GatherRule ScopeProbeRule(string id, List<string>? activePhases = null, string? fromPhase = null)
            => new GatherRule
            {
                RuleId = id,
                Title = id,
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = "interval",
                IntervalSeconds = 3600,
                OutputEventType = "gather_test",
                Enabled = true,
                ActivePhases = activePhases,
                ActiveFromPhase = fromPhase,
            };

        private static GatherRule StartupRule(string id, List<string>? activePhases = null, string? fromPhase = null)
            => new GatherRule
            {
                RuleId = id,
                Title = id,
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = "startup",
                OutputEventType = "gather_test",
                Enabled = true,
                ActivePhases = activePhases,
                ActiveFromPhase = fromPhase,
            };

        // ── Scope matrix (synchronous) ─────────────────────────────────────

        [Fact]
        public void UnscopedRule_IsInScope_EvenBeforeAnyPhaseSignal()
        {
            var rule = ScopeProbeRule("GATHER-SCOPE-000");
            _executor.UpdateRules(new List<GatherRule> { rule });

            Assert.True(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void ActivePhases_InactiveBeforeFirstPhaseSignal()
        {
            var rule = ScopeProbeRule("GATHER-SCOPE-001", activePhases: new List<string> { "AccountSetup" });
            _executor.UpdateRules(new List<GatherRule> { rule });

            Assert.False(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void ActivePhases_ActiveDuringListedPhase_InactiveAfterLeaving()
        {
            var rule = ScopeProbeRule("GATHER-SCOPE-002", activePhases: new List<string> { "AccountSetup" });
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            Assert.False(_executor.IsRuleInScope(rule));

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Assert.True(_executor.IsRuleInScope(rule));

            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);
            Assert.False(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void ActivePhases_MatchIsCaseInsensitive()
        {
            var rule = ScopeProbeRule("GATHER-SCOPE-003", activePhases: new List<string> { "accountsetup" });
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Assert.True(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void FromPhase_LatchesAtThreshold_AndStaysSticky_IncludingThroughFailed()
        {
            var rule = ScopeProbeRule("GATHER-SCOPE-010", fromPhase: "AccountSetup");
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            Assert.False(_executor.IsRuleInScope(rule)); // threshold not reached

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Assert.True(_executor.IsRuleInScope(rule)); // latched

            _executor.OnPhaseChanged(EnrollmentPhase.Failed);
            Assert.True(_executor.IsRuleInScope(rule)); // sticky through Failed
        }

        [Fact]
        public void FromPhase_NoSpuriousActivationViaFailed()
        {
            // Failed=99 would ordinal-satisfy every threshold — it must never latch.
            var rule = ScopeProbeRule("GATHER-SCOPE-011", fromPhase: "AccountSetup");
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.Failed);
            Assert.False(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void FromPhase_LatchesWhenThresholdIsSkippedOver()
        {
            // The session may never report the exact threshold phase — a later phase latches too.
            var rule = ScopeProbeRule("GATHER-SCOPE-012", fromPhase: "AccountSetup");
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.FinalizingSetup);
            Assert.True(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void FromPhase_RuleDeliveredByRefreshAfterPhaseReached_LatchesImmediately()
        {
            _executor.UpdateRules(new List<GatherRule>());
            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);

            // Config refresh delivers a new from-phase rule whose threshold already passed.
            var rule = ScopeProbeRule("GATHER-SCOPE-013", fromPhase: "DeviceSetup");
            _executor.UpdateRules(new List<GatherRule> { rule });

            Assert.True(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void BothFieldsSet_ActivePhasesWins()
        {
            // Backend validation rejects this shape; the agent defensively prefers ActivePhases.
            var rule = ScopeProbeRule("GATHER-SCOPE-020",
                activePhases: new List<string> { "DeviceSetup" }, fromPhase: "Start");
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Assert.False(_executor.IsRuleInScope(rule)); // FromPhase(Start) would say yes — list wins

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            Assert.True(_executor.IsRuleInScope(rule));
        }

        [Fact]
        public void IgnorePhaseScope_BypassesScope_WithoutPhaseContext()
        {
            _executor.IgnorePhaseScope = true;
            var during = ScopeProbeRule("GATHER-SCOPE-030", activePhases: new List<string> { "AccountSetup" });
            var from = ScopeProbeRule("GATHER-SCOPE-031", fromPhase: "Complete");
            _executor.UpdateRules(new List<GatherRule> { during, from });

            Assert.True(_executor.IsRuleInScope(during));
            Assert.True(_executor.IsRuleInScope(from));
        }

        // ── Trigger-path wiring (async, positive waits) ────────────────────

        [Fact]
        public void UnscopedStartupRule_RunsImmediately()
        {
            _executor.UpdateRules(new List<GatherRule> { StartupRule("GATHER-START-000") });

            Assert.True(_executor.WaitForStartupRules(30));
            Assert.True(WaitForEventCount(1));
        }

        [Fact]
        public void ScopedStartupRule_IsDeferred_ThenFiresExactlyOnce_WhenScopeActivates()
        {
            _executor.UpdateRules(new List<GatherRule>
            {
                StartupRule("GATHER-START-001", fromPhase: "AccountSetup")
            });

            // Not in scope at UpdateRules: nothing queued, latch trivially satisfied.
            Assert.True(_executor.WaitForStartupRules(30));
            Assert.Equal(0, EventCount);

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            Assert.Equal(0, EventCount);

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Assert.True(WaitForEventCount(1), "deferred startup rule should fire when its scope activates");

            // Later phase changes must not re-fire it (dedup via _startupRulesExecuted).
            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);
            _executor.OnPhaseChanged(EnrollmentPhase.FinalizingSetup);
            Thread.Sleep(300);
            Assert.Equal(1, EventCount);
        }

        [Fact]
        public void PhaseChangeRule_OutOfScopePhase_DoesNotFire_InScopePhaseFires()
        {
            // TriggerPhase empty = fires on every phase change; scope narrows it to AccountSetup.
            var rule = new GatherRule
            {
                RuleId = "GATHER-PC-001",
                Title = "pc",
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = "phase_change",
                TriggerPhase = "",
                OutputEventType = "gather_test",
                Enabled = true,
                ActivePhases = new List<string> { "AccountSetup" },
            };
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Assert.True(WaitForEventCount(1));

            // Same phase again: the (rule, phase) dedup slot is now consumed.
            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Thread.Sleep(300);
            Assert.Equal(1, EventCount);
        }

        [Fact]
        public void OnEventRule_GatedByScope()
        {
            var rule = new GatherRule
            {
                RuleId = "GATHER-OE-001",
                Title = "oe",
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = "on_event",
                TriggerEventType = "app_install_failed",
                OutputEventType = "gather_test",
                Enabled = true,
                ActivePhases = new List<string> { "AccountSetup" },
            };
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnEvent("app_install_failed"); // no phase signal yet → out of scope
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            _executor.OnEvent("app_install_failed");
            Assert.True(WaitForEventCount(1));
        }

        [Fact]
        public void IgnorePhaseScope_ScopedStartupRule_RunsImmediately()
        {
            // The --run-gather-rules diagnostic path: scoped rules must execute unconditionally.
            _executor.IgnorePhaseScope = true;
            _executor.UpdateRules(new List<GatherRule>
            {
                StartupRule("GATHER-START-002", activePhases: new List<string> { "AccountSetup" })
            });

            Assert.True(_executor.WaitForStartupRules(30));
            Assert.True(WaitForEventCount(1));
        }
    }
}
