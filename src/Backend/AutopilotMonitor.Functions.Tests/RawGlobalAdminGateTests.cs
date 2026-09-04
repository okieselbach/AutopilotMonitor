using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Raw;
using AutopilotMonitor.Functions.Helpers;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Defense-in-depth re-check inside the raw table/log proxies and the access probe. The catalog +
/// policy middleware are the gate; this pins that the in-body check fails CLOSED when the middleware
/// did not run (empty <see cref="RequestContext"/>) or resolved a non-GA caller, and stays silent for
/// a resolved Global Admin.
/// </summary>
public class RawGlobalAdminGateTests
{
    private static (HttpRequestData Req, FunctionContext Ctx) BuildRequest(RequestContext? requestContext)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer = new JsonObjectSerializer(ApiJsonOptions.Create()));
        var provider = services.BuildServiceProvider();

        var items = new Dictionary<object, object>();
        if (requestContext != null)
            items[RequestContext.ItemsKey] = requestContext;

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items);
        context.SetupGet(c => c.InstanceServices).Returns(provider);

        var req = new Mock<HttpRequestData>(context.Object);
        req.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context.Object));
        return (req.Object, context.Object);
    }

    [Fact]
    public async Task Middleware_never_ran_empty_context_is_403()
    {
        var (req, ctx) = BuildRequest(requestContext: null);

        var denied = await RawGlobalAdminGate.DenyUnlessGlobalAdminAsync(req, ctx);

        Assert.NotNull(denied);
        Assert.Equal(HttpStatusCode.Forbidden, denied!.StatusCode);
    }

    [Fact]
    public async Task Resolved_non_global_admin_is_403()
    {
        var (req, ctx) = BuildRequest(new RequestContext { IsGlobalAdmin = false, IsGlobalReader = true });

        var denied = await RawGlobalAdminGate.DenyUnlessGlobalAdminAsync(req, ctx);

        Assert.NotNull(denied);
        Assert.Equal(HttpStatusCode.Forbidden, denied!.StatusCode);
    }

    [Fact]
    public async Task Resolved_global_admin_passes()
    {
        var (req, ctx) = BuildRequest(new RequestContext { IsGlobalAdmin = true });

        Assert.Null(await RawGlobalAdminGate.DenyUnlessGlobalAdminAsync(req, ctx));
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
