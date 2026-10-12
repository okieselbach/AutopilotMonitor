#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// 401/403 classification in <see cref="BackendApiClient"/>: only a response that echoes the
    /// request's correlation id is the backend's device-authorization verdict. The platform page of
    /// a stopped app (field case 2026-08-09: a pre-cutover agent hit the stopped legacy Function App
    /// and support chased a non-existent authorization problem) and anything answered on the network
    /// path (TLS inspection: "Client Certificate Required", proxy pages) must NOT be reported as
    /// "device is not authorized".
    /// </summary>
    public sealed class BackendApiClientAuthClassificationTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                var response = _responder(request);
                response.RequestMessage = request;   // as HttpClientHandler does
                return Task.FromResult(response);
            }
        }

        /// <param name="fromBackend">
        /// True: the response echoes the request's X-Correlation-ID like the backend's middleware.
        /// False: a platform or proxy answer without the echo.
        /// </param>
        private static StubHandler Answer(HttpStatusCode status, string body, string mediaType, bool fromBackend) =>
            new StubHandler(request =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
                if (fromBackend && request.Headers.TryGetValues(ResponseOriginClassifier.CorrelationIdHeader, out var ids))
                    response.Headers.TryAddWithoutValidation(ResponseOriginClassifier.CorrelationIdHeader, ids);
                return response;
            });

        private static BackendApiClient BuildClient(HttpMessageHandler handler, ServerCertificateObserver? serverCertificates = null) =>
            new BackendApiClient(
                new HttpClient(handler),
                baseUrl: "https://backend.example",
                manufacturer: "Contoso",
                model: "TestBox",
                serialNumber: "SN123",
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "2.0.0",
                logger: null,
                serverCertificates: serverCertificates);

        private static BackendApiClient BuildClient(HttpStatusCode status, string body, string mediaType, bool fromBackend = true) =>
            BuildClient(Answer(status, body, mediaType, fromBackend));

        [Fact]
        public async Task Backend_json_403_is_reported_as_device_not_authorized()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"message\":\"Device validation failed\"}",
                "application/json");

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Equal(403, ex.StatusCode);
            Assert.False(ex.EndpointUnavailable);
            Assert.Contains("The device is not authorized", ex.Message);
        }

        [Fact]
        public async Task Backend_json_403_carries_the_errorCode_when_present()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"message\":\"Session belongs to another device\",\"errorCode\":\"session_owner_mismatch\"}",
                "application/json");

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Equal(403, ex.StatusCode);
            Assert.False(ex.EndpointUnavailable);
            Assert.Equal("session_owner_mismatch", ex.ErrorCode);
        }

        [Fact]
        public async Task Backend_json_403_without_errorCode_yields_null()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"message\":\"Device validation failed\"}",
                "application/json");

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));
            Assert.Null(ex.ErrorCode);
        }

        // ── coded 409: a refusal, never an auth failure ─────────────────────

        [Fact]
        public async Task Coded_409_is_a_refusal_not_an_auth_failure()
        {
            using var client = BuildClient(
                HttpStatusCode.Conflict,
                "{\"success\":false,\"message\":\"Session id is already registered to another tenant\",\"errorCode\":\"session_owner_mismatch\"}",
                "application/json");

            var ex = await Assert.ThrowsAsync<BackendRefusalException>(
                () => client.RegisterSessionAsync(new AutopilotMonitor.Shared.Models.SessionRegistration()));

            Assert.Equal(409, ex.StatusCode);
            Assert.Equal("session_owner_mismatch", ex.ErrorCode);
        }

        [Fact]
        public async Task Uncoded_409_stays_a_plain_http_error()
        {
            using var client = BuildClient(
                HttpStatusCode.Conflict,
                "{\"success\":false,\"message\":\"A diagnostics package with this name already exists\"}",
                "application/json");

            await Assert.ThrowsAsync<HttpRequestException>(
                () => client.GetDiagnosticsUploadUrlAsync("tenant-1", "session-1", "package.zip"));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("not json", null)]
        [InlineData("<html><body>403</body></html>", null)]
        [InlineData("{\"errorCode\":42}", null)]
        [InlineData("{\"errorCode\":\"  \"}", null)]
        [InlineData("{\"errorCode\":\"session_owner_mismatch\"}", "session_owner_mismatch")]
        [InlineData("{\"ErrorCode\":\"session_owner_mismatch\"}", "session_owner_mismatch")]
        [InlineData("  {\"success\":false,\"errorCode\":\" x \"}", "x")]
        public void TryExtractErrorCode_is_tolerant(string? body, string? expected)
            => Assert.Equal(expected, BackendApiClient.TryExtractErrorCode(body!));

        [Fact]
        public async Task Platform_html_403_is_reported_as_endpoint_unavailable_with_page_title()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden, InterceptionFixtures.StoppedAppPage, "text/html", fromBackend: false);

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Equal(403, ex.StatusCode);
            Assert.True(ex.EndpointUnavailable);
            Assert.False(ex.IsForeignResponse);
            Assert.Equal(ResponseOrigin.PlatformStopped, ex.Origin);
            Assert.Contains("HTML platform error page (\"Web App - Unavailable\")", ex.Message);
            Assert.Contains("NOT a device-authorization failure", ex.Message);
            Assert.Contains("stale ApiBaseUrl", ex.Message);
            Assert.DoesNotContain("Check client certificate", ex.Message);
        }

        [Fact]
        public async Task Html_body_with_generic_content_type_is_still_sniffed_as_platform_page()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden,
                "  <html><body>Error 403 - This web app is stopped.</body></html>",
                "text/plain",
                fromBackend: false);

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.True(ex.EndpointUnavailable);
            Assert.Contains("HTML platform error page", ex.Message);
        }

        // ── answered on the network path (TLS inspection, proxy) ─────────────

        [Fact]
        public async Task Certificate_required_page_says_the_certificate_did_not_reach_the_backend()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage, "text/html", fromBackend: false);

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Equal(403, ex.StatusCode);
            Assert.True(ex.IsForeignResponse);
            Assert.False(ex.EndpointUnavailable);
            Assert.Equal(ResponseClassification.ClientCertificateRequired, ex.Classification.ForeignReason);
            Assert.Null(ex.ErrorCode);
            // The cause leads: RegistrationFailureSummary.LastError keeps 256 characters.
            Assert.StartsWith("The device certificate did not reach the backend (TLS inspection or a proxy on the network path)", ex.Message);
            Assert.Contains("HTML platform error page (\"Web App - Unavailable\")", ex.Message);
            Assert.True(ex.Message.IndexOf("Web App - Unavailable", StringComparison.Ordinal) < 256);
            Assert.Contains("NOT a device-authorization failure", ex.Message);
            Assert.DoesNotContain("stale ApiBaseUrl", ex.Message);
            Assert.DoesNotContain("Check client certificate", ex.Message);
        }

        [Fact]
        public async Task Proxy_page_keeps_the_phrase_and_title_ANALYZE_NET_001_matches()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden, InterceptionFixtures.ProxyPage, "text/html", fromBackend: false);

            var ex = await Assert.ThrowsAsync<BackendAuthException>(
                () => client.GetDiagnosticsUploadUrlAsync("tenant-1", "session-1", "package.zip"));

            Assert.True(ex.IsForeignResponse);
            Assert.Equal(ResponseClassification.ForeignResponse, ex.Classification.ForeignReason);
            Assert.Contains("HTML platform error page (\"Example Gateway - Notification\")", ex.Message);
            Assert.Contains("403", ex.Message);
            Assert.DoesNotContain("stale ApiBaseUrl", ex.Message);
        }

        [Fact]
        public async Task Json_403_without_the_echo_is_foreign_and_its_errorCode_is_ignored()
        {
            // An errorCode in a body the backend did not author must not drive a session rotation.
            using var client = BuildClient(
                HttpStatusCode.Forbidden,
                "{\"success\":false,\"errorCode\":\"session_owner_mismatch\"}",
                "application/json",
                fromBackend: false);

            var ex = await Assert.ThrowsAsync<BackendAuthException>(
                () => client.RegisterSessionAsync(new AutopilotMonitor.Shared.Models.SessionRegistration()));

            Assert.True(ex.IsForeignResponse);
            Assert.Null(ex.ErrorCode);
            Assert.Contains("a response without the backend's correlation echo", ex.Message);
        }

        [Fact]
        public async Task Foreign_answer_carries_the_observed_server_certificate_issuer()
        {
            var observer = new ServerCertificateObserver();
            using (var cert = ServerCertificateObserverTests.SelfSigned("CN=Example Inspection CA"))
            {
                observer.Validate(cert, System.Net.Security.SslPolicyErrors.None);
            }
            using var client = BuildClient(
                Answer(HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage, "text/html", fromBackend: false),
                observer);

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Equal("CN=Example Inspection CA", ex.Classification.ServerCertIssuer);
        }

        // ── agent/error: a report the backend never received is not delivered ──

        [Theory]
        [InlineData(HttpStatusCode.OK, true, true)]
        [InlineData(HttpStatusCode.InternalServerError, true, true)]   // unchanged: only the round-trip counts
        [InlineData(HttpStatusCode.Forbidden, true, true)]             // the backend answered
        [InlineData(HttpStatusCode.Forbidden, false, false)]           // answered on the network path
        [InlineData(HttpStatusCode.Unauthorized, false, false)]
        public async Task ReportAgentError_counts_as_delivered_unless_the_backend_never_answered(
            HttpStatusCode status, bool fromBackend, bool expectedDelivered)
        {
            using var client = BuildClient(status, InterceptionFixtures.CertificateRequiredPage, "text/html", fromBackend);

            var delivered = await client.ReportAgentErrorAsync(new AgentErrorReport { TenantId = "tenant-1", SessionId = "session-1" });

            Assert.Equal(expectedDelivered, delivered);
        }

        [Fact]
        public async Task Intercepted_emergency_report_rolls_its_reservation_back()
        {
            var handler = Answer(HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage, "text/html", fromBackend: false);
            using var client = BuildClient(handler);
            var reporter = new EmergencyReporter(client, "session-1", "tenant-1", "2.0.0", logger: null);

            await reporter.TrySendAsync(AgentErrorType.ConfigFetchFailed, "first");
            await reporter.TrySendAsync(AgentErrorType.ConfigFetchFailed, "second");

            // Not delivered → no dedup, no cooldown: the same key is attempted again.
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task Backend_401_keeps_authorization_message_and_status()
        {
            using var client = BuildClient(
                HttpStatusCode.Unauthorized,
                "{\"success\":false,\"message\":\"certificate rejected\"}",
                "application/json");

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Equal(401, ex.StatusCode);
            Assert.False(ex.EndpointUnavailable);
            Assert.Contains("The device is not authorized", ex.Message);
        }
    }
}
