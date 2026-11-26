#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;

namespace AutopilotMonitor.Agent.V2.Core.Transport
{
    /// <summary>
    /// Remembers what the server certificate of the last TLS connection a backend
    /// <see cref="HttpClientHandler"/> validated looked like: its issuer, and when the handshake
    /// was rejected, why. Evidence for a failure that did not come from the backend: a
    /// TLS-inspecting proxy presents a certificate from its own CA, which names the product; a
    /// Microsoft issuer means the fault is on the device itself (a lost device certificate behind
    /// a 401/403, a wrong clock or a missing root behind a rejected handshake).
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
        private ServerCertificateRejection? _lastRejection;

        /// <summary>Issuer of the last validated server certificate, null before the first handshake.</summary>
        public string? LastIssuer => Volatile.Read(ref _lastIssuer);

        /// <summary>
        /// The certificate the last handshake rejected. Null before the first handshake and after
        /// one that passed, so a later failure of another kind never carries old evidence.
        /// </summary>
        public ServerCertificateRejection? LastRejection => Volatile.Read(ref _lastRejection);

        /// <summary>Installs the observing validation callback on <paramref name="handler"/>.</summary>
        public void Attach(HttpClientHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            handler.ServerCertificateCustomValidationCallback =
                (_, certificate, chain, sslPolicyErrors) => Validate(certificate, chain, sslPolicyErrors);
        }

        /// <summary>Records the issuer and a rejection, and returns the unchanged default verdict.</summary>
        internal bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        {
            var accepted = sslPolicyErrors == SslPolicyErrors.None;
            try
            {
                var issuer = IssuerOf(certificate);
                if (issuer != null) Volatile.Write(ref _lastIssuer, issuer);
                Volatile.Write(ref _lastRejection, accepted
                    ? null
                    : new ServerCertificateRejection(issuer, sslPolicyErrors, ChainStatusOf(chain)));
            }
            catch
            {
                // Observation must never fail a handshake.
            }
            return accepted;
        }

        /// <summary>
        /// The rejected certificate behind a failed send: non-null only when <paramref name="failure"/>
        /// is a <see cref="WebExceptionStatus.TrustFailure"/> and the handshake's certificate is known.
        /// </summary>
        public ServerCertificateRejection? RejectionBehind(Exception? failure) =>
            TransportErrorCodes.HasWebStatus(failure, WebExceptionStatus.TrustFailure) ? LastRejection : null;

        /// <summary>
        /// The backend's own certificate comes from a Microsoft CA, so any other issuer means
        /// something on the network path terminates TLS. Evidence, never a trust decision.
        /// </summary>
        internal static bool IsMicrosoftIssuer(string? issuer) =>
            issuer != null && issuer.IndexOf("O=Microsoft Corporation", StringComparison.Ordinal) >= 0;

        private static string? IssuerOf(X509Certificate2? certificate)
        {
            var issuer = certificate?.Issuer;
            if (string.IsNullOrEmpty(issuer)) return null;
            return issuer!.Length > MaxIssuerLength ? issuer.Substring(0, MaxIssuerLength) : issuer;
        }

        private static X509ChainStatusFlags ChainStatusOf(X509Chain? chain)
        {
            var flags = X509ChainStatusFlags.NoError;
            if (chain == null) return flags;
            foreach (var status in chain.ChainStatus)
                flags |= status.Status;
            return flags;
        }
    }

    /// <summary>
    /// What a rejected TLS handshake showed: who issued the server certificate and why Windows
    /// refused it.
    /// </summary>
    public sealed class ServerCertificateRejection
    {
        public ServerCertificateRejection(string? issuer, SslPolicyErrors policyErrors, X509ChainStatusFlags chainStatus)
        {
            Issuer = issuer;
            PolicyErrors = policyErrors;
            ChainStatus = chainStatus;
        }

        /// <summary>Issuer distinguished name; null when the server presented no certificate.</summary>
        public string? Issuer { get; }

        public SslPolicyErrors PolicyErrors { get; }

        /// <summary>The status of every chain element combined, e.g. <c>UntrustedRoot</c> or <c>NotTimeValid</c>.</summary>
        public X509ChainStatusFlags ChainStatus { get; }

        /// <summary>False for an inspection CA, a captive portal or any other certificate that is not the backend's.</summary>
        public bool IssuedByMicrosoft => ServerCertificateObserver.IsMicrosoftIssuer(Issuer);

        /// <summary>
        /// One line: who issued the certificate, why it was rejected, what to do. The cause leads
        /// because <c>RegistrationFailureSummary.LastError</c> keeps only 256 characters.
        /// </summary>
        public string Describe(string host)
        {
            var reason = ChainStatus == X509ChainStatusFlags.NoError
                ? PolicyErrors.ToString()
                : $"{PolicyErrors}; {ChainStatus}";

            if (Issuer == null)
                return $"TLS trust failure: {host} presented no server certificate ({reason}).";

            if (!IssuedByMicrosoft)
            {
                return $"TLS trust failure: {host} presented a server certificate issued by \"{Issuer}\" ({reason}), not by Microsoft. " +
                       "Something on the network path terminates TLS (TLS inspection, captive portal). " +
                       $"Exclude {host} from TLS inspection.";
            }

            var hint = (PolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0
                ? "The certificate does not name this host: DNS or the network path leads somewhere else."
                : "Check the device clock and its root certificates.";
            return $"TLS trust failure: this device rejected the server certificate of {host}, issued by Microsoft (\"{Issuer}\"; {reason}). {hint}";
        }
    }
}
