#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using Xunit;
using ProgramAlias = AutopilotMonitor.Agent.V2.Program;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// A registration the backend answered with HTTP 410 at process start: the session was deleted.
    /// Exactly one <see cref="AgentErrorType.RegistrationRejectedSessionGone"/> report, then the
    /// start-time end of monitoring with a forced cleanup — the same end state as the emergency
    /// break and the kill signal. The WhiteGlove Part 2 start that registers a new session instead
    /// is covered by <c>SessionRegistrationHelperTests</c>; here only its trace on <c>agent_started</c>.
    /// </summary>
    public sealed class SessionGoneAtRegistrationTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;
        private readonly SessionIdPersistence _persistence;
        private readonly string _stateDir;
        private readonly List<string> _sequence = new List<string>();

        public SessionGoneAtRegistrationTests()
        {
            _logger = new AgentLogger(Path.Combine(_tmp.Path, "logs"), AgentLogLevel.Info);
            _persistence = new SessionIdPersistence(_tmp.Path);
            _persistence.GetOrCreate(_logger);
            _stateDir = Path.Combine(_tmp.Path, "State");
        }

        public void Dispose() => _tmp.Dispose();

        private sealed class RecordingEmergencyReporter : EmergencyReporter
        {
            private readonly List<string> _sequence;
            public List<(AgentErrorType ErrorType, string Message, int? HttpStatusCode, int Attempts)> Reports { get; } =
                new List<(AgentErrorType, string, int?, int)>();

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
                Reports.Add((errorType, message, httpStatusCode, attempts));
                _sequence.Add("report");
                return Task.CompletedTask;
            }
        }

        private CleanupService CleanupFactory()
        {
            _sequence.Add("cleanup");
            // The real factory spawns a PowerShell cleanup script; throwing makes the cleanup retry
            // swallow and log it, so the call is counted without a side effect (as in ProgramGuardsTests).
            throw new InvalidOperationException("test-harness: cleanup factory must not spawn real PowerShell in unit tests");
        }

        private bool MarkerExists => File.Exists(Path.Combine(_stateDir, "enrollment-complete.marker"));

        [Theory]
        [InlineData(true)]
        [InlineData(false)] // forced like the kill signal: SelfDestructOnComplete does not gate it
        public void Reports_once_then_ends_monitoring_with_a_forced_cleanup(bool selfDestructOnComplete)
        {
            _persistence.SaveWhiteGloveComplete(_logger);
            var config = new AgentConfiguration { SessionId = "S1", TenantId = "T1", SelfDestructOnComplete = selfDestructOnComplete };
            var reporter = new RecordingEmergencyReporter(_sequence);

            var exitCode = BackendSessionRegistration.ConcludeSessionGone(
                config, reporter, _persistence, _stateDir, CleanupFactory, consoleMode: false, _logger);

            Assert.Equal(0, exitCode);
            // The report leaves before the cleanup deletes the agent.
            Assert.Equal(new[] { "report", "cleanup" }, _sequence);

            var report = Assert.Single(reporter.Reports);
            Assert.Equal(AgentErrorType.RegistrationRejectedSessionGone, report.ErrorType);
            Assert.Equal(410, report.HttpStatusCode);
            Assert.Contains("410", report.Message);
            Assert.True(report.Attempts > 1, "the process ends right after: the one report gets a retry");

            Assert.True(MarkerExists);
            Assert.False(_persistence.SessionExists());
            Assert.False(_persistence.IsWhiteGloveResume());
            Assert.Null(_persistence.LoadSessionCreatedAt());
            // The next start ends at the bootstrap guard, exactly as after the emergency break.
            Assert.True(ProgramAlias.CheckEnrollmentCompleteMarker(
                _stateDir, selfDestructOnComplete: false, cleanupServiceFactory: null, _logger, consoleMode: false));
        }

        [Fact]
        public void Ends_monitoring_without_a_reporter()
        {
            var config = new AgentConfiguration { SessionId = "S1", TenantId = "T1" };

            var exitCode = BackendSessionRegistration.ConcludeSessionGone(
                config, emergencyReporter: null!, _persistence, _stateDir, CleanupFactory, consoleMode: false, _logger);

            Assert.Equal(0, exitCode);
            Assert.Equal(new[] { "cleanup" }, _sequence);
            Assert.True(MarkerExists);
            Assert.False(_persistence.SessionExists());
        }

        [Theory]
        [InlineData(SessionRotationReasons.SessionGone)]
        [InlineData(SessionRotationReasons.OwnerMismatch)]
        public void Agent_started_names_why_the_session_was_rotated(string reason)
        {
            var data = EmitAgentStarted(rotatedFromSessionId: "11111111-1111-1111-1111-111111111111", reason);

            Assert.Equal(true, data["sessionRotated"]);
            Assert.Equal("11111111-1111-1111-1111-111111111111", data["previousSessionId"]);
            Assert.Equal(reason, data["sessionRotationReason"]);
        }

        [Fact]
        public void Agent_started_carries_no_rotation_reason_on_the_normal_path()
        {
            var data = EmitAgentStarted(rotatedFromSessionId: null, sessionRotationReason: null);

            Assert.Equal(false, data["sessionRotated"]);
            Assert.False(data.ContainsKey("sessionRotationReason"));
        }

        private Dictionary<string, object> EmitAgentStarted(string? rotatedFromSessionId, string? sessionRotationReason)
        {
            var sink = new FakeSignalIngressSink();
            LifecycleEmitters.EmitAgentStarted(
                new InformationalEventPost(sink, SystemClock.Instance, _logger),
                new AgentConfiguration { SessionId = "S1", TenantId = "00000000-0000-0000-0000-000000000001", CommandLineArgs = string.Empty },
                new ProgramAlias.PreviousExitSummary { ExitType = "reboot_kill" },
                "2.0.test",
                remoteConfigService: null,
                _logger,
                rotatedFromSessionId: rotatedFromSessionId,
                sessionRotationReason: sessionRotationReason);
            return (Dictionary<string, object>)Assert.Single(sink.Posted).TypedPayload!;
        }
    }
}
