using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Push;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Backup;
using Azure.Core.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The one body reader (D-207): camelCase and PascalCase both bind, string enums bind, an empty,
/// malformed or aborted body is a 400 envelope (never a 500), the optional variant treats "no body" as
/// null, the byte cap and the Newtonsoft branch behave the same way. The cap counts the bytes actually
/// read, so neither a chunked nor a gzip body gets past it; the pipeline tests run the production
/// registration on real Kestrel, through the real push handler, and pin the worker's body limit.
/// </summary>
public class RequestBodyTests
{
    private const string CorrelationId = "corr-123";

    [Fact]
    public async Task CamelCase_body_binds()
    {
        var req = Build("{\"name\":\"Group A\"}");
        var read = await req.ReadAsync<CreateTenantGroupRequest>();
        Assert.Null(read.Error);
        Assert.Equal("Group A", read.Value!.Name);
    }

    [Fact]
    public async Task PascalCase_body_binds_too()
    {
        // Superset of the two former paths: the worker serializer was case-sensitive, Newtonsoft was not.
        var req = Build("{\"Name\":\"Group A\"}");
        var read = await req.ReadAsync<CreateTenantGroupRequest>();
        Assert.Null(read.Error);
        Assert.Equal("Group A", read.Value!.Name);
    }

    [Fact]
    public async Task String_enum_binds()
    {
        var req = Build("{\"tableName\":\"t\",\"partitionKey\":\"p\",\"rowKey\":\"r\",\"mode\":\"Commit\"}");
        var read = await req.ReadAsync<RestoreRowRequest>();
        Assert.Null(read.Error);
        Assert.Equal(RestoreRowMode.Commit, read.Value!.Mode);
    }

    [Fact]
    public async Task Absent_nullable_key_stays_null_and_number_binds()
    {
        var req = Build("{\"tenantId\":\"t\",\"serialNumber\":\"s\",\"durationHours\":24}");
        var read = await req.ReadAsync<BlockDeviceRequest>();
        Assert.Null(read.Error);
        Assert.Equal(24, read.Value!.DurationHours);
        Assert.Null(read.Value.Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_body_is_400_required(string body)
    {
        var req = Build(body);
        var read = await req.ReadAsync<CreateTenantGroupRequest>();
        await AssertBadRequest(read.Error, "Request body is required");
        Assert.Null(read.Value);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"durationHours\":\"twelve\",\"tenantId\":\"t\",\"serialNumber\":\"s\"}")] // string in an int slot
    public async Task Malformed_body_is_400_invalid(string body)
    {
        var req = Build(body);
        var read = await req.ReadAsync<BlockDeviceRequest>();
        await AssertBadRequest(read.Error, "Invalid JSON body");
    }

    [Fact]
    public async Task Aborted_body_is_400_incomplete_not_500()
    {
        // The client closed the tab mid-body: before this, the handler's catch turned it into a 500.
        var req = Build(new ThrowingBodyStream(new BadHttpRequestException("Unexpected end of request content.")));
        var read = await req.ReadAsync<SignalRJoinGroupRequest>();
        await AssertBadRequest(read.Error, "Request body incomplete");
        Assert.Null(read.Value);
    }

    [Fact]
    public async Task Json_null_body_is_400_required()
    {
        var req = Build("null");
        var read = await req.ReadAsync<CreateTenantGroupRequest>();
        await AssertBadRequest(read.Error, "Request body is required");
    }

    [Fact]
    public async Task Optional_reader_returns_empty_for_no_body_but_400_for_garbage()
    {
        var empty = await Build("").ReadOptionalAsync<DownloadTicketRequest>();
        Assert.Null(empty.Error);
        Assert.Null(empty.Value);

        var present = await Build("{\"blobName\":\"b\"}").ReadOptionalAsync<DownloadTicketRequest>();
        Assert.Null(present.Error);
        Assert.Equal("b", present.Value!.BlobName);

        var garbage = await Build("{oops").ReadOptionalAsync<DownloadTicketRequest>();
        await AssertBadRequest(garbage.Error, "Invalid JSON body");
    }

    [Fact]
    public async Task Cap_is_strictly_greater_than()
    {
        var atCap = await Build(new MemoryStream(PaddedJson("{\"code\":\"x\"", 8192))).ReadAsync<BeginPairRequest>(maxBytes: 8192);
        Assert.Null(atCap.Error);
        Assert.Equal("x", atCap.Value!.Code);

        var pastCap = await Build(new MemoryStream(PaddedJson("{\"code\":\"x\"", 8193))).ReadAsync<BeginPairRequest>(maxBytes: 8192);
        await AssertBadRequest(pastCap.Error, "Request body too large");
    }

    [Fact]
    public async Task A_site_without_a_cap_reads_under_the_default()
    {
        var atDefault = await Build(new MemoryStream(PaddedJson("{\"name\":\"x\"", (int)RequestBody.DefaultMaxBytes)))
            .ReadAsync<CreateTenantGroupRequest>();
        Assert.Null(atDefault.Error);

        var pastDefault = await Build(new MemoryStream(PaddedJson("{\"name\":\"x\"", (int)RequestBody.DefaultMaxBytes + 1)))
            .ReadAsync<CreateTenantGroupRequest>();
        await AssertBadRequest(pastDefault.Error, "Request body too large");
    }

    [Fact]
    public async Task Byte_order_mark_is_honoured_as_before()
    {
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"name\":\"Group A\"}")).ToArray();
        var read = await Build(new MemoryStream(withBom)).ReadAsync<CreateTenantGroupRequest>();
        Assert.Null(read.Error);
        Assert.Equal("Group A", read.Value!.Name);
    }

    [Fact]
    public async Task Capped_buffer_is_null_past_the_cap_and_rewound_within_it()
    {
        Assert.Null(await Build(new MemoryStream(new byte[101])).ReadCappedAsync(100));

        using var within = await Build("{\"a\":1}").ReadCappedAsync(100);
        Assert.NotNull(within);
        Assert.Equal(0, within!.Position);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(within.ToArray()));
    }

    [Fact]
    public async Task Chunked_body_is_refused_one_byte_past_the_cap()
    {
        // Chunked transfer carries no Content-Length: the cap must hold on the bytes actually read.
        var body = new CountingStream(new MemoryStream(PaddedJson("{\"code\":\"x\"", 64 * 1024)));
        var read = await Build(body).ReadAsync<BeginPairRequest>(maxBytes: 8192);
        await AssertBadRequest(read.Error, "Request body too large");
        Assert.True(body.BytesRead <= 8192 + 1, $"read {body.BytesRead} bytes against an 8 KiB cap");
    }

    [Fact]
    public async Task Gzip_body_is_capped_on_its_inflated_bytes()
    {
        // UseRequestDecompression hands the handler the inflating stream, while Content-Length still names
        // the compressed size, far below the cap.
        var compressed = Gzip(PaddedJson("{\"code\":\"x\"", 1024 * 1024));
        Assert.True(compressed.Length < 8192);
        var inflated = new CountingStream(new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress));
        var read = await Build(inflated, contentLength: compressed.Length).ReadAsync<BeginPairRequest>(maxBytes: 8192);
        await AssertBadRequest(read.Error, "Request body too large");
        Assert.True(inflated.BytesRead <= 8192 + 1, $"inflated {inflated.BytesRead} bytes against an 8 KiB cap");
    }

    [Fact]
    public async Task Worker_limit_refusal_is_too_large_not_incomplete()
    {
        // Kestrel and UseRequestDecompression answer the worker's own limit with a 413 BadHttpRequestException.
        var read = await Build(new ThrowingBodyStream(new BadHttpRequestException("Request body too large.", 413)))
            .ReadAsync<BeginPairRequest>(maxBytes: 8192);
        await AssertBadRequest(read.Error, "Request body too large");
    }

    [Fact]
    public async Task Newtonsoft_branch_has_the_same_contract()
    {
        var ok = await Build("{\"Fields\":{\"a\":1},\"reason\":\"r\"}").ReadNewtonsoftAsync<PatchTenantConfigurationFieldsRequest>();
        Assert.Null(ok.Error);
        Assert.Single(ok.Value!.Fields!);
        Assert.Equal("r", ok.Value.Reason);

        var empty = await Build("").ReadNewtonsoftAsync<PatchTenantConfigurationFieldsRequest>();
        await AssertBadRequest(empty.Error, "Request body is required");

        var bad = await Build("{oops").ReadNewtonsoftAsync<PatchTenantConfigurationFieldsRequest>();
        await AssertBadRequest(bad.Error, "Invalid JSON body");
    }

    [Fact]
    public async Task Document_reader_requires_an_object_root()
    {
        var doc = await Build("{\"planTier\":\"pro\",\"trialExpiresUtc\":null}").ReadDocumentAsync();
        Assert.Null(doc.Error);
        var document = doc.Value!;
        using (document)
        {
            Assert.True(document.RootElement.TryGetProperty("trialExpiresUtc", out var trial));
            Assert.Equal(JsonValueKind.Null, trial.ValueKind); // presence survives — the reason this reader exists
            Assert.False(document.RootElement.TryGetProperty("maxDelegatedTenants", out _));
        }

        var array = await Build("[1,2]").ReadDocumentAsync();
        await AssertBadRequest(array.Error, "Body must be a JSON object");
    }

    // ── the production pipeline: real Kestrel with AddWorkerHttpPipeline ───────

    [Fact]
    public async Task Pipeline_push_route_refuses_a_gzip_bomb_before_inflating_it()
    {
        var compressed = Gzip(PaddedJson("{\"code\":\"x\"", 4 * 1024 * 1024));
        var content = new ByteArrayContent(compressed);
        content.Headers.ContentEncoding.Add("gzip");

        var (status, error, bytesRead) = await PostThroughPipelineAsync(content, PushBeginAsync);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Request body too large", error);
        Assert.True(bytesRead <= 8192 + 1, $"inflated {bytesRead} bytes against the push route's 8 KiB cap");
    }

    [Fact]
    public async Task Pipeline_push_route_refuses_a_chunked_body_at_the_cap()
    {
        var content = new StreamContent(new MemoryStream(PaddedJson("{\"code\":\"x\"", 64 * 1024)));

        var (status, error, bytesRead) = await PostThroughPipelineAsync(content, PushBeginAsync, chunked: true);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Request body too large", error);
        Assert.True(bytesRead <= 8192 + 1, $"read {bytesRead} bytes against the push route's 8 KiB cap");
    }

    [Fact]
    public async Task Pipeline_worker_limit_bounds_what_a_gzip_body_inflates_to()
    {
        // A read without an effective cap stands in for any read that escapes one: Kestrel's default
        // (30,000,000 bytes) would inflate and accept this body; the worker limit refuses it.
        var compressed = Gzip(PaddedJson("{\"code\":\"x\"", (int)RequestBody.WorkerMaxBytes + 1024 * 1024));
        var content = new ByteArrayContent(compressed);
        content.Headers.ContentEncoding.Add("gzip");

        var (status, error, bytesRead) = await PostThroughPipelineAsync(content, ReadUncappedAsync);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Request body too large", error);
        Assert.True(bytesRead <= RequestBody.WorkerMaxBytes, $"inflated {bytesRead} bytes past the worker limit");
    }

    [Fact]
    public async Task Pipeline_kestrel_refuses_a_declared_length_past_the_worker_limit_unread()
    {
        var content = new ByteArrayContent(new byte[RequestBody.WorkerMaxBytes + 1]);

        var (status, error, bytesRead) = await PostThroughPipelineAsync(content, ReadUncappedAsync, expectContinue: true);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Request body too large", error);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public async Task Pipeline_still_inflates_an_honest_gzip_body()
    {
        var content = new ByteArrayContent(Gzip(Encoding.UTF8.GetBytes("{\"code\":\"x\"}")));
        content.Headers.ContentEncoding.Add("gzip");

        var (status, error, _) = await PostThroughPipelineAsync(content, ReadPairCodeAsync);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(error);
    }

    [Fact]
    public void Worker_body_limit_survives_the_functions_host_composition()
    {
        // Program.cs order, plus two would-be overrides: a Kestrel:Limits app setting and a later Configure.
        var builder = FunctionsApplication.CreateBuilder(Array.Empty<string>());
        builder.Configuration["Kestrel:Limits:MaxRequestBodySize"] = "104857600";
        builder.ConfigureFunctionsWebApplication();
        builder.Services.AddWorkerHttpPipeline();
        builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = null);

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal(RequestBody.WorkerMaxBytes, provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        Assert.Contains(provider.GetServices<IStartupFilter>(), filter => filter is HttpCompressionStartupFilter);
    }

    /// <summary>The real push handler; the pairing service is never reached when the read fails first.</summary>
    private static Task<HttpResponseData> PushBeginAsync(HttpRequestData req)
        => new PushReceiverFunctions(null!,
                new RateLimitService(new MemoryCache(new MemoryCacheOptions()), NullLogger<RateLimitService>.Instance),
                NullLogger<PushReceiverFunctions>.Instance)
            .Begin(req);

    private static async Task<HttpResponseData> ReadUncappedAsync(HttpRequestData req)
        => (await req.ReadAsync<BeginPairRequest>(maxBytes: long.MaxValue)).Error ?? req.CreateResponse(HttpStatusCode.OK);

    private static async Task<HttpResponseData> ReadPairCodeAsync(HttpRequestData req)
    {
        var read = await req.ReadAsync<BeginPairRequest>(maxBytes: 8192);
        return read.Error ?? req.CreateResponse(read.Value!.Code == "x" ? HttpStatusCode.OK : HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Posts <paramref name="content"/> through real Kestrel with the production registration
    /// (AddWorkerHttpPipeline: the worker body limit, and UseRequestDecompression through the startup
    /// filter) to <paramref name="handle"/>, which receives the request body as the Functions worker
    /// would hand it over. Returns the status, the error envelope's message, and the body bytes the
    /// handler pulled (for a gzip body: inflated bytes).
    /// </summary>
    private static async Task<(HttpStatusCode Status, string? Error, long BytesRead)> PostThroughPipelineAsync(
        HttpContent content, Func<HttpRequestData, Task<HttpResponseData>> handle, bool chunked = false, bool expectContinue = false)
    {
        CountingStream? body = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddWorkerHttpPipeline();
        await using var app = builder.Build();
        app.Run(async context =>
        {
            body = new CountingStream(context.Request.Body);
            var response = await handle(Build(body, context.Request.ContentLength));
            context.Response.StatusCode = (int)response.StatusCode;
            response.Body.Position = 0;
            await response.Body.CopyToAsync(context.Response.Body);
        });
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/test") { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.ExpectContinue = expectContinue;
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        await app.StopAsync();

        string? error = null;
        if (text.Length > 0)
        {
            using var envelope = JsonDocument.Parse(text);
            if (envelope.RootElement.TryGetProperty("error", out var message)) error = message.GetString();
        }
        return (response.StatusCode, error, body?.BytesRead ?? 0);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────

    private static async Task AssertBadRequest(HttpResponseData? error, string message)
    {
        Assert.NotNull(error);
        Assert.Equal(HttpStatusCode.BadRequest, error!.StatusCode);
        error.Body.Position = 0;
        var envelope = await JsonSerializer.DeserializeAsync<ApiErrorResponse>(error.Body, ApiJsonOptions.Read);
        Assert.NotNull(envelope);
        Assert.Equal(message, envelope!.Error);
        Assert.Equal(AutopilotMonitor.Shared.Constants.ApiErrorCodes.BadRequest, envelope!.Code);
        Assert.Equal(CorrelationId, envelope!.CorrelationId);
    }

    private static HttpRequestData Build(string body, long? contentLength = null)
        => Build(new MemoryStream(Encoding.UTF8.GetBytes(body)), contentLength);

    private static HttpRequestData Build(Stream body, long? contentLength = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer = new JsonObjectSerializer(ApiJsonOptions.Create()));
        var provider = services.BuildServiceProvider();

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(new Dictionary<object, object> { ["CorrelationId"] = CorrelationId });
        context.SetupGet(c => c.InstanceServices).Returns(provider);

        var headers = new HttpHeadersCollection();
        if (contentLength is long len) headers.Add("Content-Length", len.ToString());

        var req = new Mock<HttpRequestData>(context.Object);
        req.SetupGet(r => r.Headers).Returns(headers);
        req.SetupGet(r => r.Body).Returns(body);
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context.Object));
        return req.Object;
    }

    /// <summary>Valid JSON: <paramref name="head"/>, spaces, and the closing brace, <paramref name="length"/> bytes in all.</summary>
    private static byte[] PaddedJson(string head, int length)
        => Encoding.UTF8.GetBytes(head + new string(' ', length - head.Length - 1) + "}");

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            gzip.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// What Kestrel's request body throws: a client that disconnects mid-body (400), or the worker's
    /// own body limit, raised by Kestrel or UseRequestDecompression (413).
    /// </summary>
    private sealed class ThrowingBodyStream : MemoryStream
    {
        private readonly BadHttpRequestException _error;

        public ThrowingBodyStream(BadHttpRequestException error) => _error = error;

        public override int Read(byte[] buffer, int offset, int count) => throw _error;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw _error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw _error;
    }

    /// <summary>Counts the bytes the reader pulled from the body (for a gzip body: the inflated bytes).</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;

        public CountingStream(Stream inner) => _inner = inner;

        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await _inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            BytesRead += read;
            return read;
        }
    }

    private sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext context) : base(context) { }
        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
    }
}
