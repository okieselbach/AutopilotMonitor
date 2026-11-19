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
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.Shared.Models;
using Xunit;
using ProgramAlias = AutopilotMonitor.Agent.V2.Program;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// The backend refused with 403 <c>TenantGraphAccessLost</c>: the tenant's Graph access is
    /// confirmed lost and no device of it can be validated. At start the agent ends monitoring like
    /// the emergency break (marker, cleanup per <c>SelfDestructOnComplete</c>), also on a WhiteGlove
    /// resume; while running it terminates through a synthetic <c>terminate_session</c> that leaves
    /// the cleanup to the same setting. No emergency report on either path.
    /// </summary>
    public sealed class TenantGraphAccessLostTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;
        private readonly SessionIdPersistence _persistence;
        private readonly string _stateDir;
        private int _cleanups;

        public TenantGraphAccessLostTests()
        {
            _logger = new AgentLogger(Path.Combine(_tmp.Path, "logs"), AgentLogLevel.Info);
            _persistence = new SessionIdPersistence(_tmp.Path);
            _persistence.GetOrCreate(_logger);
            _stateDir = Path.Combine(_tmp.Path, "State");
        }

        public void Dispose() => _tmp.Dispose();

        private CleanupService CleanupFactory()
        {
            _cleanups++;
            // Throwing makes the cleanup retry swallow it: counted without spawning PowerShell.
            throw new InvalidOperationException("test-harness: cleanup factory must not spawn real PowerShell in unit tests");
        }

        [Theory]
        [InlineData(true, 1)]
        [InlineData(false, 0)]   // not forced: the tenant's setting decides, like the emergency break
        public void At_start_monitoring_ends_with_cleanup_per_SelfDestructOnComplete(bool selfDestructOnComplete, int expectedCleanups)
        {
            _persistence.SaveWhiteGloveComplete(_logger);
            var config = new AgentConfiguration { SessionId = "S1", TenantId = "T1", SelfDestructOnComplete = selfDestructOnComplete };

            var exitCode = BackendSessionRegistration.ConcludeTenantGraphAccessLost(
                config, _persistence, _stateDir, CleanupFactory, consoleMode: false, _logger);

            Assert.Equal(0, exitCode);
            Assert.Equal(expectedCleanups, _cleanups);
            Assert.True(File.Exists(Path.Combine(_stateDir, "enrollment-complete.marker")));
            Assert.False(_persistence.SessionExists());
            // The WhiteGlove resume is over too: the 48-hour break would never end it.
            Assert.False(_persistence.IsWhiteGloveResume());
            // The next start ends at the bootstrap guard instead of asking the backend again.
            Assert.True(ProgramAlias.CheckEnrollmentCompleteMarker(
                _stateDir, selfDestructOnComplete: false, cleanupServiceFactory: null, _logger, consoleMode: false));
        }

        private sealed class RecordingDispatcher : ServerActionDispatcher
        {
            public List<ServerAction> Dispatched { get; } = new List<ServerAction>();

            public RecordingDispatcher(AgentConfiguration config, AgentLogger logger, InformationalEventPost post)
                : base(
                    configuration: config,
                    logger: logger,
                    rotateConfigAsync: () => Task.FromResult(true),
                    uploadDiagnosticsAsync: _ => Task.FromResult<DiagnosticsUploadResult>(null!),
                    onTerminateRequested: _ => Task.CompletedTask,
                    post: post)
            {
            }

            public override Task DispatchAsync(List<ServerAction> actions)
            {
                Dispatched.AddRange(actions);
                return Task.CompletedTask;
            }
        }

        private RecordingDispatcher NewDispatcher()
            => new RecordingDispatcher(
                new AgentConfiguration { SessionId = "S1", TenantId = "T1", ApiBaseUrl = "http://localhost" },
                _logger,
                new InformationalEventPost(new FakeSignalIngressSink(), new VirtualClock(new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc))));

        [Fact]
        public void While_running_it_dispatches_an_unforced_terminate_tagged_tenant_graph_access_lost()
        {
            var dispatcher = NewDispatcher();
            using var shutdown = new ManualResetEventSlim(false);
            using var shutdownComplete = new ManualResetEventSlim(false);

            ServerControlPlane.HandleTenantGraphAccessLost(
                "tenant graph access lost: http 403", dispatcher, shutdown, shutdownComplete, _logger);

            var action = Assert.Single(dispatcher.Dispatched);
            Assert.Equal(ServerActionTypes.TerminateSession, action.Type);
            var parameters = action.Params!;
            Assert.Equal("false", parameters["forceSelfDestruct"]);
            Assert.Equal("0", parameters["gracePeriodSeconds"]);
            Assert.Equal(TerminationOrigins.TenantGraphAccessLost, parameters["origin"]);
            Assert.Equal(TerminationOrigins.TenantGraphAccessLost, ServerControlPlane.ReadOrigin(parameters));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void While_running_it_does_nothing_once_a_shutdown_is_in_progress(bool shutdownSet, bool shutdownCompleteSet)
        {
            var dispatcher = NewDispatcher();
            using var shutdown = new ManualResetEventSlim(shutdownSet);
            using var shutdownComplete = new ManualResetEventSlim(shutdownCompleteSet);

            ServerControlPlane.HandleTenantGraphAccessLost(
                "tenant graph access lost: http 403", dispatcher, shutdown, shutdownComplete, _logger);

            Assert.Empty(dispatcher.Dispatched);
        }

        [Fact]
        public void A_session_gone_terminate_still_forces_the_cleanup()
        {
            // The shared synthetic-terminate helper must keep the 410 path forced.
            var dispatcher = NewDispatcher();
            using var shutdown = new ManualResetEventSlim(false);
            using var shutdownComplete = new ManualResetEventSlim(false);

            ServerControlPlane.HandleSessionGone(
                "session gone: http 410", emergencyReporter: null!, dispatcher, shutdown, shutdownComplete, _logger);

            var action = Assert.Single(dispatcher.Dispatched);
            Assert.Equal("true", action.Params!["forceSelfDestruct"]);
            Assert.Equal(TerminationOrigins.SessionGone, action.Params["origin"]);
        }
    }
}
