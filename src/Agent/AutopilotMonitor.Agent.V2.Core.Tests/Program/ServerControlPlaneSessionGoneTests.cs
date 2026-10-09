#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Host wiring for <c>TelemetryUploadOrchestrator.SessionGone</c>
    /// (HTTP 410 on the current session's telemetry): exactly one
    /// <see cref="AgentErrorType.TelemetryRejectedSessionUnknown"/> emergency report carrying the
    /// status, then a synthetic forced <c>terminate_session</c> tagged <c>origin=session_gone</c>
    /// — and nothing at all once a shutdown is already in progress.
    /// </summary>
    public sealed class ServerControlPlaneSessionGoneTests
    {
        private sealed class RecordingEmergencyReporter : EmergencyReporter
        {
            private readonly List<string> _sequence;
            public List<(AgentErrorType ErrorType, string Message, int? HttpStatusCode)> Reports { get; } =
                new List<(AgentErrorType, string, int?)>();

            public RecordingEmergencyReporter(List<string> sequence)
                : base(apiClient: null!, sessionId: "S1", tenantId: "T1", agentVersion: "0.0.0", logger: null!)
            {
                _sequence = sequence;
            }

            public override Task TrySendAsync(
                AgentErrorType errorType,
                string message,
                int? httpStatusCode = null,
                long? sequenceNumber = null,
                int attempts = 1,
                TimeSpan? perAttemptTimeout = null,
                TimeSpan? retryDelay = null,
                double? sessionAgeHours = null,
                RegistrationFailureSummary? priorRegistrationFailure = null)
            {
                Reports.Add((errorType, message, httpStatusCode));
                _sequence.Add("report");
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingDispatcher : ServerActionDispatcher
        {
            private readonly List<string> _sequence;
            public List<ServerAction> Dispatched { get; } = new List<ServerAction>();

            public RecordingDispatcher(AgentConfiguration config, AgentLogger logger, InformationalEventPost post, List<string> sequence)
                : base(
                    configuration: config,
                    logger: logger,
                    rotateConfigAsync: () => Task.FromResult(true),
                    uploadDiagnosticsAsync: _ => Task.FromResult<DiagnosticsUploadResult>(null!),
                    onTerminateRequested: _ => Task.CompletedTask,
                    post: post)
            {
                _sequence = sequence;
            }

            public override Task DispatchAsync(List<ServerAction> actions)
            {
                Dispatched.AddRange(actions);
                _sequence.Add("dispatch");
                return Task.CompletedTask;
            }
        }

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public List<string> Sequence { get; } = new List<string>();
            public RecordingEmergencyReporter Reporter { get; }
            public RecordingDispatcher Dispatcher { get; }
            public ManualResetEventSlim Shutdown { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim ShutdownComplete { get; } = new ManualResetEventSlim(false);

            public Rig()
            {
                Logger = new AgentLogger(Path.Combine(Tmp.Path, "logs"), AgentLogLevel.Info);
                var config = new AgentConfiguration { SessionId = "S1", TenantId = "T1", ApiBaseUrl = "http://localhost" };
                var post = new InformationalEventPost(
                    new FakeSignalIngressSink(),
                    new VirtualClock(new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc)));
                Reporter = new RecordingEmergencyReporter(Sequence);
                Dispatcher = new RecordingDispatcher(config, Logger, post, Sequence);
            }

            public void Handle(string reason = "session gone: http 410") =>
                global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.HandleSessionGone(
                    reason, Reporter, Dispatcher, Shutdown, ShutdownComplete, Logger);

            public void Dispose()
            {
                Shutdown.Dispose();
                ShutdownComplete.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void Reports_once_with_410_then_dispatches_forced_terminate_tagged_session_gone()
        {
            using var rig = new Rig();

            rig.Handle();

            Assert.Equal(new[] { "report", "dispatch" }, rig.Sequence);

            var report = Assert.Single(rig.Reporter.Reports);
            Assert.Equal(AgentErrorType.TelemetryRejectedSessionUnknown, report.ErrorType);
            Assert.Equal(410, report.HttpStatusCode);
            Assert.Contains("410", report.Message);

            var action = Assert.Single(rig.Dispatcher.Dispatched);
            Assert.Equal(ServerActionTypes.TerminateSession, action.Type);
            Assert.Contains("410", action.Reason);
            Assert.NotNull(action.Params);
            var parameters = action.Params!;
            Assert.Equal("true", parameters["forceSelfDestruct"]);
            Assert.Equal("0", parameters["gracePeriodSeconds"]);
            Assert.Equal(TerminationOrigins.SessionGone, parameters["origin"]);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Does_nothing_when_a_shutdown_is_already_in_progress(bool shutdownSet, bool shutdownCompleteSet)
        {
            using var rig = new Rig();
            if (shutdownSet) rig.Shutdown.Set();
            if (shutdownCompleteSet) rig.ShutdownComplete.Set();

            rig.Handle();

            Assert.Empty(rig.Reporter.Reports);
            Assert.Empty(rig.Dispatcher.Dispatched);
        }

        [Fact]
        public void ReadOrigin_returns_the_origin_param_or_null()
        {
            Assert.Equal(TerminationOrigins.SessionGone,
                global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.ReadOrigin(
                    new Dictionary<string, string> { ["origin"] = TerminationOrigins.SessionGone }));
            Assert.Equal(TerminationOrigins.KillSignal,
                global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.ReadOrigin(
                    new Dictionary<string, string> { ["origin"] = TerminationOrigins.KillSignal, ["forceSelfDestruct"] = "true" }));
            Assert.Null(global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.ReadOrigin(
                new Dictionary<string, string> { ["adminOutcome"] = "Failed" }));
            Assert.Null(global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.ReadOrigin(
                new Dictionary<string, string> { ["origin"] = string.Empty }));
            Assert.Null(global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.ReadOrigin(null!));
        }
    }
}
