#nullable enable
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// OsBuildChangeHost — deterministic Windows-Update corroboration (session 7443317c).
    /// Persists CurrentBuild.UBR across restarts, emits <c>os_build_changed</c> on a diff and
    /// feeds <c>BuildChanged</c> to the WU channel census. All system access is seamed: the
    /// build comes from an injected reader, persistence goes to a temp state directory.
    /// </summary>
    public sealed class OsBuildChangeHostTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly AgentLogger _logger;

        public OsBuildChangeHostTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose() => _tmp.Dispose();

        private OsBuildChangeHost Build(string? build, string? stateDirectory = null) =>
            new OsBuildChangeHost(
                sessionId: "sess-osb",
                tenantId: "tenant-osb",
                logger: _logger,
                ingress: _sink,
                clock: new VirtualClock(At),
                stateDirectory: stateDirectory ?? _tmp.Path,
                buildReader: () => build);

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> ChangedEvents() =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == Constants.EventTypes.OsBuildChanged).ToList();

        private string StateFilePath => Path.Combine(_tmp.Path, OsBuildChangeHost.StateFileName);

        [Fact]
        public void FirstRun_SeedsStateFile_EmitsNothing()
        {
            var host = Build("26200.8037");

            host.Start();

            Assert.False(host.BuildChanged);
            Assert.Empty(ChangedEvents());
            var state = JsonConvert.DeserializeObject<OsBuildChangeHost.OsBuildState>(File.ReadAllText(StateFilePath))!;
            Assert.Equal("26200.8037", state.OsBuild);
        }

        [Fact]
        public void SameBuildAcrossRestart_EmitsNothing()
        {
            Build("26200.8037").Start();

            var second = Build("26200.8037");
            second.Start();

            Assert.False(second.BuildChanged);
            Assert.Empty(ChangedEvents());
        }

        [Fact]
        public void ChangedBuildAcrossRestart_EmitsOsBuildChanged_AndSetsFlag()
        {
            // Session 7443317c: 26200.8037 → 26200.8655 across the mid-OOBE WU reboot.
            Build("26200.8037").Start();

            var second = Build("26200.8655");
            second.Start();

            Assert.True(second.BuildChanged);
            var s = Assert.Single(ChangedEvents());
            Assert.Equal("Info", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            Assert.Contains("26200.8037 -> 26200.8655", s.Payload![SignalPayloadKeys.Message]);

            var data = (IReadOnlyDictionary<string, object>)s.TypedPayload!;
            Assert.Equal("26200.8037", data["previousBuild"]);
            Assert.Equal("26200.8655", data["currentBuild"]);
            Assert.True(data.ContainsKey("previousCapturedUtc"));

            // The new build is persisted — a third run on the same build stays silent.
            var third = Build("26200.8655");
            third.Start();
            Assert.False(third.BuildChanged);
            Assert.Single(ChangedEvents());
        }

        [Fact]
        public void UnreadableBuild_SkipsWithoutSeeding()
        {
            var host = Build(null);

            host.Start();

            Assert.False(host.BuildChanged);
            Assert.Empty(ChangedEvents());
            Assert.False(File.Exists(StateFilePath));
        }

        [Fact]
        public void CorruptStateFile_ReseedsSilently()
        {
            File.WriteAllText(StateFilePath, "{ not json !!");

            var host = Build("26200.8655");
            host.Start();

            Assert.False(host.BuildChanged);
            Assert.Empty(ChangedEvents());
            var state = JsonConvert.DeserializeObject<OsBuildChangeHost.OsBuildState>(File.ReadAllText(StateFilePath))!;
            Assert.Equal("26200.8655", state.OsBuild);
        }

        [Fact]
        public void NoStateDirectory_IsToleratedWithoutEmission()
        {
            var host = new OsBuildChangeHost(
                sessionId: "s",
                tenantId: "t",
                logger: _logger,
                ingress: _sink,
                clock: new VirtualClock(At),
                stateDirectory: null,
                buildReader: () => "26200.8655");

            host.Start();

            Assert.False(host.BuildChanged);
            Assert.Empty(ChangedEvents());
        }
    }
}
