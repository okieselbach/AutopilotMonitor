#nullable enable
using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Office
{
    /// <summary>
    /// OfficeInstallStatePersistence — the office-install-state.json file that carries the
    /// detector lifecycle across agent restarts/reboots. Fail-soft: missing/corrupt files load
    /// as null (fresh start), saves never throw.
    /// </summary>
    public sealed class OfficeInstallStatePersistenceTests
    {
        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        [Fact]
        public void Save_then_load_roundtrips_all_fields()
        {
            using var tmp = new TempDirectory();
            var sut = new OfficeInstallStatePersistence(tmp.Path, NewLogger(tmp.Path));
            var startedAt = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);

            sut.Save(new OfficeInstallStateData
            {
                State = OfficeInstallStateData.StateActive,
                StartedAtUtc = startedAt,
                StartedTrigger = "do",
                PeakDo = new OfficeDoPeakData
                {
                    JobCount = 3,
                    FileSize = 1200,
                    TotalBytesDownloaded = 1000,
                    BytesFromPeers = 0,
                    BytesFromHttp = 1000,
                    BytesFromCacheServer = 800,
                    DownloadMode = 1,
                },
            });

            var loaded = sut.Load();
            Assert.NotNull(loaded);
            Assert.Equal(OfficeInstallStateData.StateActive, loaded!.State);
            Assert.False(loaded.IsTerminal);
            Assert.Equal(startedAt, loaded.StartedAtUtc);
            Assert.Equal("do", loaded.StartedTrigger);
            Assert.NotNull(loaded.PeakDo);
            Assert.Equal(3, loaded.PeakDo!.JobCount);
            Assert.Equal(1200, loaded.PeakDo.FileSize);
            Assert.Equal(1000, loaded.PeakDo.TotalBytesDownloaded);
            Assert.Equal(1000, loaded.PeakDo.BytesFromHttp);
            Assert.Equal(800, loaded.PeakDo.BytesFromCacheServer);
            Assert.Equal(1, loaded.PeakDo.DownloadMode);
        }

        [Fact]
        public void Save_then_load_roundtrips_terminal_metadata()
        {
            // Session bc803955 — CompletedAtUtc/VersionReached feed the summary dialog's
            // synthetic Office row (duration + version).
            using var tmp = new TempDirectory();
            var sut = new OfficeInstallStatePersistence(tmp.Path, NewLogger(tmp.Path));
            var completedAt = new DateTime(2026, 6, 8, 12, 3, 22, DateTimeKind.Utc);

            sut.Save(new OfficeInstallStateData
            {
                State = OfficeInstallStateData.StateCompleted,
                CompletedAtUtc = completedAt,
                VersionReached = "16.0.20228.20190",
            });

            var loaded = sut.Load();
            Assert.Equal(completedAt, loaded!.CompletedAtUtc);
            Assert.Equal("16.0.20228.20190", loaded.VersionReached);
        }

        [Fact]
        public void Load_tolerates_state_files_from_older_agents_without_terminal_metadata()
        {
            using var tmp = new TempDirectory();
            var sut = new OfficeInstallStatePersistence(tmp.Path, NewLogger(tmp.Path));
            // Pre-terminal-metadata wire format (verbatim shape from a live 2.0.1404 session).
            File.WriteAllText(sut.StateFilePath,
                "{\"State\":\"Completed\",\"StartedAtUtc\":\"2026-08-18T07:56:16.8208141Z\",\"StartedTrigger\":\"registry\"}");

            var loaded = sut.Load();
            Assert.Equal(OfficeInstallStateData.StateCompleted, loaded!.State);
            Assert.Null(loaded.CompletedAtUtc);
            Assert.Null(loaded.VersionReached);
        }

        [Fact]
        public void Save_overwrites_a_previous_state()
        {
            using var tmp = new TempDirectory();
            var sut = new OfficeInstallStatePersistence(tmp.Path, NewLogger(tmp.Path));

            sut.Save(new OfficeInstallStateData { State = OfficeInstallStateData.StateActive });
            sut.Save(new OfficeInstallStateData { State = OfficeInstallStateData.StateCompleted });

            var loaded = sut.Load();
            Assert.Equal(OfficeInstallStateData.StateCompleted, loaded!.State);
            Assert.True(loaded.IsTerminal);
        }

        [Fact]
        public void Load_returns_null_when_no_file_exists()
        {
            using var tmp = new TempDirectory();
            var sut = new OfficeInstallStatePersistence(tmp.Path, NewLogger(tmp.Path));

            Assert.Null(sut.Load());
        }

        [Theory]
        [InlineData("not json at all {{{")]
        [InlineData("null")]
        [InlineData("{}")] // deserializes but State is empty → invalid
        public void Load_returns_null_for_corrupt_or_empty_state(string content)
        {
            using var tmp = new TempDirectory();
            var sut = new OfficeInstallStatePersistence(tmp.Path, NewLogger(tmp.Path));
            File.WriteAllText(sut.StateFilePath, content);

            Assert.Null(sut.Load());
        }

        [Theory]
        [InlineData(OfficeInstallStateData.StateActive, false)]
        [InlineData(OfficeInstallStateData.StateCompleted, true)]
        [InlineData(OfficeInstallStateData.StateFailed, true)]
        [InlineData(OfficeInstallStateData.StatePreinstalled, false)] // persisted for dedup, but re-arms
        public void IsTerminal_only_for_completed_and_failed(string state, bool expected)
        {
            Assert.Equal(expected, new OfficeInstallStateData { State = state }.IsTerminal);
        }

        [Fact]
        public void OfficeDoPeakData_maps_to_and_from_sample()
        {
            var sample = new OfficeDoSample
            {
                JobCount = 2,
                FileSize = 500,
                TotalBytesDownloaded = 400,
                BytesFromPeers = 50,
                BytesFromHttp = 350,
                BytesFromCacheServer = 300,
                DownloadMode = 3,
            };

            var roundtripped = OfficeDoPeakData.FromSample(sample)!.ToSample();

            Assert.Equal(sample.JobCount, roundtripped.JobCount);
            Assert.Equal(sample.FileSize, roundtripped.FileSize);
            Assert.Equal(sample.TotalBytesDownloaded, roundtripped.TotalBytesDownloaded);
            Assert.Equal(sample.BytesFromPeers, roundtripped.BytesFromPeers);
            Assert.Equal(sample.BytesFromHttp, roundtripped.BytesFromHttp);
            Assert.Equal(sample.BytesFromCacheServer, roundtripped.BytesFromCacheServer);
            Assert.Equal(sample.DownloadMode, roundtripped.DownloadMode);
            Assert.Null(OfficeDoPeakData.FromSample(null));
        }
    }
}
