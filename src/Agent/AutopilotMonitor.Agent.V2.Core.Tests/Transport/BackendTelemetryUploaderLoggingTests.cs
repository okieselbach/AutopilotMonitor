using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using Xunit;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// <see cref="BackendTelemetryUploader"/> emits upload-cadence log lines (flushing / ingest
    /// OK / ingest TRANSIENT / ingest PERMANENT) so the transport path is observable. The send
    /// ("flushing") is mechanics and lives on Trace, the success record ("ingest OK") on Verbose;
    /// failures (TRANSIENT/PERMANENT/TIMEOUT/NETWORK) live on Warning/Error and remain
    /// visible at the default Info level. We assert on log-file content because AgentLogger
    /// writes to disk; checking the file is the most faithful production proxy.
    /// </summary>
    public sealed class BackendTelemetryUploaderLoggingTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc);

        private static TelemetryItem NewEventItem(long id) =>
            new TelemetryItem(
                kind: TelemetryItemKind.Event,
                partitionKey: "T1_S1",
                rowKey: id.ToString("D10"),
                telemetryItemId: id,
                sessionTraceOrdinal: id,
                payloadJson: $"{{\"EventType\":\"test\",\"Sequence\":{id}}}",
                requiresImmediateFlush: false,
                enqueuedAtUtc: At);

        private static ProxyObserver NewProxyObserver(AgentLogger logger) =>
            new ProxyObserver(
                () => new ProxySettings(false, null, null, null, null),
                () => "direct",
                logger,
                new VirtualClock(At),
                TimeSpan.FromSeconds(30));

        private static string ReadLogs(string path)
        {
            // AgentLogger writes to a dated file; scan the whole dir to stay robust to format.
            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.EnumerateFiles(path, "*.log", SearchOption.AllDirectories))
            {
                try
                {
                    // Opened with ReadWrite share so we don't collide with an in-flight logger.
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    sb.AppendLine(reader.ReadToEnd());
                }
                catch { /* best-effort */ }
            }
            return sb.ToString();
        }

        [Fact]
        public async Task UploadBatch_logs_flush_start_at_trace_and_ingest_OK_at_verbose()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Trace);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.OK);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1), NewEventItem(2) }, CancellationToken.None);
            Assert.True(result.Success);

            // AgentLogger writes each line with File.AppendAllText — content is already on disk.
            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("[TRACE] BackendTelemetryUploader: flushing 2 item(s)", logContent);
            // Each upload carries a per-request correlation id (X-Correlation-ID) for end-to-end
            // tracing; the success record carries the batch's item range so it stands alone.
            Assert.Contains("[VERBOSE] BackendTelemetryUploader: ingest OK (corr=", logContent);
            Assert.Contains("items=2, firstId=1, lastId=2, bytes=", logContent);
        }

        [Fact]
        public async Task UploadBatch_success_at_verbose_logs_the_record_without_the_send_line()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Verbose);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.OK);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.True(result.Success);

            var logContent = ReadLogs(tmp.Path);
            Assert.DoesNotContain("BackendTelemetryUploader: flushing", logContent);
            Assert.Contains("BackendTelemetryUploader: ingest OK (corr=", logContent);
        }

        [Fact]
        public async Task UploadBatch_success_is_silent_at_default_info_level()
        {
            // Guard rail for the cleanup: at the production-default Info level the transport
            // success path must not emit "flushing" or "ingest OK" any more — only failures do.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.OK);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.True(result.Success);

            var logContent = ReadLogs(tmp.Path);
            Assert.DoesNotContain("BackendTelemetryUploader: flushing", logContent);
            Assert.DoesNotContain("BackendTelemetryUploader: ingest OK", logContent);
        }

        [Fact]
        public async Task UploadBatch_logs_TRANSIENT_on_5xx()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.InternalServerError);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.True(result.IsTransient);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("BackendTelemetryUploader: ingest TRANSIENT", logContent);
        }

        [Fact]
        public async Task UploadBatch_logs_PERMANENT_on_4xx()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.BadRequest);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.False(result.IsTransient);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("BackendTelemetryUploader: ingest PERMANENT", logContent);
        }

        [Fact]
        public async Task UploadBatch_logs_the_whole_interception_finding_at_warning()
        {
            // The old PERMANENT line carried the first 200 body characters, which ended before the
            // platform's "Client Certificate Required" text. The INTERCEPTED line names the finding.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueForeign(HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.True(result.IsIntercepted);

            Assert.True(handler.Captured[0].TryGetHeader("X-Correlation-ID", out var corr));
            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("BackendTelemetryUploader: ingest INTERCEPTED", logContent);
            Assert.Contains($"corr={corr}", logContent);
            Assert.Contains("status=403", logContent);
            Assert.Contains("reason=client_certificate_required", logContent);
            Assert.Contains($"pageTitle=\"{InterceptionFixtures.PlatformPageTitle}\"", logContent);
            Assert.Contains("serverCertIssuer=\"(unknown)\"", logContent);
            Assert.DoesNotContain("ingest PERMANENT", logContent);
        }

        [Fact]
        public async Task UploadBatch_empty_batch_no_log_no_network()
        {
            // Short-circuit empty batch must not burn a log line or an HTTP call.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            using var http = new HttpClient(handler);

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(Array.Empty<TelemetryItem>(), CancellationToken.None);
            Assert.True(result.Success);
            Assert.Empty(handler.Captured);

            var logContent = ReadLogs(tmp.Path);
            Assert.DoesNotContain("flushing", logContent);
            Assert.DoesNotContain("ingest", logContent);
        }

        [Fact]
        public async Task Network_failure_names_the_route_and_opens_a_series()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueThrow(new HttpRequestException("An error occurred while sending the request."));
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http, baseUrl: "https://backend.test", tenantId: "T1", logger: logger, proxy: NewProxyObserver(logger));

            await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("ingest NETWORK fail after", logContent);
            Assert.Contains("items=1, route=direct): ", logContent);
            Assert.Contains("ProxyObserver: uploads are failing since", logContent);
        }

        [Fact]
        public async Task Timeout_names_the_route_and_opens_a_series()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueThrow(new TaskCanceledException("A task was canceled."));
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http, baseUrl: "https://backend.test", tenantId: "T1", logger: logger, proxy: NewProxyObserver(logger));

            await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("ingest TIMEOUT after", logContent);
            Assert.Contains("items=1, route=direct): ", logContent);
            Assert.Contains("ProxyObserver: uploads are failing since", logContent);
        }

        [Fact]
        public async Task Interception_names_the_route_and_opens_a_series()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueForeign(HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http, baseUrl: "https://backend.test", tenantId: "T1", logger: logger, proxy: NewProxyObserver(logger));

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.True(result.IsIntercepted);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains(", route=direct).", logContent);
            Assert.Contains("ProxyObserver: uploads are failing since", logContent);
        }

        [Fact]
        public async Task A_successful_upload_ends_the_series()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueThrow(new HttpRequestException("An error occurred while sending the request."));
            handler.QueueStatus(HttpStatusCode.OK);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http, baseUrl: "https://backend.test", tenantId: "T1", logger: logger, proxy: NewProxyObserver(logger));

            await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.Contains("ProxyObserver: uploads work again after 1 failed attempt(s)", ReadLogs(tmp.Path));
        }

        [Fact]
        public async Task An_answer_from_the_backend_that_is_not_a_success_leaves_the_series_alone()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.InternalServerError);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http, baseUrl: "https://backend.test", tenantId: "T1", logger: logger, proxy: NewProxyObserver(logger));

            await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.DoesNotContain("ProxyObserver: uploads", ReadLogs(tmp.Path));
        }

        [Fact]
        public async Task Without_an_observer_the_failure_line_stays_as_it_was()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueThrow(new HttpRequestException("An error occurred while sending the request."));
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http, baseUrl: "https://backend.test", tenantId: "T1", logger: logger);

            await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("items=1): ", logContent);
            Assert.DoesNotContain("route=", logContent);
        }
    }
}
