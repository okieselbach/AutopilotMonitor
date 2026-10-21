using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>Who the TLS probe talks to and with which identity headers.</summary>
    internal sealed class TlsProbeTarget
    {
        public string BaseUrl { get; set; }
        public string TenantId { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string AgentVersion { get; set; }
    }

    /// <summary>
    /// Diagnostic after a terminal <c>SecureChannelFailure</c>: one direct TLS connection to the
    /// backend on a raw <see cref="SslStream"/> with the client certificate (system-default
    /// protocols, default certificate selection, normal server validation) and one
    /// <c>GET /api/agent/config</c> with the agent's own headers — the platform asks for the
    /// certificate only once it has seen a path that needs one. The HTTP stack reports only
    /// <c>SecureChannelFailure</c>; this says which protocol was negotiated, whether the certificate
    /// went out and who answered, or the SSPI code of the failure. Failure path only, never a proxy.
    /// <para>
    /// Result token: <c>unreachable</c>, <c>hs(srvcert)</c> / <c>hs(0x…)</c> / <c>hs</c> (handshake
    /// failed), or <c>&lt;protocol&gt;,&lt;m|-&gt;,&lt;answer&gt;</c> — protocol <c>tls12</c> /
    /// <c>tls13</c> / …, <c>m</c> when the session ended mutually authenticated, answer
    /// <c>b&lt;status&gt;</c> (the backend answered: the certificate arrived), <c>cc&lt;status&gt;</c>
    /// (the platform's "Client Certificate Required" page: it did not), <c>st&lt;status&gt;</c>
    /// (stopped app), <c>x&lt;status&gt;</c> (someone else), <c>io(0x…)</c> / <c>io</c> (the
    /// exchange failed, e.g. during renegotiation). A server certificate not issued by Microsoft
    /// (TLS inspection on the path) appends <c>,i=&lt;issuer CN&gt;</c>.
    /// </para>
    /// </summary>
    internal static class TlsHandshakeProbe
    {
        internal const string Unreachable = "unreachable";
        internal const int MaxResponseBytes = 16 * 1024;
        internal const int MaxIssuerLength = 32;

        /// <summary>Runs the probe within <paramref name="timeout"/> per network step. Never throws.</summary>
        public static string Run(TlsProbeTarget target, X509Certificate2 clientCertificate, TimeSpan timeout, AgentLogger logger = null)
        {
            Uri baseUri;
            try { baseUri = new Uri(target.BaseUrl); }
            catch { return Unreachable; }
            var host = baseUri.Host;
            var port = baseUri.IsDefaultPort ? 443 : baseUri.Port;
            var timeoutMs = (int)timeout.TotalMilliseconds;

            try
            {
                using (var tcp = new TcpClient())
                {
                    var connect = tcp.ConnectAsync(host, port);
                    // A connect that fails after the timeout must not surface as an unobserved
                    // task exception: the agent writes crash artefacts for those.
                    connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    try
                    {
                        if (!connect.Wait(timeout))
                            return Unreachable;
                    }
                    catch
                    {
                        return Unreachable;
                    }

                    var serverCertificateRejected = false;
                    string foreignIssuer = null;
                    using (var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                        userCertificateValidationCallback: (sender, certificate, chain, errors) =>
                        {
                            foreignIssuer = ForeignIssuer(certificate);
                            serverCertificateRejected = errors != SslPolicyErrors.None;
                            return !serverCertificateRejected;
                        }))
                    {
                        ssl.ReadTimeout = timeoutMs;
                        ssl.WriteTimeout = timeoutMs;
                        var certificates = new X509CertificateCollection();
                        if (clientCertificate != null) certificates.Add(clientCertificate);

                        try
                        {
                            ssl.AuthenticateAsClient(host, certificates, SslProtocols.None, checkCertificateRevocation: false);
                        }
                        catch (Exception ex)
                        {
                            logger?.Debug($"TlsHandshakeProbe: handshake failed: {ex.GetBaseException().Message}");
                            return (serverCertificateRejected ? "hs(srvcert)" : "hs" + CodeSuffix(ex)) + IssuerSuffix(foreignIssuer);
                        }

                        var protocol = ProtocolToken(ssl.SslProtocol);
                        string answer;
                        try
                        {
                            var correlationId = Guid.NewGuid().ToString("N");
                            var request = Encoding.ASCII.GetBytes(BuildRequest(target, host, correlationId));
                            ssl.Write(request, 0, request.Length);
                            ssl.Flush();
                            answer = ClassifyAnswer(ReadResponse(ssl), correlationId);
                        }
                        catch (Exception ex)
                        {
                            logger?.Debug($"TlsHandshakeProbe: exchange failed: {ex.GetBaseException().Message}");
                            answer = "io" + CodeSuffix(ex);
                        }

                        bool mutual;
                        try { mutual = ssl.IsMutuallyAuthenticated; }
                        catch { mutual = false; }
                        return $"{protocol},{(mutual ? "m" : "-")},{answer}{IssuerSuffix(foreignIssuer)}";
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"TlsHandshakeProbe: failed: {ex.Message}");
                return Unreachable;
            }
        }

        internal static string BuildRequest(TlsProbeTarget target, string host, string correlationId)
        {
            var sb = new StringBuilder();
            sb.Append("GET ").Append(Constants.ApiEndpoints.GetAgentConfig)
              .Append("?tenantId=").Append(Uri.EscapeDataString(target.TenantId ?? string.Empty)).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append("\r\n");
            sb.Append("User-Agent: AutopilotMonitor.Agent/").Append(HeaderValue(target.AgentVersion)).Append(" tls-probe\r\n");
            sb.Append(ResponseOriginClassifier.CorrelationIdHeader).Append(": ").Append(correlationId).Append("\r\n");
            AppendHeader(sb, "X-Device-Manufacturer", target.Manufacturer);
            AppendHeader(sb, "X-Device-Model", target.Model);
            AppendHeader(sb, "X-Device-SerialNumber", target.SerialNumber);
            AppendHeader(sb, "X-Agent-Version", target.AgentVersion);
            sb.Append("Connection: close\r\n\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// Reads until the server closes or <see cref="MaxResponseBytes"/> arrived; a read that
        /// times out after the answer began keeps what came (a server that ignores
        /// <c>Connection: close</c>).
        /// </summary>
        private static byte[] ReadResponse(Stream stream)
        {
            var buffer = new byte[4096];
            using (var collected = new MemoryStream())
            {
                while (collected.Length < MaxResponseBytes)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (IOException) when (collected.Length > 0)
                    {
                        break;
                    }
                    if (read <= 0) break;
                    collected.Write(buffer, 0, read);
                }
                return collected.ToArray();
            }
        }

        /// <summary>
        /// The answer token for a raw HTTP response, through the shared <see cref="ResponseOriginClassifier"/>:
        /// an echo of <paramref name="correlationId"/> means the backend wrote it.
        /// </summary>
        internal static string ClassifyAnswer(byte[] raw, string correlationId)
        {
            if (!TryParse(raw, out var status, out var headers, out var body))
                return "io(noresponse)";

            using (var response = new HttpResponseMessage((HttpStatusCode)status))
            {
                foreach (var header in headers)
                    response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = new StringContent(body ?? string.Empty);

                var classification = ResponseOriginClassifier.Classify(response, correlationId, body);
                switch (classification.Origin)
                {
                    case ResponseOrigin.Backend:
                        return "b" + status;
                    case ResponseOrigin.PlatformStopped:
                        return "st" + status;
                    default:
                        return classification.ForeignReason == ResponseClassification.ClientCertificateRequired
                            ? "cc" + status
                            : "x" + status;
                }
            }
        }

        internal static bool TryParse(byte[] raw, out int status, out List<KeyValuePair<string, string>> headers, out string body)
        {
            status = 0;
            headers = new List<KeyValuePair<string, string>>();
            body = null;
            if (raw == null || raw.Length == 0) return false;

            var text = Encoding.UTF8.GetString(raw);
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = headEnd >= 0 ? text.Substring(0, headEnd) : text;
            body = headEnd >= 0 ? text.Substring(headEnd + 4) : string.Empty;

            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var statusParts = lines[0].Split(' ');
            if (statusParts.Length < 2 || !statusParts[0].StartsWith("HTTP/", StringComparison.Ordinal)
                || !int.TryParse(statusParts[1], out status))
                return false;

            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                    headers.Add(new KeyValuePair<string, string>(lines[i].Substring(0, colon).Trim(), lines[i].Substring(colon + 1).Trim()));
            }
            return true;
        }

        internal static string ProtocolToken(SslProtocols protocol)
        {
            switch ((int)protocol)
            {
                case 12288: return "tls13";   // SslProtocols.Tls13
                case 3072: return "tls12";
                case 768: return "tls11";
                case 192: return "tls10";
                default: return "p" + ((int)protocol).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// The issuer CN of a server certificate that Microsoft did not issue (the backend's comes
        /// from a Microsoft CA), else null: <c>[A-Za-z0-9.-]</c>, others as <c>_</c>, at most
        /// <see cref="MaxIssuerLength"/>.
        /// </summary>
        internal static string ForeignIssuer(X509Certificate certificate)
        {
            if (certificate == null) return null;
            try
            {
                if (certificate.Issuer.IndexOf("O=Microsoft Corporation", StringComparison.Ordinal) >= 0)
                    return null;
                string cn;
                using (var cert2 = new X509Certificate2(certificate))
                    cn = cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
                if (string.IsNullOrEmpty(cn)) return "unknown";
                var sb = new StringBuilder(Math.Min(cn.Length, MaxIssuerLength));
                foreach (var c in cn)
                {
                    if (sb.Length == MaxIssuerLength) break;
                    sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' ? c : '_');
                }
                return sb.ToString();
            }
            catch
            {
                return "unknown";
            }
        }

        private static string IssuerSuffix(string foreignIssuer) =>
            foreignIssuer == null ? string.Empty : ",i=" + foreignIssuer;

        private static string CodeSuffix(Exception ex)
        {
            var code = TransportErrorCodes.InnermostNativeCode(ex);
            return code == null ? string.Empty : "(" + code + ")";
        }

        private static void AppendHeader(StringBuilder sb, string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
                sb.Append(name).Append(": ").Append(HeaderValue(value)).Append("\r\n");
        }

        /// <summary>ASCII only, no line breaks — a header value must never split the request.</summary>
        private static string HeaderValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
                sb.Append(c >= 0x20 && c < 0x7F ? c : '?');
            return sb.ToString();
        }
    }
}
