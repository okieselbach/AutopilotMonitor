using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="LatestVersionsService"/> — the short-TTL reader for the
/// public <c>version.json</c> blob. Verifies cache hit/miss, forceRefresh bypass,
/// and failure fallback without hammering the blob on every call.
/// </summary>
public class LatestVersionsServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Func<int, HttpResponseMessage> Responder { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"version\":\"1.0.706\",\"sha256\":\"abc\",\"bootstrapVersion\":\"1.1\"}")
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Responder(CallCount));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;
        public StubHttpClientFactory(StubHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (LatestVersionsService svc, StubHandler handler, IMemoryCache cache) Create()
    {
        var handler = new StubHandler();
        var factory = new StubHttpClientFactory(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new LatestVersionsService(factory, cache, NullLogger<LatestVersionsService>.Instance);
        return (svc, handler, cache);
    }

    [Fact]
    public async Task GetAsync_FirstCall_FetchesFromBlob()
    {
        var (svc, handler, _) = Create();

        var result = await svc.GetAsync(forceRefresh: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1.0.706", result!.AgentVersion);
        Assert.Equal("1.1", result.BootstrapVersion);
        Assert.Equal("abc", result.AgentSha256);
        Assert.False(result.FromCache);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_SecondCall_ReturnsCacheWithoutBlobCall()
    {
        var (svc, handler, _) = Create();

        await svc.GetAsync(forceRefresh: false, CancellationToken.None);
        var second = await svc.GetAsync(forceRefresh: false, CancellationToken.None);

        Assert.NotNull(second);
        Assert.True(second!.FromCache);
        Assert.Equal(1, handler.CallCount); // NOT incremented
    }

    [Fact]
    public async Task GetAsync_ForceRefresh_BypassesCache()
    {
        var (svc, handler, _) = Create();

        await svc.GetAsync(forceRefresh: false, CancellationToken.None);
        await svc.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_ForceRefresh_UpdatesCacheWithFreshValue()
    {
        var (svc, handler, _) = Create();

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"version\":\"1.0.706\",\"bootstrapVersion\":\"1.1\"}")
        };
        await svc.GetAsync(forceRefresh: false, CancellationToken.None);

        // Switch to new payload and force refresh
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"version\":\"1.0.800\",\"bootstrapVersion\":\"1.2\"}")
        };
        var refreshed = await svc.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.Equal("1.0.800", refreshed!.AgentVersion);
        Assert.Equal("1.2", refreshed.BootstrapVersion);

        // Subsequent non-force call should read the updated cache
        var cached = await svc.GetAsync(forceRefresh: false, CancellationToken.None);
        Assert.Equal("1.0.800", cached!.AgentVersion);
        Assert.True(cached.FromCache);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_HttpFailure_ReturnsNullWithoutThrow()
    {
        var (svc, handler, _) = Create();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await svc.GetAsync(forceRefresh: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_HttpFailure_IsShortCached_AvoidsHammering()
    {
        var (svc, handler, _) = Create();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        await svc.GetAsync(forceRefresh: false, CancellationToken.None);
        await svc.GetAsync(forceRefresh: false, CancellationToken.None);
        await svc.GetAsync(forceRefresh: false, CancellationToken.None);

        // Short-TTL null marker prevents repeat blob calls
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_HttpFailure_AfterSuccess_ServesLastKnownGood()
    {
        var (svc, handler, _) = Create();

        await svc.GetAsync(forceRefresh: false, CancellationToken.None);
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        // The refresh fails, but the version line must not blank out: the last good
        // value stands until the blob answers again.
        var afterFailure = await svc.GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.NotNull(afterFailure);
        Assert.Equal("1.0.706", afterFailure!.AgentVersion);
        Assert.True(afterFailure.FromCache);

        // ...and it is what the short failure cache serves to the next callers, too.
        var next = await svc.GetAsync(forceRefresh: false, CancellationToken.None);
        Assert.Equal("1.0.706", next!.AgentVersion);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_MalformedJson_ReturnsNull()
    {
        var (svc, handler, _) = Create();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json")
        };

        var result = await svc.GetAsync(forceRefresh: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_PartialJson_MissingBootstrapVersion_StillReturnsAgentVersion()
    {
        var (svc, handler, _) = Create();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"version\":\"1.0.706\"}")
        };

        var result = await svc.GetAsync(forceRefresh: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1.0.706", result!.AgentVersion);
        Assert.Null(result.BootstrapVersion);
    }
}
