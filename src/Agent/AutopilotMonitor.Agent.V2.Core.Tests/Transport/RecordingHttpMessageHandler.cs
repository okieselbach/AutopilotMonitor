using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// Test <see cref="HttpMessageHandler"/> that captures every outgoing request (including the
    /// body string, because the default handler disposes content after <c>SendAsync</c> returns)
    /// and returns responses from a scripted queue.
    /// </summary>
    internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script =
            new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        private readonly List<CapturedRequest> _captured = new List<CapturedRequest>();

        public IReadOnlyList<CapturedRequest> Captured => _captured;

        public RecordingHttpMessageHandler QueueStatus(HttpStatusCode status, string? body = null)
        {
            _script.Enqueue(_ =>
            {
                var resp = new HttpResponseMessage(status);
                if (body != null) resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return resp;
            });
            return this;
        }

        public RecordingHttpMessageHandler QueueThrow(Exception ex)
        {
            _script.Enqueue(_ => throw ex);
            return this;
        }

        /// <summary>
        /// Queues an arbitrary response — for shapes <see cref="QueueStatus"/> can't build,
        /// e.g. content without a Content-Length header (what AutomaticDecompression leaves
        /// behind on real compressed responses).
        /// </summary>
        public RecordingHttpMessageHandler QueueResponse(Func<HttpResponseMessage> factory)
        {
            _script.Enqueue(_ => factory());
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Real HttpClient throws TaskCanceledException when the caller's token is already
            // signaled, before touching the network. Mirror that here so cancellation tests
            // don't silently succeed with a default 200.
            cancellationToken.ThrowIfCancellationRequested();

            string? body = null;
            byte[]? rawBody = null;
            if (request.Content != null)
            {
                rawBody = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var isGzipped = System.Linq.Enumerable.Contains(
                    request.Content.Headers.ContentEncoding, "gzip", StringComparer.OrdinalIgnoreCase);
                if (isGzipped && rawBody.Length > 0)
                {
                    using var input = new MemoryStream(rawBody);
                    using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gzip.CopyTo(output);
                    body = Encoding.UTF8.GetString(output.ToArray());
                }
                else
                {
                    body = Encoding.UTF8.GetString(rawBody);
                }
            }

            _captured.Add(new CapturedRequest(request, body, rawBody));

            if (_script.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return _script.Dequeue()(request);
        }

        internal sealed class CapturedRequest
        {
            public CapturedRequest(HttpRequestMessage request, string? body, byte[]? rawBody = null)
            {
                Method = request.Method;
                RequestUri = request.RequestUri;
                Headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in request.Headers)
                {
                    Headers[h.Key] = System.Linq.Enumerable.ToArray(h.Value);
                }
                if (request.Content != null)
                {
                    foreach (var h in request.Content.Headers)
                    {
                        Headers[h.Key] = System.Linq.Enumerable.ToArray(h.Value);
                    }
                }
                Body = body;
                RawBody = rawBody;
            }

            public HttpMethod Method { get; }
            public Uri? RequestUri { get; }
            public Dictionary<string, string[]> Headers { get; }
            public string? Body { get; }
            public byte[]? RawBody { get; }

            public bool TryGetHeader(string name, out string value)
            {
                if (Headers.TryGetValue(name, out var values) && values.Length > 0)
                {
                    value = values[0];
                    return true;
                }
                value = string.Empty;
                return false;
            }
        }
    }
}
