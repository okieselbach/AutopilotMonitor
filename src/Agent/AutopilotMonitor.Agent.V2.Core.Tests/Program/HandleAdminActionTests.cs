#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Regression gate for the soft admin-action semantics: backend <c>AdminAction=Succeeded</c>
    /// and <c>AdminAction=Failed</c> (portal Mark-Succeeded / Mark-Failed) both drive the agent's
    /// own soft-shutdown pipeline via the injected <c>onAdminTerminal</c> callback with that
    /// outcome — without forcing self-destruct — and only the first AdminAction of a process is
    /// acted on. The backend repeats AdminAction on every telemetry response; acting on each echo
    /// re-emitted <c>admin_marked_session</c> with an immediate upload whose response carried the
    /// next echo. Earlier, both outcomes were routed through the full
    /// <c>ServerActionDispatcher</c> path, which crashed with
    /// <c>InvalidOperationException: SignalIngress has been stopped</c> when a terminal-drain
    /// response arrived after the ingress was torn down.
    /// </summary>
    public sealed class HandleAdminActionTests
    {
        private static AgentConfiguration Config() => new AgentConfiguration
        {
            SessionId = "S1",
            TenantId = "T1",
            ApiBaseUrl = "http://localhost",
        };

        private static global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.AdminActionLatch NewLatch() =>
            new global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.AdminActionLatch();

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public InformationalEventPost Post { get; }
            public List<EnrollmentTerminatedEventArgs> OnAdminTerminalCalls { get; } = new List<EnrollmentTerminatedEventArgs>();

            public Rig()
            {
                Logger = new AgentLogger(Path.Combine(Tmp.Path, "logs"), AgentLogLevel.Info);
                Post = new InformationalEventPost(Ingress, new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));
            }

            public Action<EnrollmentTerminatedEventArgs> OnAdminTerminal => args => OnAdminTerminalCalls.Add(args);

            public IEnumerable<FakeSignalIngressSink.PostedSignal> EventsOfType(string eventType) =>
                Ingress.Posted.Where(p =>
                    p.Kind == DecisionSignalKind.InformationalEvent &&
                    p.Payload != null &&
                    p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var t) &&
                    string.Equals(t, eventType, StringComparison.Ordinal));

            public void Dispose() => Tmp.Dispose();
        }

        [Fact]
        public void Succeeded_emits_admin_marked_session_as_Info_and_terminates_with_Succeeded()
        {
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                adminAction: "Succeeded",
                latch: NewLatch(),
                stageNameAccessor: () => "EspAccountSetup",
                post: rig.Post,
                onAdminTerminal: rig.OnAdminTerminal,
                agentConfig: Config(),
                logger: rig.Logger);

            var emitted = rig.EventsOfType("admin_marked_session").ToList();
            Assert.Single(emitted);
            Assert.True(emitted[0].Payload!.TryGetValue(SignalPayloadKeys.Severity, out var sev));
            Assert.Equal(EventSeverity.Info.ToString(), sev);

            Assert.Single(rig.OnAdminTerminalCalls);
            var args = rig.OnAdminTerminalCalls[0];
            Assert.Equal(EnrollmentTerminationOutcome.Succeeded, args.Outcome);
            Assert.Equal(EnrollmentTerminationReason.DecisionTerminalStage, args.Reason);
            Assert.Equal("EspAccountSetup", args.StageName);
            Assert.Contains("Admin marked session as Succeeded", args.Details);

            // No server_action_* noise — an admin mark isn't a ServerAction.
            Assert.Empty(rig.EventsOfType("server_action_received"));
            Assert.Empty(rig.EventsOfType("server_action_executed"));
        }

        [Fact]
        public void Failed_emits_admin_marked_session_as_Warning_and_terminates_with_Failed()
        {
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                adminAction: "Failed",
                latch: NewLatch(),
                stageNameAccessor: () => "DeviceSetup",
                post: rig.Post,
                onAdminTerminal: rig.OnAdminTerminal,
                agentConfig: Config(),
                logger: rig.Logger);

            var emitted = rig.EventsOfType("admin_marked_session").ToList();
            Assert.Single(emitted);
            Assert.True(emitted[0].Payload!.TryGetValue(SignalPayloadKeys.Severity, out var sev));
            Assert.Equal(EventSeverity.Warning.ToString(), sev);

            Assert.Single(rig.OnAdminTerminalCalls);
            var args = rig.OnAdminTerminalCalls[0];
            Assert.Equal(EnrollmentTerminationOutcome.Failed, args.Outcome);
            Assert.Equal(EnrollmentTerminationReason.DecisionTerminalStage, args.Reason);
            Assert.Equal("DeviceSetup", args.StageName);
            Assert.Contains("Admin marked session as Failed", args.Details);
        }

        [Fact]
        public void Succeeded_is_case_insensitive()
        {
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                adminAction: "succeeded",
                latch: NewLatch(),
                stageNameAccessor: () => null,
                post: rig.Post,
                onAdminTerminal: rig.OnAdminTerminal,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Single(rig.OnAdminTerminalCalls);
            Assert.Equal(EnrollmentTerminationOutcome.Succeeded, rig.OnAdminTerminalCalls[0].Outcome);
        }

        [Fact]
        public void Unknown_outcome_defaults_to_Failed_path()
        {
            // Forward-safety: any unknown adminAction string is treated as Failed — the agent's
            // soft-shutdown path is the safer default.
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                adminAction: "Quarantined",
                latch: NewLatch(),
                stageNameAccessor: () => null,
                post: rig.Post,
                onAdminTerminal: rig.OnAdminTerminal,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Single(rig.OnAdminTerminalCalls);
            Assert.Equal(EnrollmentTerminationOutcome.Failed, rig.OnAdminTerminalCalls[0].Outcome);
        }

        [Fact]
        public void Null_onAdminTerminal_does_not_throw()
        {
            // Defensive path: onAdminTerminal can be null if the terminate handler wasn't wired yet.
            using var rig = new Rig();

            var ex = Record.Exception(() =>
                global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                    adminAction: "Failed",
                    latch: NewLatch(),
                    stageNameAccessor: () => null,
                    post: rig.Post,
                    onAdminTerminal: null!,
                    agentConfig: Config(),
                    logger: rig.Logger));

            Assert.Null(ex);
            Assert.Single(rig.EventsOfType("admin_marked_session"));
        }

        [Fact]
        public void Failed_tolerates_stageNameAccessor_throwing()
        {
            // The stage accessor reads orchestrator.CurrentState, which throws
            // InvalidOperationException before Start. The handler must not rethrow — stageName
            // is a nice-to-have for telemetry, not a correctness dependency.
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                adminAction: "Failed",
                latch: NewLatch(),
                stageNameAccessor: () => throw new InvalidOperationException("not started"),
                post: rig.Post,
                onAdminTerminal: rig.OnAdminTerminal,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Single(rig.OnAdminTerminalCalls);
            Assert.Null(rig.OnAdminTerminalCalls[0].StageName);
        }

        [Fact]
        public void Emit_suppresses_InvalidOperationException_from_stopped_ingress()
        {
            // Race regression gate: if the SignalIngress was stopped between the outer
            // shutdownComplete check and this Emit, the method must not surface the exception
            // to the transport's ServerResponseReceived handler — the original crash was
            // "SignalIngress has been stopped" caught upstream as an ERROR log.
            using var rig = new Rig();

            var stoppedPost = new InformationalEventPost(
                new ThrowingSink(),
                new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));

            var ex = Record.Exception(() =>
                global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                    adminAction: "Succeeded",
                    latch: NewLatch(),
                    stageNameAccessor: () => null,
                    post: stoppedPost,
                    onAdminTerminal: rig.OnAdminTerminal,
                    agentConfig: Config(),
                    logger: rig.Logger));

            Assert.Null(ex);

            // A lost timeline entry never cancels the admin's decision.
            Assert.Single(rig.OnAdminTerminalCalls);
            Assert.Equal(EnrollmentTerminationOutcome.Succeeded, rig.OnAdminTerminalCalls[0].Outcome);
        }

        [Fact]
        public void Failed_emit_race_still_terminates()
        {
            // If the timeline emit fails due to a stopped ingress, the agent's own soft-shutdown
            // must still run — the admin's decision to Mark-Failed outranks a lost timeline entry.
            using var rig = new Rig();

            var stoppedPost = new InformationalEventPost(
                new ThrowingSink(),
                new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));

            global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                adminAction: "Failed",
                latch: NewLatch(),
                stageNameAccessor: () => "DeviceSetup",
                post: stoppedPost,
                onAdminTerminal: rig.OnAdminTerminal,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Single(rig.OnAdminTerminalCalls);
            Assert.Equal(EnrollmentTerminationOutcome.Failed, rig.OnAdminTerminalCalls[0].Outcome);
        }

        [Theory]
        [InlineData("Succeeded")]
        [InlineData("Failed")]
        public void Repeated_echo_is_handled_once(string adminAction)
        {
            // The backend repeats AdminAction on every telemetry response. Each echo handled anew
            // emitted another immediate-upload timeline entry whose response carried the next echo.
            using var rig = new Rig();
            var latch = NewLatch();

            for (var i = 0; i < 3; i++)
            {
                global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                    adminAction: adminAction,
                    latch: latch,
                    stageNameAccessor: () => "EspAccountSetup",
                    post: rig.Post,
                    onAdminTerminal: rig.OnAdminTerminal,
                    agentConfig: Config(),
                    logger: rig.Logger);
            }

            Assert.Single(rig.EventsOfType("admin_marked_session"));
            Assert.Single(rig.OnAdminTerminalCalls);
        }

        [Fact]
        public void First_admin_action_of_the_process_wins()
        {
            // The first mark already started the shutdown with its outcome; a later change of
            // mind in the portal only updates the session row on the backend.
            using var rig = new Rig();
            var latch = NewLatch();

            foreach (var adminAction in new[] { "Succeeded", "Failed" })
            {
                global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleAdminAction(
                    adminAction: adminAction,
                    latch: latch,
                    stageNameAccessor: () => null,
                    post: rig.Post,
                    onAdminTerminal: rig.OnAdminTerminal,
                    agentConfig: Config(),
                    logger: rig.Logger);
            }

            var emitted = rig.EventsOfType("admin_marked_session").ToList();
            Assert.Single(emitted);
            Assert.True(emitted[0].Payload!.TryGetValue(SignalPayloadKeys.Severity, out var sev));
            Assert.Equal(EventSeverity.Info.ToString(), sev);

            Assert.Single(rig.OnAdminTerminalCalls);
            Assert.Equal(EnrollmentTerminationOutcome.Succeeded, rig.OnAdminTerminalCalls[0].Outcome);
        }

        [Fact]
        public void Latch_admits_exactly_one_of_concurrent_callers()
        {
            // Responses are raised on whichever thread ran the drain, so two can overlap.
            var latch = NewLatch();
            var admitted = 0;

            Parallel.For(0, 64, _ =>
            {
                if (latch.TryEnter()) Interlocked.Increment(ref admitted);
            });

            Assert.Equal(1, admitted);
        }

        private sealed class ThrowingSink : ISignalIngressSink
        {
            public void Post(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
                => throw new InvalidOperationException("SignalIngress has been stopped.");
        }
    }
}
