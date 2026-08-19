using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// Unit-tests for the <see cref="NetworkMetricsRecordingHandler"/> — the piece that wires
    /// the V2 telemetry-upload path into <c>NetworkMetrics</c> so <c>net_total_requests</c>
    /// in <c>agent_metrics_snapshot</c> reflects every outbound HTTP call (not just the
    /// legacy BackendApiClient ones).
    /// </summary>
    public sealed class NetworkMetricsRecordingHandlerTests
    {
        private static HttpClient BuildClient(NetworkMetrics metrics, RecordingHttpMessageHandler inner)
        {
            var pipeline = new NetworkMetricsRecordingHandler(metrics, inner);
            return new HttpClient(pipeline);
        }

        private static HttpRequestMessage NewPost(string body)
        {
            return new HttpRequestMessage(HttpMethod.Post, "https://backend.test/api/agent/telemetry")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        [Fact]
        public async Task Records_one_request_on_2xx()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK, body: "{}");
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost("hello"));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(0, snap.FailureCount);
        }

        [Fact]
        public async Task Records_failure_on_5xx()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.InternalServerError);
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost("x"));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
        }

        [Fact]
        public async Task Records_failure_on_4xx()
        {
            // Legacy BackendApiClient surfaces 4xx as exceptions via EnsureSuccessStatusCode
            // which is then counted as failed in its finally block. The handler runs upstream
            // of EnsureSuccessStatusCode, so it sees the response directly — non-2xx is failed.
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.Unauthorized);
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost("x"));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
        }

        [Fact]
        public async Task Records_failure_on_inner_exception_and_rethrows()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueThrow(new HttpRequestException("network down"));
            using var client = BuildClient(metrics, inner);

            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(NewPost("x")));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
        }

        [Fact]
        public async Task Captures_bytes_up_from_request_content_length()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK);
            using var client = BuildClient(metrics, inner);

            var body = "0123456789"; // 10 bytes
            using var resp = await client.SendAsync(NewPost(body));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(10, snap.TotalBytesUp);
        }

        [Fact]
        public async Task Captures_bytes_down_from_response_content_length()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK, body: "abcd"); // 4 bytes
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost(""));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(4, snap.TotalBytesDown);
        }

        [Fact]
        public async Task Records_each_request_separately_for_multi_send()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK);
            inner.QueueStatus(HttpStatusCode.InternalServerError);
            inner.QueueStatus(HttpStatusCode.OK);
            using var client = BuildClient(metrics, inner);

            using (var r1 = await client.SendAsync(NewPost("a"))) { }
            using (var r2 = await client.SendAsync(NewPost("bb"))) { }
            using (var r3 = await client.SendAsync(NewPost("ccc"))) { }

            var snap = metrics.GetSnapshot();
            Assert.Equal(3, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
            Assert.Equal(1 + 2 + 3, snap.TotalBytesUp);
        }

        [Fact]
        public async Task Captures_bytes_down_via_buffering_when_content_length_is_missing()
        {
            // AutomaticDecompression strips Content-Length on the backend's gzipped JSON —
            // the pre-fix handler recorded 0 for every such (healthy) response. A non-seekable
            // stream body reproduces the headerless shape.
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            var payload = Encoding.UTF8.GetBytes("{\"ok\":true}"); // 11 bytes
            inner.QueueResponse(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableReadStream(payload)),
            });
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost(""));

            var snap = metrics.GetSnapshot();
            Assert.Equal(payload.Length, snap.TotalBytesDown);
            // Buffering must not consume the body away from the caller.
            Assert.Equal("{\"ok\":true}", await resp.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Bytes_down_fails_soft_to_zero_when_buffering_throws()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueResponse(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ThrowingContent(),
            });
            using var client = BuildClient(metrics, inner);

            // ResponseHeadersRead keeps HttpClient's own post-pipeline buffering out of the
            // picture — the test isolates the handler's fallback catch (which must swallow
            // the serialization failure and record 0, never throw).
            using var resp = await client.SendAsync(NewPost(""), HttpCompletionOption.ResponseHeadersRead);

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(0, snap.TotalBytesDown);
        }

        /// <summary>Read-only, non-seekable stream — CanSeek=false keeps StreamContent from
        /// computing a Content-Length, mirroring a decompression-wrapped response body.</summary>
        private sealed class NonSeekableReadStream : System.IO.Stream
        {
            private readonly System.IO.MemoryStream _inner;
            public NonSeekableReadStream(byte[] data) { _inner = new System.IO.MemoryStream(data); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }

        /// <summary>Content whose serialization always fails — drives the fallback's catch path.</summary>
        private sealed class ThrowingContent : HttpContent
        {
            protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
                => throw new System.IO.IOException("connection dropped mid-body");
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
        }

        [Fact]
        public void Constructor_rejects_null_metrics()
        {
            Assert.Throws<ArgumentNullException>(() => new NetworkMetricsRecordingHandler(null!));
            Assert.Throws<ArgumentNullException>(
                () => new NetworkMetricsRecordingHandler(null!, new RecordingHttpMessageHandler()));
        }
    }
}
