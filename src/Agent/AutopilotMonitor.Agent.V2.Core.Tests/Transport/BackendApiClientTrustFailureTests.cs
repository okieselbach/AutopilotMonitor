#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// A send that fails because the device rejects the server certificate must say who issued that
    /// certificate. Until this was built the log, the final registration line and the failed-run
    /// record carried only "An error occurred while sending the request", and TLS inspection with an
    /// untrusted CA could not be told from any other network failure.
    /// </summary>
    public sealed class BackendApiClientTrustFailureTests
    {
        private static BackendApiClient BuildClient(
            HttpMessageHandler handler, ServerCertificateObserver? serverCertificates, AgentLogger? logger = null, string baseUrl = "https://backend.example") =>
            new BackendApiClient(
                new HttpClient(handler),
                baseUrl: baseUrl,
                manufacturer: "Contoso",
                model: "TestBox",
                serialNumber: "SN123",
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "2.0.0",
                logger: logger,
                serverCertificates: serverCertificates);

        private static ServerCertificateObserver ObserverThatRejected(X509Certificate2 certificate)
        {
            var observer = new ServerCertificateObserver();
            observer.Validate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);
            return observer;
        }

        [Fact]
        public async Task A_rejected_certificate_on_registration_is_thrown_with_its_issuer()
        {
            using var certificate = ServerCertificateObserverTests.SelfSigned("CN=Example Inspection CA");
            var failure = InterceptionFixtures.TrustFailure();
            using var client = BuildClient(new RecordingHttpMessageHandler().QueueThrow(failure), ObserverThatRejected(certificate));

            var ex = await Assert.ThrowsAsync<BackendServerCertificateException>(
                () => client.RegisterSessionAsync(new SessionRegistration()));

            Assert.Same(failure, ex.InnerException);
            Assert.Equal(certificate.Issuer, ex.Rejection.Issuer);
            Assert.StartsWith(
                "TLS trust failure: backend.example presented a server certificate issued by \"CN=Example Inspection CA\" (RemoteCertificateChainErrors), not by Microsoft.",
                ex.Message);
            // Still the transport failure it wraps: whatever walks the chain finds the status.
            Assert.True(TransportErrorCodes.HasWebStatus(ex, WebExceptionStatus.TrustFailure));
        }

        [Fact]
        public async Task A_rejected_certificate_on_the_config_fetch_is_thrown_with_its_issuer()
        {
            using var certificate = ServerCertificateObserverTests.SelfSigned("CN=Example Inspection CA");
            using var client = BuildClient(
                new RecordingHttpMessageHandler().QueueThrow(InterceptionFixtures.TrustFailure()), ObserverThatRejected(certificate));

            var ex = await Assert.ThrowsAsync<BackendServerCertificateException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Contains("issued by \"CN=Example Inspection CA\"", ex.Message);
        }

        [Fact]
        public async Task A_trust_failure_without_a_seen_certificate_stays_as_it_was()
        {
            var failure = InterceptionFixtures.TrustFailure();
            using var client = BuildClient(new RecordingHttpMessageHandler().QueueThrow(failure), new ServerCertificateObserver());

            var thrown = await Assert.ThrowsAsync<HttpRequestException>(
                () => client.RegisterSessionAsync(new SessionRegistration()));

            Assert.Same(failure, thrown);
        }

        [Fact]
        public async Task Without_an_observer_the_failure_stays_as_it_was()
        {
            var failure = InterceptionFixtures.TrustFailure();
            using var client = BuildClient(new RecordingHttpMessageHandler().QueueThrow(failure), serverCertificates: null);

            var thrown = await Assert.ThrowsAsync<HttpRequestException>(
                () => client.RegisterSessionAsync(new SessionRegistration()));

            Assert.Same(failure, thrown);
        }

        [Fact]
        public async Task An_earlier_rejection_does_not_explain_a_failure_of_another_kind()
        {
            using var certificate = ServerCertificateObserverTests.SelfSigned("CN=Example Inspection CA");
            var failure = InterceptionFixtures.NameResolutionFailure();
            using var client = BuildClient(new RecordingHttpMessageHandler().QueueThrow(failure), ObserverThatRejected(certificate));

            var thrown = await Assert.ThrowsAsync<HttpRequestException>(
                () => client.RegisterSessionAsync(new SessionRegistration()));

            Assert.Same(failure, thrown);
        }

        [Fact]
        public async Task The_logged_exception_chain_closes_with_the_finding()
        {
            using var tmp = new TempDirectory();
            using var certificate = ServerCertificateObserverTests.SelfSigned("CN=Example Inspection CA");
            using var client = BuildClient(
                new RecordingHttpMessageHandler().QueueThrow(InterceptionFixtures.TrustFailure()),
                ObserverThatRejected(certificate),
                new AgentLogger(tmp.Path, AgentLogLevel.Info));

            await Assert.ThrowsAsync<BackendServerCertificateException>(
                () => client.RegisterSessionAsync(new SessionRegistration()));

            var log = File.ReadAllText(Path.Combine(tmp.Path, "agent.log"));
            Assert.Contains("[ERROR] PostAsync POST https://backend.example/api/agent/register-session", log);
            Assert.Contains("WebException.Status = TrustFailure", log);
            Assert.Contains("  => TLS trust failure: backend.example presented a server certificate issued by \"CN=Example Inspection CA\"", log);
        }

        [Fact]
        public async Task Another_failure_is_logged_without_a_finding()
        {
            using var tmp = new TempDirectory();
            using var client = BuildClient(
                new RecordingHttpMessageHandler().QueueThrow(InterceptionFixtures.NameResolutionFailure()),
                new ServerCertificateObserver(),
                new AgentLogger(tmp.Path, AgentLogLevel.Info));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterSessionAsync(new SessionRegistration()));

            var log = File.ReadAllText(Path.Combine(tmp.Path, "agent.log"));
            Assert.Contains("WebException.Status = NameResolutionFailure", log);
            Assert.DoesNotContain("TLS trust failure", log);
        }

        /// <summary>
        /// The whole path against a real handshake: a loopback TLS listener presents a self-signed
        /// certificate, Windows rejects it, and the client names its issuer. Only a real handshake
        /// proves that the validation callback gets the chain status and that the rejection
        /// surfaces as <see cref="WebExceptionStatus.TrustFailure"/> — a hand-built exception
        /// chain carries what its author believed.
        /// </summary>
        [Fact]
        public async Task A_real_handshake_with_an_untrusted_certificate_names_its_issuer()
        {
            using var tmp = new TempDirectory();
            using var identity = new ServerIdentity("CN=Example Inspection CA");
            var serverCertificate = identity.Certificate;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = ServeOneHandshakeAsync(listener, serverCertificate);

                var observer = new ServerCertificateObserver();
                var handler = new HttpClientHandler { UseProxy = false };
                observer.Attach(handler);
                using var client = BuildClient(
                    handler, observer, new AgentLogger(tmp.Path, AgentLogLevel.Info), baseUrl: $"https://127.0.0.1:{port}");

                var failure = await Record.ExceptionAsync(() => client.GetAgentConfigAsync("tenant-1"));
                // The server side ends with the handshake; the bound only keeps a surprise from hanging the suite.
                Assert.Same(server, await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(30))));
                // A server that could not present its certificate fails here, with its own reason.
                await server;

                var ex = Assert.IsType<BackendServerCertificateException>(failure);
                Assert.True(TransportErrorCodes.HasWebStatus(ex, WebExceptionStatus.TrustFailure));
                Assert.Equal(serverCertificate.Issuer, ex.Rejection.Issuer);
                Assert.False(ex.Rejection.IssuedByMicrosoft);
                Assert.True(ex.Rejection.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
                Assert.True(ex.Rejection.ChainStatus.HasFlag(X509ChainStatusFlags.UntrustedRoot));
                Assert.StartsWith(
                    "TLS trust failure: 127.0.0.1 presented a server certificate issued by \"CN=Example Inspection CA\" (",
                    ex.Message);
                Assert.Contains("UntrustedRoot", ex.Message);
                Assert.EndsWith("Exclude 127.0.0.1 from TLS inspection.", ex.Message);
                Assert.Contains("  => TLS trust failure: 127.0.0.1 presented a server certificate issued by", File.ReadAllText(Path.Combine(tmp.Path, "agent.log")));
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// A self-signed server certificate whose key Schannel can use: a named software key,
        /// removed again by <see cref="IDisposable.Dispose"/>. Nothing is added to a certificate
        /// store, so the certificate stays untrusted.
        /// </summary>
        private sealed class ServerIdentity : IDisposable
        {
            private readonly CngKey _key;

            public ServerIdentity(string subject)
            {
                _key = CreateKey("apm-test-" + Guid.NewGuid().ToString("N"));
                using var rsa = new RSACng(_key);
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            }

            /// <summary>
            /// The user's key store first. A logon without the user's credentials (a session opened
            /// with an SSH key) may not write there; an elevated one still has the machine's store.
            /// </summary>
            private static CngKey CreateKey(string name)
            {
                try
                {
                    return CngKey.Create(CngAlgorithm.Rsa, name, KeyParameters(CngKeyCreationOptions.None));
                }
                catch (CryptographicException)
                {
                    return CngKey.Create(CngAlgorithm.Rsa, name, KeyParameters(CngKeyCreationOptions.MachineKey));
                }
            }

            private static CngKeyCreationParameters KeyParameters(CngKeyCreationOptions options)
            {
                var parameters = new CngKeyCreationParameters { KeyUsage = CngKeyUsages.AllUsages, KeyCreationOptions = options };
                parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
                return parameters;
            }

            public X509Certificate2 Certificate { get; }

            public void Dispose()
            {
                Certificate.Dispose();
                _key.Delete();
            }
        }

        /// <summary>Accepts one connection and offers the certificate; the client ends the handshake by rejecting it.</summary>
        private static async Task ServeOneHandshakeAsync(TcpListener listener, X509Certificate2 certificate)
        {
            try
            {
                using var connection = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var tls = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(
                    certificate, clientCertificateRequired: false, SslProtocols.Tls12, checkCertificateRevocation: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is AuthenticationException || ex is SocketException || ex is ObjectDisposedException)
            {
                // Expected: the client closes the connection once it has rejected the certificate.
            }
        }
    }
}
