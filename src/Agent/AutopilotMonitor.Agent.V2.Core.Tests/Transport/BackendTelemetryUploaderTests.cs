using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    public sealed class BackendTelemetryUploaderTests
    {
        private const string BaseUrl = "https://backend.test";
        private const string ExpectedEndpoint = "https://backend.test/api/agent/telemetry";
        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static TelemetryItem NewEventItem(long id, string sessionId = "S1", string tenantId = "T1") =>
            new TelemetryItem(
                kind: TelemetryItemKind.Event,
                partitionKey: $"{tenantId}_{sessionId}",
                rowKey: $"20260420100000000_{id:D10}",
                telemetryItemId: id,
                sessionTraceOrdinal: id,
                payloadJson: $"{{\"EventType\":\"test\",\"Sequence\":{id}}}",
                requiresImmediateFlush: false,
                enqueuedAtUtc: At);

        private static TelemetryItem NewSignalItem(long id) =>
            new TelemetryItem(
                kind: TelemetryItemKind.Signal,
                partitionKey: "T1_S1",
                rowKey: id.ToString("D10"),
                telemetryItemId: id,
                sessionTraceOrdinal: id,
                payloadJson: "{\"Kind\":\"SessionStarted\"}",
                requiresImmediateFlush: false,
                enqueuedAtUtc: At);

        private sealed class Rig : IDisposable
        {
            public RecordingHttpMessageHandler Handler { get; } = new RecordingHttpMessageHandler();
            public HttpClient Http { get; }
            public BackendTelemetryUploader Sut { get; }

            public Rig(
                string tenantId = "T1",
                string? manufacturer = "Dell",
                string? model = "Latitude",
                string? serial = "ABC123",
                string? bootstrap = null,
                string? version = "11.0.0")
            {
                Http = new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(30) };
                Sut = new BackendTelemetryUploader(
                    httpClient: Http,
                    baseUrl: BaseUrl,
                    tenantId: tenantId,
                    manufacturer: manufacturer,
                    model: model,
                    serialNumber: serial,
                    bootstrapToken: bootstrap,
                    agentVersion: version);
            }

            public void Dispose()
            {
                Http.Dispose();
                Handler.Dispose();
            }
        }

        // ============================================================ Request shape

        [Fact]
        public async Task UploadBatchAsync_posts_to_api_telemetry_batch_with_expected_headers()
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(HttpStatusCode.OK);

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Single(rig.Handler.Captured);
            var req = rig.Handler.Captured[0];
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal(ExpectedEndpoint, req.RequestUri!.ToString());

            Assert.True(req.TryGetHeader("X-Tenant-Id", out var tid));
            Assert.Equal("T1", tid);
            Assert.True(req.TryGetHeader("X-Device-Manufacturer", out var mf));
            Assert.Equal("Dell", mf);
            Assert.True(req.TryGetHeader("X-Device-Model", out var mo));
            Assert.Equal("Latitude", mo);
            Assert.True(req.TryGetHeader("X-Device-SerialNumber", out var sn));
            Assert.Equal("ABC123", sn);
            Assert.True(req.TryGetHeader("X-Agent-Version", out var ver));
            Assert.Equal("11.0.0", ver);

            Assert.True(req.TryGetHeader("Content-Type", out var ct));
            Assert.StartsWith("application/json", ct);

            Assert.True(req.TryGetHeader("Content-Encoding", out var ce));
            Assert.Equal("gzip", ce);

            Assert.False(req.Headers.ContainsKey("X-Bootstrap-Token"));
        }

        [Fact]
        public async Task UploadBatchAsync_stamps_send_time_header_per_attempt()
        {
            // P14: X-Send-Time-Utc carries the device clock at the SEND moment, round-trip
            // ISO-8601, one value per request. A retry is a new send moment — the header is
            // stamped inside UploadBatchAsync, so each attempt gets a fresh value.
            using var rig = new Rig();
            rig.Handler.QueueStatus(HttpStatusCode.ServiceUnavailable);
            rig.Handler.QueueStatus(HttpStatusCode.OK);

            var before = DateTime.UtcNow;
            await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            var after = DateTime.UtcNow;

            Assert.Equal(2, rig.Handler.Captured.Count);
            foreach (var req in rig.Handler.Captured)
            {
                Assert.True(req.TryGetHeader("X-Send-Time-Utc", out var raw));
                var parsed = DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
                Assert.Equal(DateTimeKind.Utc, parsed.Kind);
                Assert.InRange(parsed, before.AddSeconds(-1), after.AddSeconds(1));
            }
        }

        // ============================================================ Compression (bandwidth)

        [Fact]
        public async Task Body_is_gzip_compressed_on_the_wire()
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(HttpStatusCode.OK);

            // Moderately-sized batch so compression does real work (20 items).
            var batch = new List<TelemetryItem>();
            for (long i = 1; i <= 20; i++) batch.Add(NewEventItem(i));

            await rig.Sut.UploadBatchAsync(batch, CancellationToken.None);

            var req = rig.Handler.Captured[0];

            // Content-Encoding header advertises gzip to the backend.
            Assert.True(req.TryGetHeader("Content-Encoding", out var ce));
            Assert.Equal("gzip", ce);

            // Wire bytes begin with the gzip magic number (1F 8B).
            Assert.NotNull(req.RawBody);
            Assert.True(req.RawBody!.Length >= 2);
            Assert.Equal(0x1F, req.RawBody[0]);
            Assert.Equal(0x8B, req.RawBody[1]);

            // Decompressed payload is still the expected JSON array (RecordingHandler
            // transparently decompresses into Body for assertions).
            var parsed = JArray.Parse(req.Body!);
            Assert.Equal(20, parsed.Count);

            // Compression is actually smaller than plain JSON for a batch of this size.
            var plainSize = System.Text.Encoding.UTF8.GetByteCount(req.Body!);
            Assert.True(
                req.RawBody.Length < plainSize,
                $"gzip body ({req.RawBody.Length} B) should be smaller than plain JSON ({plainSize} B).");
        }

        [Fact]
        public async Task Bootstrap_token_is_attached_only_when_provided()
        {
            using var rig = new Rig(bootstrap: "bs-abc");
            rig.Handler.QueueStatus(HttpStatusCode.OK);

            await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            var req = rig.Handler.Captured[0];
            Assert.True(req.TryGetHeader("X-Bootstrap-Token", out var tok));
            Assert.Equal("bs-abc", tok);
        }

        [Fact]
        public async Task Hardware_headers_are_omitted_when_values_are_null()
        {
            using var rig = new Rig(manufacturer: null, model: null, serial: null);
            rig.Handler.QueueStatus(HttpStatusCode.OK);

            await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            var req = rig.Handler.Captured[0];
            Assert.False(req.Headers.ContainsKey("X-Device-Manufacturer"));
            Assert.False(req.Headers.ContainsKey("X-Device-Model"));
            Assert.False(req.Headers.ContainsKey("X-Device-SerialNumber"));
        }

        [Fact]
        public async Task Body_is_JSON_array_of_TelemetryItems_with_Kind_as_string()
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(HttpStatusCode.OK);

            var batch = new[] { NewEventItem(1), NewSignalItem(2) };
            await rig.Sut.UploadBatchAsync(batch, CancellationToken.None);

            var body = rig.Handler.Captured[0].Body!;
            var parsed = JArray.Parse(body);
            Assert.Equal(2, parsed.Count);
            Assert.Equal("Event", (string?)parsed[0]["Kind"]);
            Assert.Equal(1, (long)parsed[0]["TelemetryItemId"]!);
            Assert.Equal("Signal", (string?)parsed[1]["Kind"]);
            Assert.Equal(2, (long)parsed[1]["TelemetryItemId"]!);
            Assert.Equal("T1_S1", (string?)parsed[0]["PartitionKey"]);
        }

        [Fact]
        public async Task Empty_batch_short_circuits_without_hitting_the_network()
        {
            using var rig = new Rig();
            var result = await rig.Sut.UploadBatchAsync(Array.Empty<TelemetryItem>(), CancellationToken.None);

            Assert.True(result.Success);
            Assert.Empty(rig.Handler.Captured);
        }

        // ============================================================ Status mapping

        [Theory]
        [InlineData(HttpStatusCode.OK)]
        [InlineData(HttpStatusCode.Created)]
        [InlineData(HttpStatusCode.Accepted)]
        [InlineData(HttpStatusCode.NoContent)]
        public async Task Success_2xx_maps_to_Ok(HttpStatusCode status)
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(status);

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Null(result.ErrorReason);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task Auth_failure_401_403_maps_to_Permanent_unauthorized(HttpStatusCode status)
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(status, body: "device not whitelisted");

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.False(result.IsTransient);
            Assert.Contains("unauthorized", result.ErrorReason);
            Assert.Contains(((int)status).ToString(), result.ErrorReason);
        }

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]            // 400
        [InlineData(HttpStatusCode.NotFound)]              // 404
        [InlineData(HttpStatusCode.Conflict)]              // 409
        [InlineData(HttpStatusCode.UnsupportedMediaType)]  // 415
        public async Task Other_4xx_maps_to_Permanent(HttpStatusCode status)
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(status);

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.False(result.IsTransient);
            Assert.Contains(((int)status).ToString(), result.ErrorReason);
        }

        [Theory]
        [InlineData(HttpStatusCode.RequestTimeout)]    // 408
        [InlineData((HttpStatusCode)429)]              // TooManyRequests
        public async Task Rate_limit_and_408_map_to_Transient(HttpStatusCode status)
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(status);

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.IsTransient);
        }

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]   // 500
        [InlineData(HttpStatusCode.BadGateway)]            // 502
        [InlineData(HttpStatusCode.ServiceUnavailable)]    // 503
        [InlineData(HttpStatusCode.GatewayTimeout)]        // 504
        public async Task Server_5xx_maps_to_Transient(HttpStatusCode status)
        {
            using var rig = new Rig();
            rig.Handler.QueueStatus(status);

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.IsTransient);
        }

        // ============================================================ Exception handling

        [Fact]
        public async Task HttpRequestException_network_error_maps_to_Transient()
        {
            using var rig = new Rig();
            rig.Handler.QueueThrow(new HttpRequestException("dns failure"));

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.IsTransient);
            Assert.Contains("network", result.ErrorReason);
        }

        [Fact]
        public async Task HttpClient_timeout_maps_to_Transient()
        {
            // HttpClient surfaces its own internal timeout as TaskCanceledException with
            // CancellationToken.IsCancellationRequested == false.
            using var rig = new Rig();
            rig.Handler.QueueThrow(new TaskCanceledException("The operation was canceled."));

            var result = await rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.IsTransient);
            Assert.Contains("timeout", result.ErrorReason);
        }

        [Fact]
        public async Task Caller_cancellation_propagates_as_OperationCanceledException()
        {
            using var rig = new Rig();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                rig.Sut.UploadBatchAsync(new[] { NewEventItem(1) }, cts.Token));
        }

        // ============================================================ Misuse

        [Fact]
        public void Constructor_rejects_null_or_empty_required_args()
        {
            var http = new HttpClient();
            Assert.Throws<ArgumentNullException>(() =>
                new BackendTelemetryUploader(null!, BaseUrl, "T1"));
            Assert.Throws<ArgumentException>(() =>
                new BackendTelemetryUploader(http, "", "T1"));
            Assert.Throws<ArgumentException>(() =>
                new BackendTelemetryUploader(http, BaseUrl, ""));
        }

        [Fact]
        public async Task UploadBatchAsync_rejects_null_items()
        {
            using var rig = new Rig();
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                rig.Sut.UploadBatchAsync(null!, CancellationToken.None));
        }
    }
}
