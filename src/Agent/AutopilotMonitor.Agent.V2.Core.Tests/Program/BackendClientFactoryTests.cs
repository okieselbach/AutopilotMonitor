using System.IO;
using System.Net.Http;
using System.Net.Security;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.Agent.V2.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <see cref="BackendClientFactory"/>: Phase 3 (auth-clients bundle) and
    /// Phase 5 (telemetry-clients result) extracts from <c>Program.RunAgent</c>.
    /// </summary>
    public sealed class BackendClientFactoryTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static AgentConfiguration MinimalBootstrapTokenConfig()
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

        // ============================================================ BuildAuthClients

        [Fact]
        public void BuildAuthClients_returns_bundle_with_all_clients_populated()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var config = MinimalBootstrapTokenConfig();

            var bundle = BackendClientFactory.BuildAuthClients(config, agentVersion: "1.2.3.4", logger: logger);

            Assert.NotNull(bundle);
            Assert.NotNull(bundle.BackendApiClient);
            Assert.NotNull(bundle.NetworkMetrics);
            Assert.NotNull(bundle.DistressReporter);
            Assert.NotNull(bundle.EmergencyReporter);
            Assert.NotNull(bundle.AuthFailureTracker);
            // HardwareInfo.GetHardwareInfo always returns string fields (possibly empty on
            // non-WMI environments) — the bundle must surface whatever it produced.
            Assert.NotNull(bundle.Manufacturer);
            Assert.NotNull(bundle.Model);
            Assert.NotNull(bundle.SerialNumber);
        }

        [Fact]
        public void BuildAuthClients_does_not_attempt_cert_lookup_when_bootstrap_auth_used()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var config = MinimalBootstrapTokenConfig();

            var bundle = BackendClientFactory.BuildAuthClients(config, agentVersion: "1.2.3.4", logger: logger);

            // With UseClientCertAuth=false, the factory skips the cert-store search entirely
            // → HasClientCertificate is false and no AuthCertificateMissing distress is
            // dispatched. Asserts the bundle is consistent with that path.
            Assert.False(bundle.HasClientCertificate);
        }

        [Fact]
        public void BuildAuthClients_carries_the_server_certificate_observer_for_both_stacks()
        {
            using var tmp = new TempDirectory();
            var bundle = BackendClientFactory.BuildAuthClients(MinimalBootstrapTokenConfig(), agentVersion: "1.2.3.4", logger: NewLogger(tmp.Path));

            Assert.NotNull(bundle.ServerCertificates);
        }

        [Fact]
        public void BuildBackendApiHttpClient_installs_the_observing_callback_on_the_inner_handler()
        {
            var observer = new ServerCertificateObserver();
            using var cert = ServerCertificateObserverTests.SelfSigned("CN=Example Inspection CA");

            using var client = BackendClientFactory.BuildBackendApiHttpClient(new NetworkMetrics(), clientCertificate: null, agentVersion: "1.2.3.4", observer);
            var inner = InnerHandler(client);
            var callback = inner.ServerCertificateCustomValidationCallback;

            Assert.NotNull(callback);
            Assert.False(callback!(null!, cert, null!, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.True(callback(null!, cert, null!, SslPolicyErrors.None));
            Assert.Equal(cert.Issuer, observer.LastIssuer);
        }

        [Fact]
        public void BuildBackendApiHttpClient_without_observer_keeps_the_default_validation()
        {
            using var client = BackendClientFactory.BuildBackendApiHttpClient(new NetworkMetrics(), clientCertificate: null, agentVersion: "1.2.3.4");

            Assert.Null(InnerHandler(client).ServerCertificateCustomValidationCallback);
        }

        // Pipeline: NetworkMetricsRecordingHandler → HttpClientHandler.
        private static HttpClientHandler InnerHandler(HttpClient client) =>
            (HttpClientHandler)((DelegatingHandler)MtlsHttpClientFactoryTests.GetPipelineHead(client)).InnerHandler;

        // =========================================================== TelemetryClientResult

        [Fact]
        public void TelemetryClientResult_Exit_carries_exit_code_and_no_clients()
        {
            var result = TelemetryClientResult.Exit(4);

            Assert.True(result.ShouldExit);
            Assert.Equal(4, result.ExitCode);
            Assert.Null(result.MtlsHttpClient);
            Assert.Null(result.Uploader);
        }

        [Fact]
        public void TelemetryClientResult_Continue_carries_clients_and_zero_exit_code()
        {
            using var tmp = new TempDirectory();
            using var fakeHttpClient = new System.Net.Http.HttpClient();

            // BackendTelemetryUploader is hard to construct purely (needs auth tracker etc.) —
            // pass null, the Continue() factory does not validate. The Result-type contract
            // is what we exercise here.
            var result = TelemetryClientResult.Continue(fakeHttpClient, uploader: null);

            Assert.False(result.ShouldExit);
            Assert.Equal(0, result.ExitCode);
            Assert.Same(fakeHttpClient, result.MtlsHttpClient);
            Assert.Null(result.Uploader);
        }
    }
}
