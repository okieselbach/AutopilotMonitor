using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <c>Program.CheckConfigKillSignal</c> — the control-channel kill guard that
    /// runs after the Phase-4 config fetch. Pins the two safety contracts: the kill is ONLY
    /// honoured from a live backend fetch (never from the on-disk cache or built-in defaults),
    /// and a honoured kill behaves like the telemetry-channel kill (forced cleanup regardless
    /// of SelfDestructOnComplete, enrollment-complete marker so ghost restarts exit cleanly,
    /// persisted session cleared).
    /// </summary>
    public sealed class ProgramConfigKillSignalTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static AgentConfigResponse KillConfig() => new AgentConfigResponse
        {
            DeviceBlocked = true,
            DeviceKillSignal = true,
        };

        [Fact]
        public void No_kill_flag_does_not_trip()
        {
            using var tmp = new TempDirectory();
            var tripped = AutopilotMonitor.Agent.V2.Program.CheckConfigKillSignal(
                new AgentConfigResponse(),
                RemoteConfigFetchOutcome.Succeeded,
                dataDirectory: tmp.Path,
                stateDirectory: Path.Combine(tmp.Path, "State"),
                cleanupServiceFactory: null,
                logger: NewLogger(tmp.Path),
                consoleMode: false);

            Assert.False(tripped);
        }

        [Fact]
        public void Null_config_does_not_trip()
        {
            using var tmp = new TempDirectory();
            var tripped = AutopilotMonitor.Agent.V2.Program.CheckConfigKillSignal(
                null,
                RemoteConfigFetchOutcome.Succeeded,
                dataDirectory: tmp.Path,
                stateDirectory: Path.Combine(tmp.Path, "State"),
                cleanupServiceFactory: null,
                logger: NewLogger(tmp.Path),
                consoleMode: false);

            Assert.False(tripped);
        }

        [Theory]
        [InlineData(RemoteConfigFetchOutcome.FromCache)]
        [InlineData(RemoteConfigFetchOutcome.UsedDefaults)]
        [InlineData(RemoteConfigFetchOutcome.NotAttempted)]
        public void Kill_flag_from_non_live_fetch_is_ignored(RemoteConfigFetchOutcome outcome)
        {
            // CacheConfig/LoadCachedConfig strip the flag, so this state should be impossible —
            // the guard re-checks as defence-in-depth against a planted cache file.
            using var tmp = new TempDirectory();
            var stateDir = Path.Combine(tmp.Path, "State");

            var tripped = AutopilotMonitor.Agent.V2.Program.CheckConfigKillSignal(
                KillConfig(),
                outcome,
                dataDirectory: tmp.Path,
                stateDirectory: stateDir,
                cleanupServiceFactory: null,
                logger: NewLogger(tmp.Path),
                consoleMode: false);

            Assert.False(tripped);
            Assert.False(File.Exists(Path.Combine(stateDir, "enrollment-complete.marker")));
        }

        [Fact]
        public void Kill_from_live_fetch_trips_writes_marker_forces_cleanup_and_clears_session()
        {
            using var tmp = new TempDirectory();
            var stateDir = Path.Combine(tmp.Path, "State");
            var persistence = new SessionIdPersistence(tmp.Path);
            persistence.GetOrCreate();
            Assert.True(persistence.SessionExists());

            var factoryCalls = 0;

            var tripped = AutopilotMonitor.Agent.V2.Program.CheckConfigKillSignal(
                KillConfig(),
                RemoteConfigFetchOutcome.Succeeded,
                dataDirectory: tmp.Path,
                stateDirectory: stateDir,
                cleanupServiceFactory: () =>
                {
                    factoryCalls++;
                    // The real factory would spawn a PowerShell cleanup script; throwing here
                    // forces TryRetryCleanup to swallow + log so we can assert factoryCalls==1
                    // without a real side-effect.
                    throw new InvalidOperationException(
                        "test-harness: cleanup factory must not spawn real PowerShell in unit tests");
                },
                logger: NewLogger(tmp.Path),
                consoleMode: false);

            Assert.True(tripped);
            // Cleanup is FORCED (kill semantics = forceSelfDestruct, no SelfDestructOnComplete gate).
            Assert.Equal(1, factoryCalls);
            // Marker so ghost restarts exit cleanly even when cleanup failed.
            Assert.True(File.Exists(Path.Combine(stateDir, "enrollment-complete.marker")));
            // Persisted session cleared.
            Assert.False(persistence.SessionExists());
        }

        [Fact]
        public void Kill_with_null_cleanup_factory_still_trips_and_writes_marker()
        {
            using var tmp = new TempDirectory();
            var stateDir = Path.Combine(tmp.Path, "State");

            var tripped = AutopilotMonitor.Agent.V2.Program.CheckConfigKillSignal(
                KillConfig(),
                RemoteConfigFetchOutcome.Succeeded,
                dataDirectory: tmp.Path,
                stateDirectory: stateDir,
                cleanupServiceFactory: null,
                logger: NewLogger(tmp.Path),
                consoleMode: false);

            Assert.True(tripped);
            Assert.True(File.Exists(Path.Combine(stateDir, "enrollment-complete.marker")));
        }
    }
}
