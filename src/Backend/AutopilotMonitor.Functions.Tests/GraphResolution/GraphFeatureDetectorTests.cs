using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>
/// Pure-logic tests for <see cref="GraphFeatureDetector.TryParseToken"/>. The cache +
/// token-service orchestration is covered indirectly by <c>ScriptDisplayNameResolverTests</c>
/// (PR-C), where a fake context is set up end-to-end.
/// </summary>
public class GraphFeatureDetectorTests
{
    [Fact]
    public void TryParseToken_returns_single_role()
    {
        var token = BuildJwt(
            new[] { new Claim("roles", GraphAppPermissions.DeviceManagementScriptsReadAll) },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out var expiresAt));
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, roles);
        Assert.Single(roles);
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryParseToken_collects_multiple_roles_case_insensitive()
    {
        var token = BuildJwt(
            new[]
            {
                new Claim("roles", "DeviceManagementScripts.Read.All"),
                new Claim("roles", "DeviceManagementConfiguration.Read.All"),
                new Claim("roles", "DeviceManagementManagedDevices.Read.All"),
            },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out _));
        Assert.Equal(3, roles.Count);
        // Case-insensitive contains: tenant might issue with slightly different casing.
        Assert.Contains("devicemanagementscripts.read.all", roles);
    }

    [Fact]
    public void TryParseToken_returns_empty_set_when_roles_claim_missing()
    {
        var token = BuildJwt(
            new[] { new Claim("scp", "User.Read") },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out _));
        Assert.Empty(roles);
    }

    [Fact]
    public void TryParseToken_filters_empty_role_values()
    {
        var token = BuildJwt(
            new[]
            {
                new Claim("roles", ""),
                new Claim("roles", "  "),
                new Claim("roles", "DeviceManagementScripts.Read.All"),
            },
            expires: DateTime.UtcNow.AddHours(1));

        Assert.True(GraphFeatureDetector.TryParseToken(token, out var roles, out _));
        Assert.Single(roles);
        Assert.Contains("DeviceManagementScripts.Read.All", roles);
    }

    [Fact]
    public void TryParseToken_fails_on_garbage_input()
    {
        Assert.False(GraphFeatureDetector.TryParseToken("not.a.jwt", out _, out _));
        Assert.False(GraphFeatureDetector.TryParseToken("", out _, out _));
        Assert.False(GraphFeatureDetector.TryParseToken("definitely-not-base64-and-no-dots", out _, out _));
    }

    [Fact]
    public void TryParseToken_reads_expiry_correctly()
    {
        var fixedExpiry = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var token = BuildJwt(new[] { new Claim("roles", "x") }, expires: fixedExpiry);

        Assert.True(GraphFeatureDetector.TryParseToken(token, out _, out var expiresAt));
        // JWT 'exp' is unix seconds — second precision is sufficient.
        Assert.Equal(fixedExpiry, expiresAt.UtcDateTime);
    }

    // ── HasPermission helper observable behaviour via cache-only path ────────

    [Fact]
    public void IsFeatureGranted_via_catalog()
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GraphAppPermissions.DeviceManagementScriptsReadAll,
        };

        Assert.True(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, roles));
        Assert.False(GraphFeatureCatalog.IsFeatureGranted("UnknownFeature", roles));
    }

    [Fact]
    public void IsFeatureGranted_returns_false_when_required_permission_missing()
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Foo.Bar" };

        Assert.False(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, roles));
    }

    [Fact]
    public void IsFeatureGranted_accepts_any_collection_shape_without_unsafe_cast()
    {
        // Verifies Finding 3: the catalog API no longer demands ISet<string>, so an
        // IReadOnlySet (or any IEnumerable) works without casting.
        IReadOnlySet<string> roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GraphAppPermissions.DeviceManagementScriptsReadAll,
        };
        Assert.True(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, roles));

        // Even a plain array (case-insensitive match handled internally).
        string[] rolesArray = { "devicemanagementscripts.read.all" };
        Assert.True(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, rolesArray));

        // Null collection is safely false.
        Assert.False(GraphFeatureCatalog.IsFeatureGranted(
            GraphFeatureCatalog.FeatureScriptDisplayNames, (IEnumerable<string>?)null));
    }

    // ── ScriptRef parsing (Shared/Models/Graph) ─────────────────────────────

    [Theory]
    [InlineData("Platform:abc-123", ScriptKind.Platform, "abc-123")]
    [InlineData("platform:abc-123", ScriptKind.Platform, "abc-123")]
    [InlineData("Remediation:def", ScriptKind.Remediation, "def")]
    [InlineData("REMEDIATION:def", ScriptKind.Remediation, "def")]
    public void ScriptRef_parses_canonical_form(string input, ScriptKind kind, string id)
    {
        Assert.True(ScriptRef.TryParse(input, out var r));
        Assert.Equal(kind, r.Kind);
        Assert.Equal(id, r.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nokind")]
    [InlineData("UnknownKind:abc")]
    [InlineData(":id")]
    [InlineData("Platform:")]
    public void ScriptRef_rejects_malformed(string? input)
    {
        Assert.False(ScriptRef.TryParse(input, out _));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string BuildJwt(IEnumerable<Claim> claims, DateTime expires)
    {
        // JWT without signature is fine — the detector NEVER validates signature
        // (this is our own token, read seconds after acquisition from Azure AD over TLS).
        var jwt = new JwtSecurityToken(
            issuer: "https://sts.windows.net/test",
            audience: "https://graph.microsoft.com",
            claims: claims,
            notBefore: null,
            expires: expires);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>Smoke: cache hit short-circuits — no token-service call needed.</summary>
    [Fact]
    public void Cache_hit_returns_cached_context_without_acquire()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        var preSeeded = new GraphTenantTokenContext
        {
            TenantId = tenantId,
            AccessToken = "fake",
            GrantedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                GraphAppPermissions.DeviceManagementScriptsReadAll,
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        };
        cache.Set($"graph-feature-detector:{tenantId}", preSeeded);

        // We can verify cache layout works by reading back through the same key.
        // Full Detector orchestration is covered in PR-C via the Resolver integration tests.
        Assert.True(cache.TryGetValue($"graph-feature-detector:{tenantId}",
            out GraphTenantTokenContext? roundtrip));
        Assert.Equal(tenantId, roundtrip!.TenantId);
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, roundtrip.GrantedRoles);
    }

    // ── Negative caching: avoid replaying the AAD consent-propagation retry chain ─────
    // for tenants where the multi-tenant app was never consented to (e.g. cross-tenant
    // sessions a Global Admin keeps clicking through during agent triage).

    [Fact]
    public async Task PermanentFailure_caches_negative_verdict_and_short_circuits_next_call()
    {
        const string tenantId = "22222222-2222-2222-2222-222222222222";
        var tokenService = new StubGraphTokenService();
        var detector = NewDetector(tokenService, out var cache);

        // First attempt: token service reports "no consent" definitively.
        tokenService.NextResults.Enqueue(GraphTokenResult.PermanentFailure());
        var first = await detector.TryGetTokenContextAsync(tenantId);

        Assert.Null(first);
        Assert.Equal(1, tokenService.CallCount);
        // Cache now carries the negative marker (not visible as GraphTenantTokenContext).
        Assert.True(cache.TryGetValue($"graph-feature-detector:{tenantId}", out object? cached));
        Assert.NotNull(cached);
        Assert.False(cached is GraphTenantTokenContext);

        // Second attempt within the negative TTL: must NOT re-invoke the token service.
        var second = await detector.TryGetTokenContextAsync(tenantId);
        Assert.Null(second);
        Assert.Equal(1, tokenService.CallCount);
    }

    [Fact]
    public async Task PermanentFailure_negative_verdict_reports_not_transient_on_snapshot()
    {
        const string tenantId = "33333333-3333-3333-3333-333333333333";
        var tokenService = new StubGraphTokenService();
        var detector = NewDetector(tokenService, out _);

        tokenService.NextResults.Enqueue(GraphTokenResult.PermanentFailure());
        await detector.TryGetTokenContextAsync(tenantId); // primes negative cache

        var snapshot = await detector.GetSnapshotAsync(tenantId);

        // Verdict is definite — UI must NOT render "unknown / try again" on a negative cache
        // hit (that's reserved for transient failures).
        Assert.False(snapshot.IsTransient);
        Assert.Empty(snapshot.GrantedRoles);
        Assert.Equal(1, tokenService.CallCount);
    }

    [Fact]
    public async Task TransientFailure_is_NOT_cached_and_retries_on_next_call()
    {
        const string tenantId = "44444444-4444-4444-4444-444444444444";
        var tokenService = new StubGraphTokenService();
        var detector = NewDetector(tokenService, out var cache);

        tokenService.NextResults.Enqueue(GraphTokenResult.TransientFailure());
        var first = await detector.TryGetTokenContextAsync(tenantId);
        Assert.Null(first);
        Assert.Equal(1, tokenService.CallCount);
        // No entry persisted — transient failures must be retried.
        Assert.False(cache.TryGetValue($"graph-feature-detector:{tenantId}", out object? _));

        // Second attempt: now consent has propagated and the token comes back fine.
        tokenService.NextResults.Enqueue(GraphTokenResult.Success(
            BuildJwt(new[] { new Claim("roles", GraphAppPermissions.DeviceManagementScriptsReadAll) },
                expires: DateTime.UtcNow.AddHours(1))));

        var second = await detector.TryGetTokenContextAsync(tenantId);
        Assert.NotNull(second);
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, second!.GrantedRoles);
        Assert.Equal(2, tokenService.CallCount);
    }

    [Fact]
    public async Task InvalidateTenant_clears_negative_marker_so_freshly_granted_tenant_lights_up()
    {
        const string tenantId = "55555555-5555-5555-5555-555555555555";
        var tokenService = new StubGraphTokenService();
        var detector = NewDetector(tokenService, out _);

        // Step 1: tenant has not granted yet → negative cache.
        tokenService.NextResults.Enqueue(GraphTokenResult.PermanentFailure());
        await detector.TryGetTokenContextAsync(tenantId);
        Assert.Equal(1, tokenService.CallCount);

        // Step 2: admin runs the grant script then hits "Refresh" in the UI → InvalidateTenant.
        detector.InvalidateTenant(tenantId);

        // Step 3: next attempt MUST re-acquire (otherwise the customer would have to wait the
        // full negative TTL despite explicitly asking for a fresh check).
        tokenService.NextResults.Enqueue(GraphTokenResult.Success(
            BuildJwt(new[] { new Claim("roles", GraphAppPermissions.DeviceManagementScriptsReadAll) },
                expires: DateTime.UtcNow.AddHours(1))));

        var refreshed = await detector.TryGetTokenContextAsync(tenantId);
        Assert.NotNull(refreshed);
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, refreshed!.GrantedRoles);
        Assert.Equal(2, tokenService.CallCount);
    }

    [Fact]
    public async Task InvalidateTenant_drops_underlying_token_cache_so_post_grant_refresh_sees_new_roles()
    {
        // Regression for the exact consent-propagation bug: a token acquired BEFORE the grant
        // carries an empty roles claim, and GraphTokenService caches that raw JWT (~55 min). After
        // the admin grants the role and hits Refresh (→ InvalidateTenant), the detector must mint a
        // FRESH token and report the new role — NOT re-parse the stale cached JWT. This exercises
        // the cross-layer invalidation wiring with a REAL GraphTokenService (not the stub), so a
        // future change that forgets to clear the token cache fails here.
        const string tenantId = "66666666-6666-6666-6666-666666666666";

        var handler = new SwappableTokenHandler(
            BuildJwt(Array.Empty<Claim>(), expires: DateTime.UtcNow.AddHours(1))); // pre-grant: no roles
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tokenService = new GraphTokenService(
            NullLogger<GraphTokenService>.Instance,
            new SingleHandlerFactory(handler),
            cache,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EntraId:ClientId"] = "test-client",
                    ["EntraId:ClientSecret"] = "test-secret",
                })
                .Build());
        var telemetry = new TelemetryClient(new TelemetryConfiguration { DisableTelemetry = true });
        var detector = new GraphFeatureDetector(
            tokenService, cache, NullLogger<GraphFeatureDetector>.Instance, telemetry);

        // Pre-grant read: empty roles, and the raw JWT is now cached in GraphTokenService.
        var before = await detector.GetSnapshotAsync(tenantId);
        Assert.Empty(before.GrantedRoles);
        Assert.Equal(1, handler.Requests);

        // Admin grants the role; the wire now returns a JWT WITH it.
        handler.Body = BuildJwt(
            new[] { new Claim("roles", GraphAppPermissions.DeviceManagementScriptsReadAll) },
            expires: DateTime.UtcNow.AddHours(1));

        // The fresh-read trigger. Without the cross-layer wiring the stale empty-roles JWT would
        // still be served and this would keep reporting "not granted".
        detector.InvalidateTenant(tenantId);

        var after = await detector.GetSnapshotAsync(tenantId);
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, after.GrantedRoles);
        Assert.Equal(2, handler.Requests); // fresh token minted, not served from cache
    }

    [Fact]
    public async Task InvalidateTenant_during_in_flight_acquire_does_not_cache_stale_roles()
    {
        // Write-after-invalidate race on the DETECTOR's own cache: an acquire that started before
        // InvalidateTenant must not leave a stale parsed-roles ctx behind. The token acquire is
        // parked in flight; the admin grants + Refreshes (InvalidateTenant) while it is parked; on
        // release the detector writes a (stale-generation-tagged) ctx. A fresh read MUST reject it
        // and re-acquire, seeing the newly granted role.
        const string tenantId = "77777777-7777-7777-7777-777777777777";
        var tokenService = new GatedStubGraphTokenService(
            firstToken: BuildJwt(Array.Empty<Claim>(), expires: DateTime.UtcNow.AddHours(1)),       // pre-grant: no roles
            laterToken: BuildJwt(new[] { new Claim("roles", GraphAppPermissions.DeviceManagementScriptsReadAll) },
                expires: DateTime.UtcNow.AddHours(1)));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var telemetry = new TelemetryClient(new TelemetryConfiguration { DisableTelemetry = true });
        var detector = new GraphFeatureDetector(
            tokenService, cache, NullLogger<GraphFeatureDetector>.Instance, telemetry);

        // D1 starts; its token acquire parks in flight (genAtStart captured at method entry).
        var inflight = detector.GetSnapshotAsync(tenantId);
        await WaitUntilAsync(() => tokenService.CallCount >= 1, TimeSpan.FromSeconds(2));

        // Admin grants + Refresh WHILE D1 is mid-acquire.
        detector.InvalidateTenant(tenantId);

        // D1 completes → writes a CachedVerdict tagged with the pre-invalidation generation.
        tokenService.Release();
        var before = await inflight;
        Assert.Empty(before.GrantedRoles); // D1 itself saw the pre-grant token

        // Fresh read must reject the stale-tagged entry (gen mismatch) and re-acquire → new role.
        var after = await detector.GetSnapshotAsync(tenantId);
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, after.GrantedRoles);
        Assert.Equal(2, tokenService.CallCount); // a real re-acquire happened (no stale cache hit)
    }

    [Fact]
    public async Task InvalidateTenant_token_layer_first_prevents_stale_detector_write_in_overlap()
    {
        // Cross-layer ordering race: InvalidateTenant must invalidate the token layer BEFORE bumping
        // its own generation. We pause INSIDE the token-layer invalidation and slip a concurrent
        // GetSnapshotAsync into the gap. With the correct (token-first) order, that overlapping read
        // cannot leave an accepted stale ctx behind; with the buggy (gen-first) order it would, and
        // the post-refresh read below would still report the old (no-roles) verdict.
        const string tenantId = "88888888-8888-8888-8888-888888888888";
        var handler = new SwappableTokenHandler(
            BuildJwt(Array.Empty<Claim>(), expires: DateTime.UtcNow.AddHours(1))); // pre-grant: no roles
        var cache = new MemoryCache(new MemoryCacheOptions());

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new System.Threading.ManualResetEventSlim(false);
        var tokenService = new BarrieredInvalidateTokenService(
            new SingleHandlerFactory(handler), cache,
            onInvalidate: () => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); });
        var telemetry = new TelemetryClient(new TelemetryConfiguration { DisableTelemetry = true });
        var detector = new GraphFeatureDetector(
            tokenService, cache, NullLogger<GraphFeatureDetector>.Instance, telemetry);

        // Prime: cache the old (no-roles) token + parsed ctx on both layers.
        var primed = await detector.GetSnapshotAsync(tenantId);
        Assert.Empty(primed.GrantedRoles);

        // Admin grants the role on the wire.
        handler.Body = BuildJwt(
            new[] { new Claim("roles", GraphAppPermissions.DeviceManagementScriptsReadAll) },
            expires: DateTime.UtcNow.AddHours(1));

        // Admin Refresh → InvalidateTenant, blocked inside the token-layer invalidation step.
        var invalidateTask = Task.Run(() => detector.InvalidateTenant(tenantId));
        await entered.Task;

        // A concurrent read slips into the gap, then we let InvalidateTenant finish.
        var overlap = await detector.GetSnapshotAsync(tenantId);
        release.Set();
        await invalidateTask;

        // Post-refresh read MUST reflect the granted role — no stale entry survived the overlap.
        var afterRefresh = await detector.GetSnapshotAsync(tenantId);
        Assert.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll, afterRefresh.GrantedRoles);
        _ = overlap; // the overlapping read itself may legitimately observe pre-refresh state
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // ── Test seam: token service stub + detector factory ──────────────────────────────

    /// <summary>
    /// Subclass that bypasses the real AAD HTTP call so tests can drive the detector with
    /// canned <see cref="GraphTokenResult"/> values. Base ctor dependencies are satisfied
    /// but never touched (we override the only method the detector calls).
    /// </summary>
    private sealed class StubGraphTokenService : GraphTokenService
    {
        public Queue<GraphTokenResult> NextResults { get; } = new();
        public int CallCount { get; private set; }

        public StubGraphTokenService()
            : base(
                NullLogger<GraphTokenService>.Instance,
                new NoopHttpClientFactory(),
                new MemoryCache(new MemoryCacheOptions()),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["EntraId:ClientId"] = "test-client-id",
                        ["EntraId:ClientSecret"] = "test-secret",
                    })
                    .Build())
        {
        }

        public override Task<GraphTokenResult> GetAccessTokenAsync(string tenantId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(NextResults.Count > 0
                ? NextResults.Dequeue()
                : GraphTokenResult.PermanentFailure());
        }
    }

    /// <summary>
    /// Token-service stub whose FIRST acquire parks until <see cref="Release"/> is called (modelling
    /// a token fetch in flight while an InvalidateTenant runs), returning <c>firstToken</c>; every
    /// later acquire returns <c>laterToken</c> immediately. Drives the detector's in-flight
    /// write-after-invalidate test. CallCount is incremented BEFORE parking and read thread-safely.
    /// </summary>
    private sealed class GatedStubGraphTokenService : GraphTokenService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _firstToken;
        private readonly string _laterToken;
        private int _calls;

        public GatedStubGraphTokenService(string firstToken, string laterToken)
            : base(
                NullLogger<GraphTokenService>.Instance,
                new NoopHttpClientFactory(),
                new MemoryCache(new MemoryCacheOptions()),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["EntraId:ClientId"] = "test-client-id",
                        ["EntraId:ClientSecret"] = "test-secret",
                    })
                    .Build())
        {
            _firstToken = firstToken;
            _laterToken = laterToken;
        }

        public int CallCount => System.Threading.Volatile.Read(ref _calls);
        public void Release() => _release.TrySetResult();

        public override async Task<GraphTokenResult> GetAccessTokenAsync(string tenantId, CancellationToken ct = default)
        {
            var n = System.Threading.Interlocked.Increment(ref _calls);
            if (n == 1)
            {
                await _release.Task.ConfigureAwait(false);
                return GraphTokenResult.Success(_firstToken);
            }
            return GraphTokenResult.Success(_laterToken);
        }
    }

    /// <summary>
    /// Real GraphTokenService (real token cache + generation + HTTP path) whose InvalidateTenant is
    /// wrapped with a test-controlled barrier, run BEFORE delegating to the base. Lets a test pause
    /// inside the token-layer invalidation and interleave a concurrent acquire, to prove the
    /// cross-layer ordering in GraphFeatureDetector.InvalidateTenant.
    /// </summary>
    private sealed class BarrieredInvalidateTokenService : GraphTokenService
    {
        private readonly Action _onInvalidate;

        public BarrieredInvalidateTokenService(
            System.Net.Http.IHttpClientFactory factory, IMemoryCache cache, Action onInvalidate)
            : base(
                NullLogger<GraphTokenService>.Instance,
                factory,
                cache,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["EntraId:ClientId"] = "test-client-id",
                        ["EntraId:ClientSecret"] = "test-secret",
                    })
                    .Build())
        {
            _onInvalidate = onInvalidate;
        }

        public override void InvalidateTenant(string tenantId)
        {
            _onInvalidate();
            base.InvalidateTenant(tenantId);
        }
    }

    /// <summary>Placeholder factory — the detector never causes the base GraphTokenService HTTP path to run.</summary>
    private sealed class NoopHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP path should not be exercised in detector tests");
    }

    private static GraphFeatureDetector NewDetector(StubGraphTokenService tokenService, out IMemoryCache cache)
    {
        cache = new MemoryCache(new MemoryCacheOptions());
        var telemetry = new TelemetryClient(new TelemetryConfiguration { DisableTelemetry = true });
        return new GraphFeatureDetector(
            tokenService,
            cache,
            NullLogger<GraphFeatureDetector>.Instance,
            telemetry);
    }
}
