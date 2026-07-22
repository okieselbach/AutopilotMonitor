using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;
using ProgramAlias = AutopilotMonitor.Agent.V2.Program;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Pins the §observability contract on <see cref="LifecycleEmitters.EmitAgentStarted"/>
    /// (must carry <c>configVersion</c> + <c>remoteConfigFetched</c>) and
    /// <see cref="LifecycleEmitters.EmitRemoteConfigFetchFailedIfAny"/> (must emit a
    /// dedicated <c>remote_config_fetch_failed</c> wire event when the agent fell back
    /// to cache or built-in defaults; must NOT emit on success). Motivating incident:
    /// session 8f2bef72 (2026-05-19) where the only on-the-wire trace of a degraded
    /// agent run was the absence of expected events — invisible to operators.
    /// </summary>
    public class LifecycleEmittersRemoteConfigTests
    {
        // ── agent_started carries configVersion + remoteConfigFetched ─────────

        [Fact]
        public void EmitAgentStarted_OnSuccessfulFetch_FlagsRemoteConfigTrue()
        {
            using var rig = new EmitterRig();
            rig.RemoteConfig.SetFakeOutcome(RemoteConfigFetchOutcome.Succeeded, configVersion: 28);

            LifecycleEmitters.EmitAgentStarted(
                rig.Post, rig.AgentConfig, rig.PreviousExit, "2.0.806", rig.RemoteConfig, rig.Logger);

            var data = AssertSingleEmit(rig.Sink, expectedEventType: "agent_started");
            Assert.Equal(28, data["configVersion"]);
            Assert.Equal(true, data["remoteConfigFetched"]);
            Assert.Equal("Succeeded", data["remoteConfigOutcome"]);
        }

        [Fact]
        public void EmitAgentStarted_OnFallbackToDefaults_FlagsRemoteConfigFalse()
        {
            using var rig = new EmitterRig();
            // The motivating incident: cold-start → all retries fail → defaults applied.
            // The wire trace MUST make this visible at a glance.
            rig.RemoteConfig.SetFakeOutcome(RemoteConfigFetchOutcome.UsedDefaults, configVersion: 0);

            LifecycleEmitters.EmitAgentStarted(
                rig.Post, rig.AgentConfig, rig.PreviousExit, "2.0.806", rig.RemoteConfig, rig.Logger);

            var data = AssertSingleEmit(rig.Sink, expectedEventType: "agent_started");
            Assert.Equal(0, data["configVersion"]);
            Assert.Equal(false, data["remoteConfigFetched"]);
            Assert.Equal("UsedDefaults", data["remoteConfigOutcome"]);
        }

        [Fact]
        public void EmitAgentStarted_OnFallbackToCache_FlagsCacheOutcome()
        {
            using var rig = new EmitterRig();
            rig.RemoteConfig.SetFakeOutcome(RemoteConfigFetchOutcome.FromCache, configVersion: 27);

            LifecycleEmitters.EmitAgentStarted(
                rig.Post, rig.AgentConfig, rig.PreviousExit, "2.0.806", rig.RemoteConfig, rig.Logger);

            var data = AssertSingleEmit(rig.Sink, expectedEventType: "agent_started");
            Assert.Equal(27, data["configVersion"]); // stale-but-real version, not 0
            Assert.Equal(false, data["remoteConfigFetched"]); // cache != fresh fetch
            Assert.Equal("FromCache", data["remoteConfigOutcome"]);
        }

        [Fact]
        public void EmitAgentStarted_TolerantOfNullRemoteConfigService()
        {
            // Defensive: the agent must always emit agent_started even if the
            // RemoteConfigService construction failed before the lifecycle hook ran.
            using var rig = new EmitterRig();

            LifecycleEmitters.EmitAgentStarted(
                rig.Post, rig.AgentConfig, rig.PreviousExit, "2.0.806", remoteConfigService: null, rig.Logger);

            var data = AssertSingleEmit(rig.Sink, expectedEventType: "agent_started");
            Assert.Equal(0, data["configVersion"]);
            Assert.Equal(false, data["remoteConfigFetched"]);
            Assert.Equal(nameof(RemoteConfigFetchOutcome.NotAttempted), data["remoteConfigOutcome"]);
        }

        // ── remote_config_fetch_failed event ─────────────────────────────────

        [Fact]
        public void EmitFetchFailedIfAny_OnSuccess_DoesNotEmit()
        {
            using var rig = new EmitterRig();
            rig.RemoteConfig.SetFakeOutcome(RemoteConfigFetchOutcome.Succeeded, configVersion: 28);

            LifecycleEmitters.EmitRemoteConfigFetchFailedIfAny(
                rig.Post, rig.AgentConfig, rig.RemoteConfig, rig.Logger);

            Assert.Empty(rig.Sink.Posted);
        }

        [Fact]
        public void EmitFetchFailedIfAny_OnNotAttempted_DoesNotEmit()
        {
            using var rig = new EmitterRig();
            // No SetFakeOutcome → default = NotAttempted. Test the defensive branch
            // in case the emitter is wired before any fetch happens.

            LifecycleEmitters.EmitRemoteConfigFetchFailedIfAny(
                rig.Post, rig.AgentConfig, rig.RemoteConfig, rig.Logger);

            Assert.Empty(rig.Sink.Posted);
        }

        [Fact]
        public void EmitFetchFailedIfAny_OnDefaultsFallback_EmitsWithDetails()
        {
            using var rig = new EmitterRig();
            rig.RemoteConfig.SetFakeOutcome(
                RemoteConfigFetchOutcome.UsedDefaults,
                configVersion: 0,
                attempts: 4,
                failureType: "HttpRequestException",
                failureMessage: "simulated cold-start timeout");

            LifecycleEmitters.EmitRemoteConfigFetchFailedIfAny(
                rig.Post, rig.AgentConfig, rig.RemoteConfig, rig.Logger);

            var data = AssertSingleEmit(rig.Sink, expectedEventType: "remote_config_fetch_failed");
            Assert.Equal("UsedDefaults", data["outcome"]);
            Assert.Equal(4, data["attempts"]);
            Assert.Equal("HttpRequestException", data["failureType"]);
            Assert.Equal("simulated cold-start timeout", data["failureMessage"]);
            Assert.False(data.ContainsKey("authStatusCode")); // not an auth failure
        }

        [Fact]
        public void EmitFetchFailedIfAny_OnAuthFailure_IncludesStatusCode()
        {
            using var rig = new EmitterRig();
            rig.RemoteConfig.SetFakeOutcome(
                RemoteConfigFetchOutcome.UsedDefaults,
                configVersion: 0,
                attempts: 1, // auth bails immediately, no retry
                failureType: "BackendAuthException",
                failureMessage: "Unauthorized",
                authStatusCode: 401);

            LifecycleEmitters.EmitRemoteConfigFetchFailedIfAny(
                rig.Post, rig.AgentConfig, rig.RemoteConfig, rig.Logger);

            var data = AssertSingleEmit(rig.Sink, expectedEventType: "remote_config_fetch_failed");
            Assert.Equal(1, data["attempts"]);
            Assert.Equal(401, data["authStatusCode"]);
        }

        [Fact]
        public void EmitFetchFailedIfAny_OnCacheFallback_Emits()
        {
            // Cache fallback IS a degraded mode — the tenant might have flipped a
            // setting that the agent isn't seeing because the live fetch failed.
            // Worth a wire event so operators can correlate.
            using var rig = new EmitterRig();
            rig.RemoteConfig.SetFakeOutcome(
                RemoteConfigFetchOutcome.FromCache,
                configVersion: 27,
                attempts: 4,
                failureType: "HttpRequestException",
                failureMessage: "transient error");

            LifecycleEmitters.EmitRemoteConfigFetchFailedIfAny(
                rig.Post, rig.AgentConfig, rig.RemoteConfig, rig.Logger);

            var data = AssertSingleEmit(rig.Sink, expectedEventType: "remote_config_fetch_failed");
            Assert.Equal("FromCache", data["outcome"]);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static Dictionary<string, object> AssertSingleEmit(
            FakeSignalIngressSink sink, string expectedEventType)
        {
            Assert.Single(sink.Posted);
            var posted = sink.Posted[0];
            Assert.Equal(DecisionSignalKind.InformationalEvent, posted.Kind);
            Assert.NotNull(posted.Payload);
            Assert.Equal(expectedEventType, posted.Payload!["eventType"]);
            // typedPayload carries the Data dict object-preserving (per InformationalEventPost.cs).
            Assert.IsType<Dictionary<string, object>>(posted.TypedPayload);
            return (Dictionary<string, object>)posted.TypedPayload!;
        }

        private sealed class EmitterRig : System.IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Sink { get; }
            public InformationalEventPost Post { get; }
            public FakeRemoteConfigService RemoteConfig { get; }

            public AgentConfiguration AgentConfig { get; } = new AgentConfiguration
            {
                ApiBaseUrl = "https://example.invalid",
                SessionId = "S1",
                TenantId = "00000000-0000-0000-0000-000000000001",
                AgentMaxLifetimeMinutes = 360,
                MaxAuthFailures = 3,
                AuthFailureTimeoutMinutes = 30,
                DiagnosticsUploadMode = "Off",
                CommandLineArgs = string.Empty,
            };

            public ProgramAlias.PreviousExitSummary PreviousExit { get; }
                = new ProgramAlias.PreviousExitSummary { ExitType = "first_run" };

            public EmitterRig()
            {
                Logger = new AgentLogger(Path.Combine(Tmp.Path, "logs"), AgentLogLevel.Info);
                Sink = new FakeSignalIngressSink();
                Post = new InformationalEventPost(Sink, SystemClock.Instance, Logger);

                var apiClient = new BackendApiClient(
                    httpClient: new System.Net.Http.HttpClient(),
                    baseUrl: "http://localhost",
                    manufacturer: string.Empty,
                    model: string.Empty,
                    serialNumber: string.Empty,
                    useBootstrapTokenAuth: false,
                    bootstrapToken: null,
                    agentVersion: "0.0.0",
                    logger: Logger);
                RemoteConfig = new FakeRemoteConfigService(apiClient, Logger);
            }

            public void Dispose() => Tmp.Dispose();
        }

        /// <summary>
        /// Subclass with a setter for the outcome state so tests can pin emit behaviour
        /// without going through the full FetchConfigAsync retry path (that's covered
        /// separately in <c>RemoteConfigServiceRetryTests</c>).
        /// </summary>
        private sealed class FakeRemoteConfigService : RemoteConfigService
        {
            public FakeRemoteConfigService(BackendApiClient api, AgentLogger logger)
                : base(api, "00000000-0000-0000-0000-000000000001", logger) { }

            public void SetFakeOutcome(
                RemoteConfigFetchOutcome outcome,
                int configVersion = 0,
                int attempts = 1,
                string? failureType = null,
                string? failureMessage = null,
                int? authStatusCode = null)
            {
                // CurrentConfig is set via the protected SetConfig path — easiest:
                // a real FetchConfigAsync replacement that just stashes a canned config.
                typeof(RemoteConfigService).GetMethod("SetConfig",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(this, new object[] { new AgentConfigResponse { ConfigVersion = configVersion } });

                // The public outcome props have private setters; use reflection so the
                // test can control them without dirtying the production API surface.
                typeof(RemoteConfigService).GetProperty(nameof(LastFetchOutcome))?.SetValue(this, outcome);
                typeof(RemoteConfigService).GetProperty(nameof(LastFetchAttempts))?.SetValue(this, attempts);
                typeof(RemoteConfigService).GetProperty(nameof(LastFetchFailureType))?.SetValue(this, failureType);
                typeof(RemoteConfigService).GetProperty(nameof(LastFetchFailureMessage))?.SetValue(this, failureMessage);
                typeof(RemoteConfigService).GetProperty(nameof(LastFetchAuthStatusCode))?.SetValue(this, authStatusCode);
            }
        }
    }
}
