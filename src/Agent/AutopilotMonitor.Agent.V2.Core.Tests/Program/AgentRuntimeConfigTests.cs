using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <see cref="AgentRuntimeConfig"/>: Phase 4 extract from <c>Program.RunAgent</c>.
    /// The Resolve path itself depends on live HTTP / WMI / cert-store side effects that
    /// are exercised by the existing scenario suite; here we cover the contract surface
    /// (RuntimeConfigBundle property mapping + argument validation).
    /// </summary>
    public sealed class AgentRuntimeConfigTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static AgentConfiguration MinimalConfig()
            => new AgentConfiguration
            {
                ApiBaseUrl = "https://example.invalid",
                TenantId = "00000000-0000-0000-0000-000000000123",
                SessionId = "11111111-1111-1111-1111-111111111111",
                BootstrapToken = "test-token",
                UseBootstrapTokenAuth = true,
                UseClientCertAuth = false,
                MaxAuthFailures = 3,
                AuthFailureTimeoutMinutes = 30,
            };

        [Fact]
        public void RuntimeConfigBundle_ctor_preserves_all_fields()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var auth = BackendClientFactory.BuildAuthClients(MinimalConfig(), agentVersion: "1.0", logger: logger);

            // Build the supporting state without going through Resolve (which would fetch
            // from a real backend). We exercise the bundle-shape contract here.
            var rcs = new AutopilotMonitor.Agent.V2.Core.Configuration.RemoteConfigService(
                auth.BackendApiClient, "tenant-x", logger,
                auth.EmergencyReporter, auth.AuthFailureTracker);
            var rc = new AgentConfigResponse();
            var merge = new RemoteConfigMergeResult();

            var bundle = new RuntimeConfigBundle(rcs, rc, merge);

            Assert.Same(rcs, bundle.RemoteConfigService);
            Assert.Same(rc, bundle.RemoteConfig);
            Assert.Same(merge, bundle.MergeResult);
        }

        [Fact]
        public void Resolve_throws_on_null_agent_config()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var auth = BackendClientFactory.BuildAuthClients(MinimalConfig(), agentVersion: "1.0", logger: logger);

            Assert.Throws<ArgumentNullException>(
                () => AgentRuntimeConfig.Resolve(null, auth, "1.0", consoleMode: false, logger: logger));
        }

        [Fact]
        public void Resolve_throws_on_null_auth_bundle()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            Assert.Throws<ArgumentNullException>(
                () => AgentRuntimeConfig.Resolve(MinimalConfig(), auth: null, agentVersion: "1.0", consoleMode: false, logger: logger));
        }

        [Fact]
        public void Resolve_throws_on_null_logger()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var auth = BackendClientFactory.BuildAuthClients(MinimalConfig(), agentVersion: "1.0", logger: logger);

            Assert.Throws<ArgumentNullException>(
                () => AgentRuntimeConfig.Resolve(MinimalConfig(), auth, "1.0", consoleMode: false, logger: null));
        }
    }
}
