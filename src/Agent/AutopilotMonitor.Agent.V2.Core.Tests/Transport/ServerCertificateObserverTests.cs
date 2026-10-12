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

            observer.Validate(certificate, SslPolicyErrors.None);

            Assert.Equal(ServerCertificateObserver.MaxIssuerLength, observer.LastIssuer!.Length);
            Assert.StartsWith(observer.LastIssuer, certificate.Issuer, StringComparison.Ordinal);
        }
    }
}
