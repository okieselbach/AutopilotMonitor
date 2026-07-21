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
    /// The <c>phase_exit</c> trigger: a one-shot collection when a phase is LEFT — the closing
    /// bookend to <c>phase_change</c> (which fires on ENTER). The distinguishing invariant is that
    /// both the TriggerPhase match and the phase-scope gate must evaluate against the phase being
    /// left, i.e. before the executor's current-phase state moves forward.
    /// Rules use the absent-registry-path pattern so no real system state is touched.
    /// </summary>
    [Collection("SerialThreading")] // rules execute on the shared ThreadPool
    public sealed class GatherRuleExecutorPhaseExitTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();
        private readonly object _eventsGate = new object();
        private readonly GatherRuleExecutor _executor;

        private const string AbsentPath =
            "HKLM\\SOFTWARE\\AutopilotMonitorTests\\DefinitelyAbsent_9c4d2a7f-0000-0000-0000-000000000000";

        public GatherRuleExecutorPhaseExitTests()
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

        private static GatherRule ExitRule(
            string id, string triggerPhase, List<string> activePhases = null, string fromPhase = null)
            => new GatherRule
            {
                RuleId = id,
                Title = id,
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = "phase_exit",
                TriggerPhase = triggerPhase,
                OutputEventType = "gather_test",
                Enabled = true,
                ActivePhases = activePhases,
                ActiveFromPhase = fromPhase,
            };

        [Fact]
        public void FiresOnceWhenTheNamedPhaseIsLeft_NotWhenItIsEntered()
        {
            _executor.UpdateRules(new List<GatherRule> { ExitRule("GATHER-PX-001", "AccountSetup") });

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);

            // Entering AccountSetup must NOT fire an exit rule.
            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);

            // Leaving it does.
            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);
            Assert.True(WaitForEventCount(1));
        }

        [Fact]
        public void DoesNotRefireWhenThePhaseIsLeftTwice()
        {
            _executor.UpdateRules(new List<GatherRule> { ExitRule("GATHER-PX-002", "AccountSetup") });

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);
            Assert.True(WaitForEventCount(1));

            // A phase can be re-entered (ESP re-reporting); the one-shot slot stays consumed.
            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);
            Thread.Sleep(400);
            Assert.Equal(1, EventCount);
        }

        [Fact]
        public void EmptyTriggerPhase_FiresOnEveryExit()
        {
            _executor.UpdateRules(new List<GatherRule> { ExitRule("GATHER-PX-003", "") });

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup); // exit of Unknown — must NOT fire
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup); // exit of DeviceSetup
            Assert.True(WaitForEventCount(1));

            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);     // exit of AccountSetup
            Assert.True(WaitForEventCount(2));
        }

        [Fact]
        public void NeverFiresOnExitOfUnknown()
        {
            // Before the first phase signal there is no phase to leave.
            _executor.UpdateRules(new List<GatherRule> { ExitRule("GATHER-PX-004", "") });

            _executor.OnPhaseChanged(EnrollmentPhase.Start);
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);
        }

        [Fact]
        public void RepeatedSamePhaseSignal_IsNotAnExit()
        {
            _executor.UpdateRules(new List<GatherRule> { ExitRule("GATHER-PX-005", "") });

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup); // same phase — no transition
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);
        }

        [Fact]
        public void FiresOnTransitionIntoFailed_CapturingStateAtTheFailurePoint()
        {
            _executor.UpdateRules(new List<GatherRule> { ExitRule("GATHER-PX-006", "AccountSetup") });

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.Failed);

            Assert.True(WaitForEventCount(1), "leaving a phase into Failed must still snapshot it");
        }

        [Fact]
        public void FiresOnExitOfCompleteOnly_WhenCompleteIsActuallyLeft()
        {
            // "End of enrollment" is better served by phase_change on Complete / on_event; an exit
            // rule on Complete only fires if a later transition happens at all.
            _executor.UpdateRules(new List<GatherRule> { ExitRule("GATHER-PX-007", "Complete") });

            _executor.OnPhaseChanged(EnrollmentPhase.FinalizingSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.Complete);
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);
        }

        [Fact]
        public void ScopeGate_EvaluatesAgainstThePhaseBeingLeft()
        {
            // Scope = only during DeviceSetup. Leaving DeviceSetup must fire (the rule is still in
            // scope at that instant) even though the NEW phase is outside the scope.
            _executor.UpdateRules(new List<GatherRule>
            {
                ExitRule("GATHER-PX-010", "DeviceSetup", activePhases: new List<string> { "DeviceSetup" })
            });

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);

            Assert.True(WaitForEventCount(1),
                "the scope gate must see the phase being left, not the one being entered");
        }

        [Fact]
        public void OutOfScopeExit_DoesNotFire_AndDoesNotConsumeTheOneShotSlot()
        {
            // Scope = only during AccountSetup; the rule exits DeviceSetup while out of scope.
            var rule = ExitRule("GATHER-PX-011", "", activePhases: new List<string> { "AccountSetup" });
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.DeviceSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup); // exit of DeviceSetup: out of scope
            Thread.Sleep(300);
            Assert.Equal(0, EventCount);

            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);     // exit of AccountSetup: in scope
            Assert.True(WaitForEventCount(1));
        }

        [Fact]
        public void ExitAndChangeRulesForTheSamePhase_BothFireIndependently()
        {
            // The dedup key spaces must not collide: same ruleId shape, same phase, different trigger.
            var enter = new GatherRule
            {
                RuleId = "GATHER-PX-020",
                Title = "enter",
                CollectorType = "registry",
                Target = AbsentPath,
                Trigger = "phase_change",
                TriggerPhase = "AccountSetup",
                OutputEventType = "gather_test",
                Enabled = true,
            };
            var exit = ExitRule("GATHER-PX-021", "AccountSetup");
            _executor.UpdateRules(new List<GatherRule> { enter, exit });

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            Assert.True(WaitForEventCount(1)); // enter fired

            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);
            Assert.True(WaitForEventCount(2)); // exit fired
        }

        [Fact]
        public void ExitRuleWithEmitOnChange_StillEmitsItsFirstResult()
        {
            var rule = ExitRule("GATHER-PX-030", "AccountSetup");
            rule.EmitMode = "on_change";
            _executor.UpdateRules(new List<GatherRule> { rule });

            _executor.OnPhaseChanged(EnrollmentPhase.AccountSetup);
            _executor.OnPhaseChanged(EnrollmentPhase.AppsUser);

            Assert.True(WaitForEventCount(1));
        }
    }
}
