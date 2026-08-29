#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// 401/403 classification in <see cref="BackendApiClient"/>: a backend JSON 403 is a real
    /// device-authorization verdict, while an HTML body means the response came from the Azure
    /// platform / an edge proxy (stopped or retired app) and must NOT be reported as
    /// "device is not authorized". Field case sits-d.cloud 2026-08-09: a pre-cutover agent hit
    /// the stopped legacy Function App and support chased a non-existent authorization problem.
    /// </summary>
    public sealed class BackendApiClientAuthClassificationTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responder(request));
        }

        private static BackendApiClient BuildClient(HttpStatusCode status, string body, string mediaType)
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
            return new BackendApiClient(
                new HttpClient(handler),
                baseUrl: "https://backend.example",
                manufacturer: "Contoso",
                model: "TestBox",
                serialNumber: "SN123",
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "2.0.0",
                logger: null);
        }

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
                HttpStatusCode.Forbidden,
                "<!DOCTYPE html><html><head><title>Web App - Unavailable</title></head><body>stopped</body></html>",
                "text/html");

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.Equal(403, ex.StatusCode);
            Assert.True(ex.EndpointUnavailable);
            Assert.Contains("Web App - Unavailable", ex.Message);
            Assert.Contains("NOT a device-authorization failure", ex.Message);
            Assert.DoesNotContain("Check client certificate", ex.Message);
        }

        [Fact]
        public async Task Html_body_with_generic_content_type_is_still_sniffed_as_platform_page()
        {
            using var client = BuildClient(
                HttpStatusCode.Forbidden,
                "  <html><body>Error 403 - This web app is stopped.</body></html>",
                "text/plain");

            var ex = await Assert.ThrowsAsync<BackendAuthException>(() => client.GetAgentConfigAsync("tenant-1"));

            Assert.True(ex.EndpointUnavailable);
            Assert.Contains("HTML platform error page", ex.Message);
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
