#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Persistence
{
    /// <summary>
    /// BandwidthStatePersistence — the reboot-survivor store for the passive bandwidth
    /// estimator. Covers the save/load roundtrip (a new instance over the same state
    /// directory models an agent restart), the session-id ownership check, and fail-soft
    /// behavior on missing/corrupt files. The estimator-side import semantics (sanitizing,
    /// merge) live in BandwidthEstimatorTests.
    /// </summary>
    public sealed class BandwidthStatePersistenceTests
    {
        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private static BandwidthStateData SampleState(string sessionId) => new BandwidthStateData
        {
            SessionId = sessionId,
            SavedAtUtc = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc),
            InterimEmitted = true,
            WanSamplesMbps = new List<double> { 16.0, 15.5, 16.2 },
            LanSamplesMbps = new List<double> { 400.0 },
            WanBytesObserved = 18_000_000,
            LanBytesObserved = 150_000_000,
        };

        [Fact]
        public void Roundtrip_survives_an_agent_restart()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            new BandwidthStatePersistence(tmp.Path, logger).Save(SampleState("session-1"));

            // New instance over the same state directory = agent restarted after a reboot.
            var loaded = new BandwidthStatePersistence(tmp.Path, logger).Load("session-1");

            Assert.NotNull(loaded);
            Assert.True(loaded!.InterimEmitted);
            Assert.Equal(new List<double> { 16.0, 15.5, 16.2 }, loaded.WanSamplesMbps);
            Assert.Equal(new List<double> { 400.0 }, loaded.LanSamplesMbps);
            Assert.Equal(18_000_000, loaded.WanBytesObserved);
            Assert.Equal(150_000_000, loaded.LanBytesObserved);
        }

        [Fact]
        public void Load_rejects_state_from_a_different_session()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            new BandwidthStatePersistence(tmp.Path, logger).Save(SampleState("session-1"));

            Assert.Null(new BandwidthStatePersistence(tmp.Path, logger).Load("session-2"));
        }

        [Fact]
        public void Load_returns_null_when_no_file_exists()
        {
            using var tmp = new TempDirectory();
            Assert.Null(new BandwidthStatePersistence(tmp.Path, NewLogger(tmp.Path)).Load("session-1"));
        }

        [Fact]
        public void Load_is_fail_soft_on_corrupt_or_empty_file()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new BandwidthStatePersistence(tmp.Path, logger);

            File.WriteAllText(persistence.StateFilePath, "{ not valid json !!!");
            Assert.Null(persistence.Load("session-1"));

            File.WriteAllText(persistence.StateFilePath, "null");
            Assert.Null(persistence.Load("session-1"));

            File.WriteAllText(persistence.StateFilePath, "{}"); // valid JSON, no session id
            Assert.Null(persistence.Load("session-1"));
        }

        [Fact]
        public void Save_overwrites_atomically_and_latest_state_wins()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new BandwidthStatePersistence(tmp.Path, logger);

            persistence.Save(SampleState("session-1"));
            var updated = SampleState("session-1");
            updated.WanSamplesMbps!.Add(17.1);
            updated.WanBytesObserved = 25_000_000;
            persistence.Save(updated);

            var loaded = persistence.Load("session-1");
            Assert.Equal(4, loaded!.WanSamplesMbps!.Count);
            Assert.Equal(25_000_000, loaded.WanBytesObserved);
            Assert.False(File.Exists(persistence.StateFilePath + ".tmp")); // temp file cleaned up
        }
    }
}
