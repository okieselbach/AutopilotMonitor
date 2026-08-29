#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// Single-rail refactor plan §5.3 / §6.2 — ServerActionDispatcher must route all telemetry
    /// through <see cref="InformationalEventPost"/> (single-rail) and dedup at-least-once
    /// re-deliveries from the backend's PendingActions queue before any handler or telemetry
    /// side effect fires.
    /// </summary>
    public sealed class ServerActionDispatcherTests
    {
        private static AgentConfiguration Config() => new AgentConfiguration
        {
            SessionId = "S1",
            TenantId = "T1",
            ApiBaseUrl = "http://localhost",
        };

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public InformationalEventPost Post { get; }
            public int RotateConfigCalls;
            public bool RotateConfigShouldSucceed = true;
            public int DiagnosticsCalls;
            public DiagnosticsUploadResult? DiagnosticsResult = new DiagnosticsUploadResult { BlobName = "blob" };
            public int TerminateCalls;

            public Rig()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Post = new InformationalEventPost(Ingress, new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));
            }

            public ServerActionDispatcher Build() => new ServerActionDispatcher(
                configuration: Config(),
                logger: Logger,
                rotateConfigAsync: () => { RotateConfigCalls++; return Task.FromResult(RotateConfigShouldSucceed); },
                uploadDiagnosticsAsync: _ => { DiagnosticsCalls++; return Task.FromResult(DiagnosticsResult ?? new DiagnosticsUploadResult()); },
                onTerminateRequested: _ => { TerminateCalls++; return Task.CompletedTask; },
                post: Post);

            public IEnumerable<FakeSignalIngressSink.PostedSignal> EventsOfType(string eventType) =>
                Ingress.Posted.Where(p =>
                    p.Kind == DecisionSignalKind.InformationalEvent &&
                    p.Payload != null &&
                    p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var t) &&
                    string.Equals(t, eventType, StringComparison.Ordinal));

            public void Dispose() => Tmp.Dispose();
        }

        private static ServerAction TerminateAction(string? ruleId = null, DateTime? queuedAt = null) =>
            new ServerAction
            {
                Type = ServerActionTypes.TerminateSession,
                Reason = "admin-override",
                RuleId = ruleId,
                QueuedAt = queuedAt ?? new DateTime(2026, 4, 23, 11, 0, 0, DateTimeKind.Utc),
            };

        [Fact]
        public async Task Dispatch_rotate_config_emits_received_and_executed_via_ingress()
        {
            using var rig = new Rig();
            var action = new ServerAction
            {
                Type = ServerActionTypes.RotateConfig,
                QueuedAt = new DateTime(2026, 4, 23, 12, 0, 0, DateTimeKind.Utc),
            };

            await rig.Build().DispatchAsync(new List<ServerAction> { action });

            Assert.Equal(1, rig.RotateConfigCalls);
            Assert.Single(rig.EventsOfType("server_action_received"));
            Assert.Single(rig.EventsOfType("server_action_executed"));
            Assert.Empty(rig.EventsOfType("server_action_failed"));

            var received = rig.EventsOfType("server_action_received").First();
            Assert.Equal("ServerActionDispatcher", received.Payload![SignalPayloadKeys.Source]);
            // Event.Data (actionType etc.) now flows through the typed sidecar to preserve
            // structure end-to-end (single-rail plan §1.3). String payload carries only the
            // decision-relevant top-level fields.
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(received.TypedPayload!);
            Assert.Equal("rotate_config", (string)typed["actionType"]);
        }

        [Fact]
        public async Task Dispatch_rotate_config_failure_emits_received_and_failed()
        {
            using var rig = new Rig();
            rig.RotateConfigShouldSucceed = false;

            await rig.Build().DispatchAsync(new List<ServerAction>
            {
                new ServerAction { Type = ServerActionTypes.RotateConfig, QueuedAt = DateTime.UtcNow },
            });

            Assert.Single(rig.EventsOfType("server_action_received"));
            Assert.Single(rig.EventsOfType("server_action_failed"));
            Assert.Empty(rig.EventsOfType("server_action_executed"));

            var failed = rig.EventsOfType("server_action_failed").First();
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(failed.TypedPayload!);
            Assert.Equal("config_fetch_failed", (string)typed["failureReason"]);
        }

        [Fact]
        public async Task Dispatch_request_diagnostics_skipped_by_gate_surfaces_gate_name_as_failure_reason()
        {
            // Session 2550bcea (2026-08-29): the on-demand collection was refused by the
            // mode=Off gate and the portal only saw a generic "diagnostics_upload_failed".
            // The gate name must travel through to the event so the operator can tell a
            // config gate from a real upload error.
            using var rig = new Rig();
            rig.DiagnosticsResult = DiagnosticsUploadResult.SkippedBy(DiagnosticsUploadResult.SkipModeOff);

            await rig.Build().DispatchAsync(new List<ServerAction>
            {
                new ServerAction { Type = ServerActionTypes.RequestDiagnostics, QueuedAt = DateTime.UtcNow },
            });

            Assert.Equal(1, rig.DiagnosticsCalls);
            Assert.Empty(rig.EventsOfType("server_action_executed"));
            var failed = rig.EventsOfType("server_action_failed").Single();
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(failed.TypedPayload!);
            Assert.Equal(DiagnosticsUploadResult.SkipModeOff, (string)typed["failureReason"]);
        }

        [Fact]
        public async Task Dispatch_unknown_action_type_emits_failed_with_unknown_action_type_reason()
        {
            using var rig = new Rig();

            await rig.Build().DispatchAsync(new List<ServerAction>
            {
                new ServerAction { Type = "unsupported_future_type", QueuedAt = DateTime.UtcNow },
            });

            var failed = Assert.Single(rig.EventsOfType("server_action_failed"));
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(failed.TypedPayload!);
            Assert.Equal("unknown_action_type", (string)typed["failureReason"]);
        }

        // ============================================================= Plan §6.2 ActionId dedup

        [Fact]
        public async Task Dispatch_duplicate_terminate_session_is_squelched_before_any_side_effect()
        {
            using var rig = new Rig();
            var dispatcher = rig.Build();
            var first = TerminateAction();
            var second = TerminateAction(ruleId: first.RuleId, queuedAt: first.QueuedAt);

            await dispatcher.DispatchAsync(new List<ServerAction> { first });
            await dispatcher.DispatchAsync(new List<ServerAction> { second });

            // Exactly one telemetry pair + one handler invocation — the 77× storm is gone.
            Assert.Single(rig.EventsOfType("server_action_received"));
            Assert.Equal(1, rig.TerminateCalls);
        }

        [Fact]
        public async Task Dispatch_same_batch_with_duplicates_dedups_inside_batch()
        {
            using var rig = new Rig();
            var action = TerminateAction();

            await rig.Build().DispatchAsync(new List<ServerAction> { action, action, action });

            Assert.Single(rig.EventsOfType("server_action_received"));
            Assert.Equal(1, rig.TerminateCalls);
        }

        [Fact]
        public async Task Dispatch_distinct_queued_at_fires_independently_for_genuinely_different_actions()
        {
            using var rig = new Rig();
            var dispatcher = rig.Build();
            var first = TerminateAction(queuedAt: new DateTime(2026, 4, 23, 11, 0, 0, DateTimeKind.Utc));
            var second = TerminateAction(queuedAt: new DateTime(2026, 4, 23, 11, 5, 0, DateTimeKind.Utc));

            await dispatcher.DispatchAsync(new List<ServerAction> { first });
            await dispatcher.DispatchAsync(new List<ServerAction> { second });

            // Two genuinely distinct admin-issued terminates — both must dispatch.
            Assert.Equal(2, rig.EventsOfType("server_action_received").Count());
            Assert.Equal(2, rig.TerminateCalls);
        }

        [Fact]
        public async Task Dispatch_distinct_rule_ids_fire_independently_for_same_type_and_queued_at()
        {
            using var rig = new Rig();
            var dispatcher = rig.Build();
            var queuedAt = new DateTime(2026, 4, 23, 11, 0, 0, DateTimeKind.Utc);

            await dispatcher.DispatchAsync(new List<ServerAction>
            {
                TerminateAction(ruleId: "ruleA", queuedAt: queuedAt),
                TerminateAction(ruleId: "ruleB", queuedAt: queuedAt),
            });

            Assert.Equal(2, rig.EventsOfType("server_action_received").Count());
            Assert.Equal(2, rig.TerminateCalls);
        }

        [Fact]
        public async Task Dispatch_rotate_and_terminate_with_identical_queued_at_do_not_collide()
        {
            using var rig = new Rig();
            var dispatcher = rig.Build();
            var queuedAt = new DateTime(2026, 4, 23, 11, 0, 0, DateTimeKind.Utc);

            await dispatcher.DispatchAsync(new List<ServerAction>
            {
                new ServerAction { Type = ServerActionTypes.RotateConfig, QueuedAt = queuedAt },
                new ServerAction { Type = ServerActionTypes.TerminateSession, QueuedAt = queuedAt },
            });

            Assert.Equal(2, rig.EventsOfType("server_action_received").Count());
            Assert.Equal(1, rig.RotateConfigCalls);
            Assert.Equal(1, rig.TerminateCalls);
        }

        // ============================================================= Ingress-stopped race guard

        [Fact]
        public async Task Dispatch_with_stopped_ingress_does_not_throw_and_still_runs_handler()
        {
            // Race regression gate: a terminal-drain response that arrives after
            // SignalIngress.Stop but before TelemetryUploadOrchestrator.DrainAllAsync returns
            // must NOT surface as "InvalidOperationException: SignalIngress has been stopped"
            // from PostEvent — the dispatcher has to stay robust during the shutdown window so
            // any still-pending handler work (e.g. a follow-up rotate_config) can complete.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var stoppedPost = new InformationalEventPost(
                new ThrowingIngressSink(),
                new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));

            int rotateCalls = 0;
            var dispatcher = new ServerActionDispatcher(
                configuration: new AgentConfiguration { SessionId = "S1", TenantId = "T1", ApiBaseUrl = "http://localhost" },
                logger: logger,
                rotateConfigAsync: () => { rotateCalls++; return Task.FromResult(true); },
                uploadDiagnosticsAsync: _ => Task.FromResult(new DiagnosticsUploadResult()),
                onTerminateRequested: _ => Task.CompletedTask,
                post: stoppedPost);

            var ex = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(new List<ServerAction>
            {
                new ServerAction { Type = ServerActionTypes.RotateConfig, QueuedAt = DateTime.UtcNow },
            }));

            Assert.Null(ex);
            Assert.Equal(1, rotateCalls);
        }

        private sealed class ThrowingIngressSink : ISignalIngressSink
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
