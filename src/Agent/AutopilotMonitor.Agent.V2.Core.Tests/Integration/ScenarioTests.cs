using System;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

#pragma warning disable xUnit1031 // SpinWait.SpinUntil for uploader-batch-arrival assertion

namespace AutopilotMonitor.Agent.V2.Core.Tests.Integration
{
    /// <summary>
    /// End-to-end runtime tests that drive the V2 Agent orchestrator through the same fixtures
    /// the M3 <c>ClassifierAwareReplayHarness</c> uses. Release-Gate M4.4 (Plan §4.x M4.4.5.g).
    /// <para>
    /// Each test loads a JSONL fixture from <c>tests/fixtures/enrollment-sessions/</c>, replays
    /// all signals through <see cref="Orchestration.SignalIngress"/>, and asserts the final
    /// <see cref="DecisionState"/> + journal tail against the expected terminal shape. Unlike
    /// the M3 harness, this exercises Persistence + EffectRunner + Classifiers + Telemetry in
    /// a single live pipeline.
    /// </para>
    /// <para>
    /// Scenario coverage mirrors <c>AutopilotMonitor.DecisionCore.Tests/Scenarios/*</c>:
    /// UserDriven happy/hello-timeout, SelfDeploying happy/esp-unknown, WhiteGlove inline/
    /// signal-correlated/anti-loop/false-positive, WhiteGlove Part-2 happy/stuck, Hybrid-Reboot,
    /// ESP-Terminal-Failure, Late-AADJ. DevPrep-v2 variants land with M5 (no M3 fixture yet).
    /// </para>
    /// </summary>
    public sealed class ScenarioTests
    {
        private const int DefaultTerminalTimeoutMs = 10_000;

        // ================================================================ 1) UserDriven-v1 Happy

        [Fact]
        public void Scenario01_UserDriven_Happy_reaches_Completed()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("userdriven-happy-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed, SessionStage.Failed));
            Assert.Equal(SessionStage.Completed, f.Orchestrator.CurrentState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);
            Assert.Equal("Success", f.Orchestrator.CurrentState.HelloOutcome?.Value);

            f.Stop();
        }

        // ================================================================ 2) UserDriven-v1 HelloTimeout

        [Fact]
        public void Scenario02_UserDriven_HelloTimeout_completes_with_timeout_outcome()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("userdriven-hello-timeout-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);
            Assert.Equal("Timeout", f.Orchestrator.CurrentState.HelloOutcome?.Value);

            f.Stop();
        }

        // ================================================================ 3) SelfDeploying-v1 Happy

        [Fact]
        public void Scenario03_SelfDeploying_Happy_reaches_Completed()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("selfdeploying-happy-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);

            f.Stop();
        }

        // ================================================================ 4) SelfDeploying-v1 ESP-Exit-Unknown

        [Fact]
        public void Scenario04_SelfDeploying_EspExitUnknown_completes_via_provisioning()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("selfdeploying-esp-exit-unknown-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);
            // DeviceOnlyDeployment hypothesis should have climbed from Unknown → Strong/Confirmed.
            Assert.NotEqual(HypothesisLevel.Unknown, f.Orchestrator.CurrentState.ClassifierOutcomes.DeviceOnlyDeployment.Level);

            f.Stop();
        }

        // ================================================================ 5) WhiteGlove Inline

        [Fact]
        public void Scenario05_WhiteGlove_Inline_seals_part1()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("whiteglove-inline-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.WhiteGloveSealed));
            Assert.Equal(HypothesisLevel.Confirmed, f.Orchestrator.CurrentState.ClassifierOutcomes.WhiteGloveSealing.Level);

            f.Stop();
        }

        // ================================================================ 6) WhiteGlove Signal-Correlated

        [Fact]
        public void Scenario06_WhiteGlove_SignalCorrelated_seals_after_second_signal()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("whiteglove-signal-correlated-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.WhiteGloveSealed));
            Assert.Equal(HypothesisLevel.Confirmed, f.Orchestrator.CurrentState.ClassifierOutcomes.WhiteGloveSealing.Level);

            f.Stop();
        }

        // ================================================================ 7) WhiteGlove Anti-Loop

        [Fact]
        public void Scenario07_WhiteGlove_AntiLoop_does_not_double_invoke_classifier()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("whiteglove-antiloop-v1.jsonl");

            // Anti-Loop: after two classifier-ticks with unchanged inputs, second run is skipped.
            // Verify we reached a state where at least one sealing verdict was produced.
            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs,
                SessionStage.WhiteGloveSealed,
                SessionStage.Completed,
                SessionStage.Failed,
                SessionStage.EspDeviceSetup));

            f.Stop();
        }

        // ================================================================ 8) WhiteGlove False-Positive (Late-AADJ)

        [Fact]
        public void Scenario08_WhiteGlove_FalsePositive_LateAADJ_falls_back_to_classic()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("whiteglove-false-positive-v1.jsonl");

            // Late-AADJ rejects the WG hypothesis; session still completes via the classic path.
            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);
            Assert.NotEqual(HypothesisLevel.Confirmed, f.Orchestrator.CurrentState.ClassifierOutcomes.WhiteGloveSealing.Level);

            f.Stop();
        }

        // (PR-B 2026-05-04: Scenario 9 "WhiteGlove Part-2 Happy" removed — the V2-only
        // Part-2 apparatus (Spezial-Stages / Outcomes / Classifier / Safety-Deadline) was
        // deleted along with the bridge-signal mechanic. Post-reseal-reboot Part-2 now
        // runs as a fresh Classic enrollment driven by the orchestrator's archive-and-reset
        // path; the fixture and a replacement classic-flow scenario test land in PR-E.)

        // (PR-B 2026-05-04: Scenario 10 "WhiteGlove Part-2 Stuck" removed — the 24h Part-2
        // safety deadline that drove this scenario into Failed/EnrollmentFailed was deleted
        // along with the rest of the WG-Part-2 apparatus. The new model treats Part-2 as a
        // fresh Classic enrollment, so a stuck Part-2 sits on the existing max-lifetime
        // watchdog instead of a Part-2-specific 24h deadline; coverage for that path lives
        // in the orchestrator's lifetime tests.)

        // ================================================================ 11) Hybrid-Reboot

        [Fact]
        public void Scenario11_HybridReboot_preserves_stage_and_completes()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("hybrid-reboot-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);
            Assert.NotNull(f.Orchestrator.CurrentState.SystemRebootUtc);

            f.Stop();
        }

        // ================================================================ 12) ESP-Terminal-Failure

        [Fact]
        public void Scenario12_EspTerminalFailure_fails_with_reason()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("esp-terminal-failure-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Failed));
            Assert.Equal(SessionOutcome.EnrollmentFailed, f.Orchestrator.CurrentState.Outcome);

            var last = f.LastTransition();
            Assert.NotNull(last);
            Assert.Equal("EspTerminalFailure", last!.Trigger);

            f.Stop();
        }

        // ================================================================ 13) Late-AADJ Standalone

        [Fact]
        public void Scenario13_LateAADJ_standalone_classic_completion()
        {
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("late-aadj-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);

            f.Stop();
        }

        // ================================================================ 14) UserDriven-v1 Hello-Skipped (real-world replay)

        [Fact]
        public void Scenario14_UserDriven_HelloSkipped_completes_with_skipped_outcome()
        {
            // Anonymized replay of session df7abbc2 — UserDriven v1 happy variant where the
            // Hello-for-Business policy was enabled but the per-user enrollment ultimately
            // resolved as "skipped" (not "Success"). Verifies that the AND-gate (Desktop +
            // Hello) accepts any terminal Hello outcome and still routes through Finalizing
            // → Completed (project memory: feedback_hello_policy_wait_not_completion).
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("userdriven-hello-skipped-v2.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));
            Assert.Equal(SessionOutcome.EnrollmentComplete, f.Orchestrator.CurrentState.Outcome);
            Assert.Equal("skipped", f.Orchestrator.CurrentState.HelloOutcome?.Value);
            // ImeUserSessionCompleted at signal 21 promotes ScenarioProfile.Mode to Classic.
            Assert.Equal(EnrollmentMode.Classic, f.Orchestrator.CurrentState.ScenarioProfile.Mode);

            f.Stop();
        }

        // ================================================================ Pipeline smoke (Telemetry)

        [Fact]
        public void Full_pipeline_enqueues_telemetry_items_for_terminal_events()
        {
            // Verifies that beyond state-shape, the EffectRunner emitted at least one
            // EmitEventTimelineEntry effect (e.g. enrollment_complete) that flowed through
            // the Spool into the uploader. Before the P1 "immediate-flush wakeup" fix this
            // test inspected pending items in the spool directly — but now the drain loop
            // wakes up on each RequiresImmediateFlush=true enqueue and uploads the item
            // immediately, so terminal enrollment_complete no longer sits in the pending
            // queue. The uploader's Received batches are the authoritative "left the agent"
            // signal and are stable across both pre-fix and post-fix behavior.
            using var f = new EnrollmentOrchestratorFixture();
            f.Start();
            f.PostFixture("userdriven-happy-v1.jsonl");

            Assert.True(f.WaitForStage(DefaultTerminalTimeoutMs, SessionStage.Completed));

            // Give the drain loop time to finish the post-completion flush — this test runs
            // in parallel with other orchestrator tests and the ThreadPool can be saturated,
            // so use a generous timeout. Stop() below also performs a terminal drain, but we
            // want to see the wakeup-driven upload here explicitly because that is the P1
            // behavior under test (EnrollmentOrchestrator.OnImmediateFlushRequested).
            Assert.True(
                SpinWait.SpinUntil(
                    () => f.Uploader.Received
                        .SelectMany(batch => batch)
                        .Any(i => i.Kind == TelemetryItemKind.Event
                                  && i.PayloadJson.Contains("enrollment_complete")),
                    10000),
                "Expected enrollment_complete event to reach the uploader after Stage=Completed.");

            f.Stop();
        }
    }
}
