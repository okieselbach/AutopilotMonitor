#nullable enable
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// <see cref="ServerCertificateObserver"/> must never weaken TLS validation: the installed
    /// callback returns exactly the verdict .NET Framework applies without one
    /// (<c>sslPolicyErrors == None</c>) and only records the issuer as evidence.
    /// </summary>
    public sealed class ServerCertificateObserverTests
    {
        internal static X509Certificate2 SelfSigned(string subject)
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            }
        }

        private static Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> AttachedCallback(ServerCertificateObserver observer)
        {
            var handler = new HttpClientHandler();
            observer.Attach(handler);
            return handler.ServerCertificateCustomValidationCallback!;
        }

        [Theory]
        [InlineData(SslPolicyErrors.None, true)]
        [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
        [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
        [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, false)]
        [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch, false)]
        [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateChainErrors, false)]
        [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false)]
        [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false)]
        public void Callback_accepts_only_a_certificate_without_policy_errors(SslPolicyErrors errors, bool expected)
        {
            using var certificate = SelfSigned("CN=Example Inspection CA");
            var observer = new ServerCertificateObserver();
            var callback = AttachedCallback(observer);

            Assert.Equal(expected, callback(null!, certificate, null!, errors));
            // Recorded whatever the verdict: a rejected inspection certificate is evidence too.
            Assert.Equal(certificate.Issuer, observer.LastIssuer);
        }

        [Fact]
        public void Last_validated_connection_wins()
        {
            using var first = SelfSigned("CN=Example Backend CA");
            using var second = SelfSigned("CN=Example Inspection CA");
            var observer = new ServerCertificateObserver();
            var callback = AttachedCallback(observer);

            Assert.Null(observer.LastIssuer);
            callback(null!, first, null!, SslPolicyErrors.None);
            callback(null!, second, null!, SslPolicyErrors.None);

            Assert.Equal(second.Issuer, observer.LastIssuer);
        }

        [Fact]
        public void Missing_certificate_is_rejected_and_keeps_the_last_issuer()
        {
            using var certificate = SelfSigned("CN=Example Backend CA");
            var observer = new ServerCertificateObserver();
            var callback = AttachedCallback(observer);
            callback(null!, certificate, null!, SslPolicyErrors.None);

            Assert.False(callback(null!, null!, null!, SslPolicyErrors.RemoteCertificateNotAvailable));
            Assert.Equal(certificate.Issuer, observer.LastIssuer);
        }

        [Fact]
        public void Long_issuer_is_capped()
        {
            using var certificate = SelfSigned($"CN=Example Inspection CA, O={new string('o', 200)}");
            var observer = new ServerCertificateObserver();

            observer.Validate(certificate, null, SslPolicyErrors.None);

            Assert.Equal(ServerCertificateObserver.MaxIssuerLength, observer.LastIssuer!.Length);
            Assert.StartsWith(observer.LastIssuer, certificate.Issuer, StringComparison.Ordinal);
        }

        // ── the rejected certificate: who issued it and why it was refused ──

        [Fact]
        public void A_rejected_certificate_is_kept_with_its_issuer_and_chain_status()
        {
            using var certificate = SelfSigned("CN=Example Inspection CA");
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // A real chain: self-signed and in no store, so Windows reports an untrusted root.
            Assert.False(chain.Build(certificate));
            var observer = new ServerCertificateObserver();

            Assert.False(observer.Validate(certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors));

            var rejection = observer.LastRejection!;
            Assert.Equal(certificate.Issuer, rejection.Issuer);
            Assert.Equal(SslPolicyErrors.RemoteCertificateChainErrors, rejection.PolicyErrors);
            Assert.True(rejection.ChainStatus.HasFlag(X509ChainStatusFlags.UntrustedRoot));
            Assert.False(rejection.IssuedByMicrosoft);
        }

        [Fact]
        public void A_passing_validation_clears_the_rejection()
        {
            using var certificate = SelfSigned("CN=Example Inspection CA");
            var observer = new ServerCertificateObserver();
            observer.Validate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);
            Assert.NotNull(observer.LastRejection);

            observer.Validate(certificate, null, SslPolicyErrors.None);

            Assert.Null(observer.LastRejection);
        }

        [Fact]
        public void A_missing_certificate_is_a_rejection_without_an_issuer()
        {
            using var earlier = SelfSigned("CN=Example Backend CA");
            var observer = new ServerCertificateObserver();
            observer.Validate(earlier, null, SslPolicyErrors.None);

            Assert.False(observer.Validate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable));

            // The issuer of an earlier connection is not this handshake's evidence.
            var rejection = observer.LastRejection!;
            Assert.Null(rejection.Issuer);
            Assert.Equal(
                "TLS trust failure: backend.example presented no server certificate (RemoteCertificateNotAvailable).",
                rejection.Describe("backend.example"));
        }

        [Fact]
        public void Only_a_trust_failure_gets_the_rejection()
        {
            using var certificate = SelfSigned("CN=Example Inspection CA");
            var observer = new ServerCertificateObserver();
            observer.Validate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

            Assert.Same(observer.LastRejection, observer.RejectionBehind(InterceptionFixtures.TrustFailure()));
            // A rejection seen earlier never explains a failure of another kind.
            Assert.Null(observer.RejectionBehind(InterceptionFixtures.NameResolutionFailure()));
            Assert.Null(observer.RejectionBehind(null));
        }

        [Fact]
        public void A_trust_failure_without_a_seen_certificate_has_no_rejection()
        {
            Assert.Null(new ServerCertificateObserver().RejectionBehind(InterceptionFixtures.TrustFailure()));
        }

        [Fact]
        public void A_foreign_issuer_is_described_as_something_on_the_network_path()
        {
            var rejection = new ServerCertificateRejection(
                InterceptionFixtures.InspectionIssuer, SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.UntrustedRoot);

            Assert.Equal(
                "TLS trust failure: backend.example presented a server certificate issued by " +
                "\"CN=Example Inspection CA, O=Example Security Ltd, C=US\" (RemoteCertificateChainErrors; UntrustedRoot), not by Microsoft. " +
                "Something on the network path terminates TLS (TLS inspection, captive portal). " +
                "Exclude backend.example from TLS inspection.",
                rejection.Describe("backend.example"));
        }

        [Fact]
        public void Without_chain_status_only_the_policy_errors_are_named()
        {
            var rejection = new ServerCertificateRejection(
                InterceptionFixtures.InspectionIssuer, SslPolicyErrors.RemoteCertificateNameMismatch, X509ChainStatusFlags.NoError);

            Assert.Contains("(RemoteCertificateNameMismatch), not by Microsoft.", rejection.Describe("backend.example"));
        }

        [Theory]
        [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.NotTimeValid,
            "(\"CN=Example Issuing CA, O=Microsoft Corporation, C=US\"; RemoteCertificateChainErrors; NotTimeValid). Check the device clock and its root certificates.")]
        [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, X509ChainStatusFlags.NoError,
            "(\"CN=Example Issuing CA, O=Microsoft Corporation, C=US\"; RemoteCertificateNameMismatch). The certificate does not name this host: DNS or the network path leads somewhere else.")]
        public void A_microsoft_issuer_points_at_the_device_or_the_route(
            SslPolicyErrors errors, X509ChainStatusFlags chainStatus, string expectedEnd)
        {
            var rejection = new ServerCertificateRejection("CN=Example Issuing CA, O=Microsoft Corporation, C=US", errors, chainStatus);

            var text = rejection.Describe("backend.example");

            Assert.True(rejection.IssuedByMicrosoft);
            Assert.Equal(
                "TLS trust failure: this device rejected the server certificate of backend.example, issued by Microsoft " + expectedEnd,
                text);
            Assert.DoesNotContain("TLS inspection", text);
        }
    }
}
