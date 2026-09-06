using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Backup;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The one body reader (D-207): camelCase and PascalCase both bind, string enums bind, an empty
/// or malformed body is a 400 envelope (never a 500), the optional variant treats "no body" as
/// null, the byte cap and the Newtonsoft branch behave the same way.
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
    public async Task Byte_cap_applies_only_when_given()
    {
        var capped = await Build("{\"name\":\"x\"}", contentLength: 2_000_000).ReadAsync<CreateTenantGroupRequest>(maxBytes: 1_048_576);
        await AssertBadRequest(capped.Error, "Request body too large");

        var uncapped = await Build("{\"name\":\"x\"}", contentLength: 2_000_000).ReadAsync<CreateTenantGroupRequest>();
        Assert.Null(uncapped.Error);
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
        req.SetupGet(r => r.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context.Object));
        return req.Object;
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
