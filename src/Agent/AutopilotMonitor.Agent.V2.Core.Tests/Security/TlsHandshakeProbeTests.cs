using System.Security.Authentication;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// The TLS probe's request and the reading of its answer: only an echo of the probe's own
    /// correlation id counts as the backend, the platform's "Client Certificate Required" page says
    /// the certificate did not arrive, and a device value can never split the request.
    /// </summary>
    public sealed class TlsHandshakeProbeTests
    {
        private const string SentId = "0123456789abcdef0123456789abcdef";

        private static TlsProbeTarget Target(string serial = "SN-1") => new TlsProbeTarget
        {
            BaseUrl = "https://api.example.test",
            TenantId = "00000000-0000-0000-0000-000000000001",
            Manufacturer = "Contoso",
            Model = "Book 1",
            SerialNumber = serial,
            AgentVersion = "2.0.1500",
        };

        private static byte[] Raw(string statusLine, string headers, string body) =>
            Encoding.UTF8.GetBytes(statusLine + "\r\n" + headers + "\r\n" + body);

        [Fact]
        public void Request_asks_for_the_agent_config_with_the_agent_headers()
        {
            var request = TlsHandshakeProbe.BuildRequest(Target(), "api.example.test", SentId);

            Assert.StartsWith(
                "GET /api/agent/config?tenantId=00000000-0000-0000-0000-000000000001 HTTP/1.1\r\nHost: api.example.test\r\n",
                request);
            Assert.Contains("\r\nX-Correlation-ID: " + SentId + "\r\n", request);
            Assert.Contains("\r\nX-Device-Manufacturer: Contoso\r\n", request);
            Assert.Contains("\r\nX-Device-Model: Book 1\r\n", request);
            Assert.Contains("\r\nX-Device-SerialNumber: SN-1\r\n", request);
            Assert.Contains("\r\nX-Agent-Version: 2.0.1500\r\n", request);
            Assert.EndsWith("\r\nConnection: close\r\n\r\n", request);
        }

        [Fact]
        public void Device_value_with_a_line_break_cannot_add_a_header()
        {
            var request = TlsHandshakeProbe.BuildRequest(Target("SN\r\nX-Injected: 1"), "api.example.test", SentId);

            Assert.DoesNotContain("\r\nX-Injected", request);
            Assert.Contains("\r\nX-Device-SerialNumber: SN??X-Injected: 1\r\n", request);
        }

        [Fact]
        public void Echoed_correlation_id_means_the_backend_answered()
        {
            var raw = Raw("HTTP/1.1 401 Unauthorized", "X-Correlation-ID: " + SentId + "\r\nContent-Type: application/json\r\n", "{}");

            Assert.Equal("b401", TlsHandshakeProbe.ClassifyAnswer(raw, SentId));
        }

        [Fact]
        public void Backend_success_is_read_as_backend()
        {
            var raw = Raw("HTTP/1.1 200 OK", "x-correlation-id: " + SentId + "\r\n", "{\"ok\":true}");

            Assert.Equal("b200", TlsHandshakeProbe.ClassifyAnswer(raw, SentId));
        }

        [Fact]
        public void Platform_certificate_required_page_means_the_certificate_did_not_arrive()
        {
            var raw = Raw("HTTP/1.1 403 Forbidden", "Content-Type: text/html\r\n", InterceptionFixtures.CertificateRequiredPage);

            Assert.Equal("cc403", TlsHandshakeProbe.ClassifyAnswer(raw, SentId));
        }

        [Fact]
        public void Certificate_required_page_with_an_echo_is_still_the_backend()
        {
            var raw = Raw("HTTP/1.1 403 Forbidden", "X-Correlation-ID: " + SentId + "\r\n", InterceptionFixtures.CertificateRequiredPage);

            Assert.Equal("b403", TlsHandshakeProbe.ClassifyAnswer(raw, SentId));
        }

        [Fact]
        public void Echo_of_another_id_is_someone_else()
        {
            var raw = Raw("HTTP/1.1 403 Forbidden", "X-Correlation-ID: ffffffffffffffffffffffffffffffff\r\n", "<html>Access denied</html>");

            Assert.Equal("x403", TlsHandshakeProbe.ClassifyAnswer(raw, SentId));
        }

        [Fact]
        public void Stopped_app_page_is_reported_as_stopped()
        {
            var raw = Raw("HTTP/1.1 403 Forbidden", "Content-Type: text/html\r\n", InterceptionFixtures.StoppedAppPage);

            Assert.Equal("st403", TlsHandshakeProbe.ClassifyAnswer(raw, SentId));
        }

        [Theory]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("HTTP/1.1 abc\r\n\r\n")]
        public void No_parsable_answer_is_reported_as_no_response(string text)
        {
            Assert.Equal("io(noresponse)", TlsHandshakeProbe.ClassifyAnswer(Encoding.ASCII.GetBytes(text), SentId));
        }

        [Fact]
        public void Microsoft_issued_server_certificate_adds_nothing()
        {
            using var cert = ServerCertificateObserverTests.SelfSigned(
                "CN=Microsoft Azure RSA TLS Issuing CA 03, O=Microsoft Corporation, C=US");

            Assert.Null(TlsHandshakeProbe.ForeignIssuer(cert));
            Assert.Null(TlsHandshakeProbe.ForeignIssuer(null!));
        }

        [Fact]
        public void Inspection_issuer_is_named_in_a_compact_token()
        {
            using var cert = ServerCertificateObserverTests.SelfSigned("CN=Contoso Inspection CA (2026), O=Contoso Ltd");

            Assert.Equal("Contoso_Inspection_CA__2026_", TlsHandshakeProbe.ForeignIssuer(cert));
        }

        [Fact]
        public void Long_issuer_is_capped()
        {
            using var cert = ServerCertificateObserverTests.SelfSigned("CN=" + new string('A', 80) + ", O=Fabrikam");

            Assert.Equal(new string('A', TlsHandshakeProbe.MaxIssuerLength), TlsHandshakeProbe.ForeignIssuer(cert));
        }

        [Theory]
        [InlineData(SslProtocols.Tls12, "tls12")]
        [InlineData((SslProtocols)12288, "tls13")]
        [InlineData(SslProtocols.Tls11, "tls11")]
        [InlineData(SslProtocols.Tls, "tls10")]
        [InlineData(SslProtocols.None, "p0")]
        public void Protocol_token_names_the_negotiated_version(SslProtocols protocol, string expected)
        {
            Assert.Equal(expected, TlsHandshakeProbe.ProtocolToken(protocol));
        }
    }
}
