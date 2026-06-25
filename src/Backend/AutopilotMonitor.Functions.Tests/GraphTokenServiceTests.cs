using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Tests.GraphResolution;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the per-tenant app-token cache on <see cref="GraphTokenService"/>. The security
/// contract: a successful client-credentials token is reused until shortly before expiry, keyed
/// strictly per tenant (no cross-tenant reuse), and failures are never cached. The
/// <see cref="StubHttpMessageHandler"/> records every outbound request so we can assert exactly how
/// many token POSTs hit AAD.
/// </summary>
public class GraphTokenServiceTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private static (GraphTokenService svc, StubHttpMessageHandler handler) Create(StubHttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:ClientId"] = "test-client",
                ["EntraId:ClientSecret"] = "test-secret",
            })
            .Build();

        var svc = new GraphTokenService(
            NullLogger<GraphTokenService>.Instance,
            new StubHttpClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            config);

        return (svc, handler);
    }

    private static int TokenPosts(StubHttpMessageHandler handler, string tenantId)
        => handler.Requests.Count(u => u.Contains($"/{tenantId}/oauth2/v2.0/token"));

    [Fact]
    public async Task GetAccessTokenAsync_caches_token_across_calls()
    {
        var handler = new StubHttpMessageHandler()
            .When($"/{TenantA}/oauth2", HttpStatusCode.OK, "{\"access_token\":\"tok-A\",\"expires_in\":3599}");
        var (svc, _) = Create(handler);

        var first  = await svc.GetAccessTokenAsync(TenantA);
        var second = await svc.GetAccessTokenAsync(TenantA);

        Assert.Equal("tok-A", first.AccessToken);
        Assert.Equal("tok-A", second.AccessToken);
        // Second call is served from cache — only one POST ever reached AAD.
        Assert.Equal(1, TokenPosts(handler, TenantA));
    }

    [Fact]
    public async Task GetAccessTokenAsync_isolates_tokens_per_tenant()
    {
        // Cross-tenant isolation: tenant A's token must never be served for tenant B, and each
        // tenant caches independently.
        var handler = new StubHttpMessageHandler()
            .When($"/{TenantA}/oauth2", HttpStatusCode.OK, "{\"access_token\":\"tok-A\",\"expires_in\":3599}")
            .When($"/{TenantB}/oauth2", HttpStatusCode.OK, "{\"access_token\":\"tok-B\",\"expires_in\":3599}");
        var (svc, _) = Create(handler);

        var a1 = await svc.GetAccessTokenAsync(TenantA);
        var a2 = await svc.GetAccessTokenAsync(TenantA);
        var b1 = await svc.GetAccessTokenAsync(TenantB);

        Assert.Equal("tok-A", a1.AccessToken);
        Assert.Equal("tok-A", a2.AccessToken);
        Assert.Equal("tok-B", b1.AccessToken);
        Assert.Equal(1, TokenPosts(handler, TenantA));
        Assert.Equal(1, TokenPosts(handler, TenantB));
    }

    [Fact]
    public async Task GetAccessTokenAsync_does_not_cache_failure()
    {
        // A non-retryable 400 (not a consent-propagation error) returns immediately and must NOT be
        // cached — the next request re-acquires so a transient outage doesn't pin a failure.
        var handler = new StubHttpMessageHandler()
            .When($"/{TenantA}/oauth2", HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}");
        var (svc, _) = Create(handler);

        var first  = await svc.GetAccessTokenAsync(TenantA);
        var second = await svc.GetAccessTokenAsync(TenantA);

        Assert.Null(first.AccessToken);
        Assert.Null(second.AccessToken);
        Assert.Equal(2, TokenPosts(handler, TenantA));
    }

    [Fact]
    public async Task GetAccessTokenAsync_caches_token_without_expires_in()
    {
        // AAD always returns expires_in, but the fallback lifetime path must still cache so a quirky
        // response doesn't silently disable caching.
        var handler = new StubHttpMessageHandler()
            .When($"/{TenantA}/oauth2", HttpStatusCode.OK, "{\"access_token\":\"tok-A\"}");
        var (svc, _) = Create(handler);

        await svc.GetAccessTokenAsync(TenantA);
        await svc.GetAccessTokenAsync(TenantA);

        Assert.Equal(1, TokenPosts(handler, TenantA));
    }

    [Fact]
    public async Task InvalidateTenant_forces_fresh_token_on_next_call()
    {
        // The fresh-read contract: after an admin grants consent/roles, the cached token still
        // carries the OLD roles claim. InvalidateTenant (called by GraphFeatureDetector.InvalidateTenant
        // on the admin Refresh / access-check path) must drop it so the next acquire mints fresh.
        var handler = new StubHttpMessageHandler()
            .When($"/{TenantA}/oauth2", HttpStatusCode.OK, "{\"access_token\":\"tok-A\",\"expires_in\":3599}");
        var (svc, _) = Create(handler);

        await svc.GetAccessTokenAsync(TenantA);
        svc.InvalidateTenant(TenantA);
        await svc.GetAccessTokenAsync(TenantA);

        Assert.Equal(2, TokenPosts(handler, TenantA));
    }

    [Fact]
    public async Task InvalidateTenant_during_in_flight_acquire_is_not_overwritten_by_stale_token()
    {
        // Write-after-invalidate race: a token POST that STARTED before InvalidateTenant must not
        // repopulate the cache with its now-stale token. The first POST is parked in flight; the
        // admin grants + Refreshes (InvalidateTenant) while it is parked; on release it writes a
        // (stale-generation-tagged) token. A subsequent read MUST reject it and re-mint fresh.
        var handler = new InflightRaceHandler(
            firstBody: "{\"access_token\":\"stale\",\"expires_in\":3599}",
            laterBody: "{\"access_token\":\"fresh\",\"expires_in\":3599}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:ClientId"] = "test-client",
                ["EntraId:ClientSecret"] = "test-secret",
            })
            .Build();
        var svc = new GraphTokenService(
            NullLogger<GraphTokenService>.Instance,
            new SingleHandlerFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            config);

        // R1 starts; its POST parks in flight (holding the gate, before writing to the cache).
        var inflight = svc.GetAccessTokenAsync(TenantA);
        await WaitUntilAsync(() => handler.Entered >= 1, TimeSpan.FromSeconds(2));

        // Admin grants + Refresh WHILE R1's POST is in flight.
        svc.InvalidateTenant(TenantA);

        // R1 completes → writes "stale", tagged with the pre-invalidation generation.
        handler.Release();
        var r1 = await inflight;
        Assert.Equal("stale", r1.AccessToken); // R1's own caller still gets the token it acquired

        // Fresh read must NOT serve the stale entry — the generation mismatch makes it a miss → re-mint.
        var r2 = await svc.GetAccessTokenAsync(TenantA);
        Assert.Equal("fresh", r2.AccessToken);
        Assert.Equal(2, handler.Entered); // a real second POST happened (no stale cache hit)
    }

    [Fact]
    public async Task GetAccessTokenAsync_coalesces_concurrent_misses()
    {
        // Stampede guard, proven properly: a barrier-gated handler parks the first request on the
        // wire so the other nine genuinely pile up at the miss BEFORE any response/cache-fill. With
        // the per-tenant semaphore only ONE request ever reaches the wire; without it all ten would.
        // (A synchronous stub could pass even without the gate, because the first call may fill the
        // cache before the rest start — hence this stronger construction.)
        var handler = new GatedHttpMessageHandler("{\"access_token\":\"tok-A\",\"expires_in\":3599}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:ClientId"] = "test-client",
                ["EntraId:ClientSecret"] = "test-secret",
            })
            .Build();
        var svc = new GraphTokenService(
            NullLogger<GraphTokenService>.Instance,
            new SingleHandlerFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            config);

        var tasks = Enumerable.Range(0, 10).Select(_ => svc.GetAccessTokenAsync(TenantA)).ToArray();

        // Wait for the first request to reach the wire, then give the other nine ample time to block
        // on the semaphore. If coalescing works, exactly one request is at the handler.
        await WaitUntilAsync(() => handler.Entered >= 1, TimeSpan.FromSeconds(2));
        await Task.Delay(300);
        Assert.Equal(1, handler.Entered);

        handler.Release();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("tok-A", r.AccessToken));
        Assert.Equal(1, handler.Entered);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
