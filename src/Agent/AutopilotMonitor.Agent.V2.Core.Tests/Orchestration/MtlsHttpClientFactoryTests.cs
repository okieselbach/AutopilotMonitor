using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class MtlsHttpClientFactoryTests
    {
        private sealed class FakeCertificateResolver : ICertificateResolver
        {
            private readonly X509Certificate2? _cert;
            public int CallCount { get; private set; }

            public FakeCertificateResolver(X509Certificate2? cert) => _cert = cert;

            public X509Certificate2? FindClientCertificate(AgentLogger logger)
            {
                CallCount++;
                return _cert;
            }
        }

        /// <summary>
        /// Creates an in-memory self-signed cert so the factory has something real to attach
        /// without touching the Windows cert store. <see cref="CertificateRequest"/> is
        /// available on net48 (4.7.2+).
        /// </summary>
        private static X509Certificate2 CreateSelfSignedCert()
        {
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest(
                    "CN=apmon-v2-test",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                return req.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(1));
            }
        }

        [Fact]
        public void CreateHandler_attaches_cert_and_enables_decompression()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();
            var resolver = new FakeCertificateResolver(cert);

            using var handler = MtlsHttpClientFactory.CreateHandler(resolver, logger);

            Assert.Equal(1, resolver.CallCount);
            Assert.Equal(
                DecompressionMethods.GZip | DecompressionMethods.Deflate,
                handler.AutomaticDecompression);
            Assert.Single(handler.ClientCertificates);
            Assert.Equal(cert.Thumbprint, ((X509Certificate2)handler.ClientCertificates[0]).Thumbprint);
        }

        [Fact]
        public void Create_returns_client_with_default_timeout()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();
            var resolver = new FakeCertificateResolver(cert);

            using var client = MtlsHttpClientFactory.Create(resolver, logger);

            Assert.NotNull(client);
            Assert.Equal(MtlsHttpClientFactory.DefaultTimeout, client.Timeout);
        }

        [Fact]
        public void Create_respects_custom_timeout()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();
            var resolver = new FakeCertificateResolver(cert);

            using var client = MtlsHttpClientFactory.Create(resolver, logger, TimeSpan.FromSeconds(5));

            Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
        }

        [Fact]
        public void CreateHandler_without_observer_keeps_the_default_validation()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();

            using var handler = MtlsHttpClientFactory.CreateHandler(new FakeCertificateResolver(cert), logger);

            Assert.Null(handler.ServerCertificateCustomValidationCallback);
        }

        [Fact]
        public void CreateHandler_with_observer_installs_an_observing_callback_that_never_weakens_validation()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();
            var observer = new ServerCertificateObserver();

            using var handler = MtlsHttpClientFactory.CreateHandler(new FakeCertificateResolver(cert), logger, observer);
            var callback = handler.ServerCertificateCustomValidationCallback;

            Assert.NotNull(callback);
            Assert.False(callback!(null!, cert, null!, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.True(callback(null!, cert, null!, SslPolicyErrors.None));
            Assert.Equal(cert.Issuer, observer.LastIssuer);
        }

        [Fact]
        public void CreateHandler_throws_when_resolver_returns_null()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var resolver = new FakeCertificateResolver(null);

            var ex = Assert.Throws<InvalidOperationException>(
                () => MtlsHttpClientFactory.CreateHandler(resolver, logger));

            Assert.Contains("no MDM client certificate found", ex.Message);
            Assert.Equal(1, resolver.CallCount);
        }

        [Fact]
        public void Create_rejects_null_dependencies()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();

            Assert.Throws<ArgumentNullException>(
                () => MtlsHttpClientFactory.Create(null!, logger));

            Assert.Throws<ArgumentNullException>(
                () => MtlsHttpClientFactory.Create(new FakeCertificateResolver(cert), null!));
        }

        [Fact]
        public void Create_without_metrics_keeps_pipeline_handler_only()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();
            var resolver = new FakeCertificateResolver(cert);

            using var client = MtlsHttpClientFactory.Create(resolver, logger);

            // No metrics ⇒ no recording handler in the chain. Pipeline head must be the
            // raw HttpClientHandler; this guards against accidentally always wrapping
            // (which would be a silent perf/double-counting hazard for callers without metrics).
            Assert.IsType<HttpClientHandler>(GetPipelineHead(client));
        }

        [Fact]
        public void Create_with_metrics_inserts_recording_handler_at_pipeline_head()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var cert = CreateSelfSignedCert();
            var resolver = new FakeCertificateResolver(cert);
            var metrics = new NetworkMetrics();

            using var client = MtlsHttpClientFactory.Create(resolver, logger, metrics: metrics);

            var head = GetPipelineHead(client);
            Assert.IsType<NetworkMetricsRecordingHandler>(head);
        }

        /// <summary>
        /// Reflectively retrieves <see cref="HttpClient"/>'s internal handler chain head.
        /// The field name varies between .NET Framework and .NET Core (handler / _handler);
        /// we scan the type hierarchy for the first <see cref="HttpMessageHandler"/>-typed
        /// instance field to stay version-tolerant.
        /// </summary>
        internal static HttpMessageHandler GetPipelineHead(HttpClient client)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            for (var t = (Type?)client.GetType(); t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags))
                {
                    if (typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType))
                    {
                        return (HttpMessageHandler)f.GetValue(client)!;
                    }
                }
            }
            throw new InvalidOperationException("HttpClient handler field not found via reflection.");
        }
    }
}
