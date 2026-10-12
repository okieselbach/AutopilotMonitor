#nullable enable
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace AutopilotMonitor.Agent.V2.Core.Transport
{
    /// <summary>
    /// Remembers the issuer of the server certificate of the last TLS connection a backend
    /// <see cref="HttpClientHandler"/> validated. Evidence for a 401/403 that did not come from the
    /// backend: a TLS-inspecting proxy presents a certificate from its own CA, which names the
    /// product; a Microsoft issuer means the device certificate got lost on the device itself.
    /// <para>
    /// Observation only: the callback returns exactly <c>sslPolicyErrors == None</c>, the verdict
    /// .NET Framework applies to a client connection without a callback, so validation is never
    /// weakened. The callback runs during the handshake of a new connection; a reused keep-alive
    /// connection is not validated again.
    /// </para>
    /// </summary>
    public sealed class ServerCertificateObserver
    {
        /// <summary>Longest issuer kept. The CN leads the distinguished name and survives the cut.</summary>
        internal const int MaxIssuerLength = 128;

        private string? _lastIssuer;

        /// <summary>Issuer of the last validated server certificate, null before the first handshake.</summary>
        public string? LastIssuer => Volatile.Read(ref _lastIssuer);

        /// <summary>Installs the observing validation callback on <paramref name="handler"/>.</summary>
        public void Attach(HttpClientHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            handler.ServerCertificateCustomValidationCallback =
                (_, certificate, _, sslPolicyErrors) => Validate(certificate, sslPolicyErrors);
        }

        /// <summary>Records the issuer and returns the unchanged default verdict.</summary>
        internal bool Validate(X509Certificate2? certificate, SslPolicyErrors sslPolicyErrors)
        {
            Record(certificate);
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        private void Record(X509Certificate2? certificate)
        {
            if (certificate == null) return;
            try
            {
                var issuer = certificate.Issuer;
                if (string.IsNullOrEmpty(issuer)) return;
                Volatile.Write(ref _lastIssuer, issuer.Length > MaxIssuerLength ? issuer.Substring(0, MaxIssuerLength) : issuer);
            }
            catch
            {
                // Observation must never fail a handshake.
            }
        }
    }
}
