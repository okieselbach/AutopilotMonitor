#nullable enable
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// <see cref="ResponseOriginClassifier"/>: only the echo of the request's correlation id proves
    /// that a 401/403 came from the backend. Fixtures are synthetic copies of the platform and proxy
    /// pages (<see cref="InterceptionFixtures"/>).
    /// </summary>
    public sealed class ResponseOriginClassifierTests
    {
        private const string SentId = "0123456789abcdef0123456789abcdef";

        private static HttpResponseMessage Response(HttpStatusCode status, string? body, string mediaType, string? echo = null)
        {
            var response = new HttpResponseMessage(status);
            if (body != null) response.Content = new StringContent(body, Encoding.UTF8, mediaType);
            if (echo != null) response.Headers.TryAddWithoutValidation(ResponseOriginClassifier.CorrelationIdHeader, echo);
            return response;
        }

        private static ResponseClassification Classify(HttpResponseMessage response, string? issuer = null)
        {
            var body = ResponseOriginClassifier.ReadBodyPrefixAsync(response).GetAwaiter().GetResult();
            return ResponseOriginClassifier.Classify(response, SentId, body, issuer);
        }

        [Fact]
        public void Platform_certificate_required_page_is_foreign_client_certificate_required()
        {
            using var response = Response(HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage, "text/html");

            var c = Classify(response, InterceptionFixtures.InspectionIssuer);

            Assert.Equal(ResponseOrigin.Foreign, c.Origin);
            Assert.True(c.IsForeign);
            Assert.Equal(ResponseClassification.ClientCertificateRequired, c.ForeignReason);
            Assert.Equal(InterceptionFixtures.PlatformPageTitle, c.PageTitle);
            Assert.True(c.IsHtml);
            Assert.Equal(403, c.StatusCode);
            Assert.Equal(InterceptionFixtures.InspectionIssuer, c.ServerCertIssuer);
        }

        [Fact]
        public void Platform_stopped_app_page_is_platform_stopped()
        {
            using var response = Response(HttpStatusCode.Forbidden, InterceptionFixtures.StoppedAppPage, "text/html");

            var c = Classify(response);

            Assert.Equal(ResponseOrigin.PlatformStopped, c.Origin);
            Assert.False(c.IsForeign);
            Assert.Null(c.ForeignReason);
            Assert.Equal(InterceptionFixtures.PlatformPageTitle, c.PageTitle);
        }

        [Fact]
        public void Proxy_page_is_foreign_response_with_its_title()
        {
            using var response = Response(HttpStatusCode.Forbidden, InterceptionFixtures.ProxyPage, "text/html");

            var c = Classify(response);

            Assert.Equal(ResponseOrigin.Foreign, c.Origin);
            Assert.Equal(ResponseClassification.ForeignResponse, c.ForeignReason);
            Assert.Equal(InterceptionFixtures.ProxyPageTitle, c.PageTitle);
        }

        [Fact]
        public void Json_403_with_echoed_correlation_id_is_backend()
        {
            using var response = Response(HttpStatusCode.Forbidden, "{\"error\":\"Device validation failed\"}", "application/json", echo: SentId);

            Assert.Equal(ResponseOrigin.Backend, Classify(response).Origin);
        }

        [Fact]
        public void Json_403_without_echo_is_foreign_response()
        {
            using var response = Response(HttpStatusCode.Forbidden, "{\"error\":\"Device validation failed\"}", "application/json");

            var c = Classify(response);

            Assert.Equal(ResponseOrigin.Foreign, c.Origin);
            Assert.Equal(ResponseClassification.ForeignResponse, c.ForeignReason);
            Assert.False(c.IsHtml);
            Assert.Null(c.PageTitle);
        }

        [Fact]
        public void Html_page_with_echo_is_backend_because_the_header_wins()
        {
            using var response = Response(HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage, "text/html", echo: SentId);

            Assert.Equal(ResponseOrigin.Backend, Classify(response).Origin);
        }

        [Fact]
        public void Echo_of_another_request_id_is_not_proof()
        {
            using var response = Response(HttpStatusCode.Unauthorized, "{}", "application/json", echo: "ffffffffffffffffffffffffffffffff");

            Assert.Equal(ResponseOrigin.Foreign, Classify(response).Origin);
        }

        [Fact]
        public void No_sent_id_can_never_be_echoed()
        {
            using var response = Response(HttpStatusCode.Forbidden, "{}", "application/json", echo: SentId);

            Assert.Equal(ResponseOrigin.Foreign, ResponseOriginClassifier.Classify(response, sentCorrelationId: null, bodyPrefix: "{}").Origin);
        }

        [Fact]
        public void Markers_match_case_insensitively_and_in_plain_text()
        {
            using var stopped = Response(HttpStatusCode.Forbidden, "error 403 - this web app is stopped.", "text/plain");
            using var certificate = Response(HttpStatusCode.Forbidden, "Forbidden: CLIENT CERTIFICATE REQUIRED", "text/plain");

            Assert.Equal(ResponseOrigin.PlatformStopped, Classify(stopped).Origin);
            var c = Classify(certificate);
            Assert.Equal(ResponseClassification.ClientCertificateRequired, c.ForeignReason);
            Assert.False(c.IsHtml);
        }

        [Fact]
        public void Html_served_with_generic_content_type_is_sniffed()
        {
            using var response = Response(HttpStatusCode.Forbidden, "  <html><head><title>Blocked</title></head></html>", "text/plain");

            var c = Classify(response);

            Assert.True(c.IsHtml);
            Assert.Equal("Blocked", c.PageTitle);
        }

        [Fact]
        public void Page_title_is_capped_at_80_characters()
        {
            var longTitle = new string('x', 120);
            using var response = Response(HttpStatusCode.Forbidden, $"<html><head><title>{longTitle}</title></head></html>", "text/html");

            Assert.Equal(new string('x', 80), Classify(response).PageTitle);
        }

        [Fact]
        public async Task Body_prefix_is_capped_and_a_marker_beyond_it_is_not_seen()
        {
            var body = new string(' ', ResponseOriginClassifier.MaxBodyChars) + "Client Certificate Required";
            using var response = Response(HttpStatusCode.Forbidden, body, "text/plain");

            var prefix = await ResponseOriginClassifier.ReadBodyPrefixAsync(response);

            Assert.Equal(ResponseOriginClassifier.MaxBodyChars, prefix!.Length);
            Assert.Equal(ResponseClassification.ForeignResponse, ResponseOriginClassifier.Classify(response, SentId, prefix).ForeignReason);
        }

        [Fact]
        public async Task Missing_body_reads_as_null_and_classifies_as_foreign_response()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

            Assert.Null(await ResponseOriginClassifier.ReadBodyPrefixAsync(response));
            Assert.Equal(ResponseClassification.ForeignResponse, Classify(response).ForeignReason);
        }
    }
}
