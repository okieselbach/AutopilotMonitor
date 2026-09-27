using System.Net;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Minimal HTTP endpoint harness: a mocked <see cref="HttpRequestData"/> whose FunctionContext carries the
/// <see cref="RequestContext"/> the policy middleware would have resolved, serialized with the production
/// wire settings.
/// </summary>
internal static class EndpointHarness
{
    public static (HttpRequestData Req, FunctionContext Context) Request(
        string tenantId, string upn = "admin@contoso.com", string? jsonBody = null, string queryString = "",
        IDictionary<string, string>? headers = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer = new JsonObjectSerializer(ApiJsonOptions.Create()));
        var provider = services.BuildServiceProvider();

        var items = new Dictionary<object, object>
        {
            ["RequestContext"] = new RequestContext
            {
                TenantId = tenantId,
                TargetTenantId = tenantId,
                UserPrincipalName = upn,
                IsTenantAdmin = true,
                UserRole = Constants.TenantRoles.Admin,
            },
        };
        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items);
        context.SetupGet(c => c.InstanceServices).Returns(provider);

        var req = new Mock<HttpRequestData>(context.Object);
        var headerCollection = new HttpHeadersCollection();
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            headerCollection.Add(name, value);
        req.SetupGet(r => r.Headers).Returns(headerCollection);
        // Query parses the Url in the base class (a bare mock would hand back null).
        req.SetupGet(r => r.Url).Returns(new Uri("https://localhost/api/test" + queryString));
        req.SetupGet(r => r.Query).CallBase();
        req.SetupGet(r => r.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(jsonBody ?? "")));
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeResponse(context.Object));
        return (req.Object, context.Object);
    }

    /// <summary>The <c>code</c> of an error envelope; null when the body carries none.</summary>
    public static string? ErrorCode(HttpResponseData response)
    {
        response.Body.Position = 0;
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed class FakeResponse : HttpResponseData
    {
        public FakeResponse(FunctionContext context) : base(context) { }
        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
    }
}
