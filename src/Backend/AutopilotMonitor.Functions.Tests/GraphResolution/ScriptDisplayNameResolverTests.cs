using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>
/// End-to-end resolver behaviour: gate -> cache -> full-pull -> per-ID fallback -> 404 ->
/// transient. Uses fakes (no real Azure/Graph dependencies).
/// </summary>
public class ScriptDisplayNameResolverTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Returns_all_null_when_permission_missing()
    {
        var fix = new Fixture();
        // No context registered -> detector returns null.
        var refs = new[] { new ScriptRef(ScriptKind.Platform, "abc") };

        var result = await fix.Sut.ResolveAsync(TenantId, refs);

        Assert.Single(result.Names);
        Assert.Null(result.Names[refs[0]]);
        Assert.Empty(fix.Http.Requests); // Graph never called when permission gate fails
        Assert.Empty(fix.IncompleteLines); // "no token or no role" is an answer, not a cut
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Token_that_outlasts_its_budget_is_reported_as_an_incomplete_lookup()
    {
        var fix = new Fixture(tokenAcquireBudget: TimeSpan.FromMilliseconds(50));
        fix.Detector.HangingTenants.Add(TenantId);
        var r = new ScriptRef(ScriptKind.Platform, "abc");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(result.Names[r]);
        Assert.Empty(fix.Http.Requests);
        var line = Assert.Single(fix.IncompleteLines);
        Assert.Contains($"tenant={TenantId} ", line);
        Assert.Contains("reasons=TokenTimeout ", line);
        Assert.Contains("requested=1 ", line);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Failing_token_context_is_reported_as_an_incomplete_lookup()
    {
        var fix = new Fixture();
        fix.Detector.TokenContextThrows = new InvalidOperationException("simulated");
        var r = new ScriptRef(ScriptKind.Platform, "abc");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(result.Names[r]);
        Assert.Contains("reasons=TokenError ", Assert.Single(fix.IncompleteLines));
        Assert.False(result.TimedOut); // a failure is not a time limit: asking again is not suggested
    }

    [Fact]
    public async Task Cache_hit_returns_displayName_without_graph_call()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        var r = new ScriptRef(ScriptKind.Platform, "abc-123");
        fix.Repo.Entries[(TenantId, r.Kind, r.Id)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = r.Kind,
            ScriptId = r.Id,
            DisplayName = "MyScript.ps1",
            FetchedAt = fix.FixedNow,
            IsNotFound = false,
        };
        // Meta row exists and is fresh -> no full-pull path.
        fix.Repo.Meta[(TenantId, r.Kind)] = new ScriptNameCacheMeta
        {
            TenantId = TenantId,
            Kind = r.Kind,
            LastFullRefreshAt = fix.FixedNow - TimeSpan.FromDays(1),
        };

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Equal("MyScript.ps1", result.Names[r]);
        Assert.Empty(fix.Http.Requests);
    }

    [Fact]
    public async Task Cold_tenant_triggers_full_pull_and_populates_cache()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        var r = new ScriptRef(ScriptKind.Platform, "id-1");

        fix.Http.When("deviceManagementScripts?", HttpStatusCode.OK,
            "{\"value\":[" +
            "{\"id\":\"id-1\",\"displayName\":\"Bootstrap\",\"fileName\":\"bootstrap.ps1\"}," +
            "{\"id\":\"id-2\",\"displayName\":\"Cleanup\",\"fileName\":\"cleanup.ps1\"}" +
            "]}");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Equal("Bootstrap", result.Names[r]);
        Assert.Single(fix.Http.Requests);
        // Both list entries cached for future calls.
        Assert.Equal(2, fix.Repo.Entries.Count);
        Assert.True(fix.Repo.Meta.ContainsKey((TenantId, ScriptKind.Platform)));
        Assert.Empty(fix.IncompleteLines);
    }

    [Fact]
    public async Task Script_missing_from_the_list_is_still_resolved_by_id()
    {
        // The list leaves out some scripts that a single GET returns. Treating "not in the list"
        // as "not found" would drop their names.
        var fix = new Fixture();
        fix.GivenPermission();
        fix.Http.When("deviceHealthScripts?", HttpStatusCode.OK,
            "{\"value\":[{\"id\":\"listed\",\"displayName\":\"Listed\"}]}");
        fix.Http.When("deviceHealthScripts/unlisted", HttpStatusCode.OK,
            "{\"id\":\"unlisted\",\"displayName\":\"Unlisted\"}");
        var listed = new ScriptRef(ScriptKind.Remediation, "listed");
        var unlisted = new ScriptRef(ScriptKind.Remediation, "unlisted");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { listed, unlisted });

        Assert.Equal("Listed", result.Names[listed]);
        Assert.Equal("Unlisted", result.Names[unlisted]);
        Assert.Equal(2, fix.Http.Requests.Count);
        Assert.False(fix.Repo.Entries[(TenantId, ScriptKind.Remediation, "unlisted")].IsNotFound);
        Assert.Empty(fix.IncompleteLines);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Timed_out_list_pull_is_reported_and_skips_the_single_lookups()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        fix.Http.WhenTimedOut("deviceManagementScripts?");
        fix.Http.When("deviceManagementScripts/abc", HttpStatusCode.OK,
            "{\"id\":\"abc\",\"displayName\":\"SHOULD-NOT-BE-RETURNED\"}");
        var r = new ScriptRef(ScriptKind.Platform, "abc");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(result.Names[r]);
        Assert.Single(fix.Http.Requests);
        Assert.False(fix.Repo.Meta.ContainsKey((TenantId, ScriptKind.Platform)));
        Assert.Contains("reasons=ListTimeout:Platform ", Assert.Single(fix.IncompleteLines));
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Budget_ending_between_list_pages_stores_nothing_and_leaves_the_kind_unfetched()
    {
        // A list cut after its first page is incomplete. Stored together with the meta row it
        // would count as the tenant's whole list for a week.
        var fix = new Fixture();
        fix.GivenPermission();
        using var caller = new CancellationTokenSource();
        fix.Http.OnRequest = _ => caller.Cancel();
        fix.Http.When("deviceManagementScripts?", HttpStatusCode.OK,
            "{\"value\":[{\"id\":\"id-1\",\"displayName\":\"Bootstrap\"}]," +
            "\"@odata.nextLink\":\"https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts?$skiptoken=2\"}");
        var r = new ScriptRef(ScriptKind.Platform, "id-1");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r }, caller.Token);

        Assert.Equal("Bootstrap", result.Names[r]); // page one was read before the cut
        Assert.Single(fix.Http.Requests);
        Assert.Empty(fix.Repo.Entries);
        Assert.False(fix.Repo.Meta.ContainsKey((TenantId, ScriptKind.Platform)));
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task NetNew_id_after_recent_full_pull_uses_per_id_fallback()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        // Recent full-pull means we DON'T re-pull on a miss.
        fix.Repo.Meta[(TenantId, ScriptKind.Remediation)] = new ScriptNameCacheMeta
        {
            TenantId = TenantId,
            Kind = ScriptKind.Remediation,
            LastFullRefreshAt = fix.FixedNow - TimeSpan.FromDays(1),
        };
        var r = new ScriptRef(ScriptKind.Remediation, "net-new-id");
        fix.Http.When("deviceHealthScripts/net-new-id", HttpStatusCode.OK,
            "{\"id\":\"net-new-id\",\"displayName\":\"Net-New\"}");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Equal("Net-New", result.Names[r]);
        Assert.Single(fix.Http.Requests);
        Assert.Contains("deviceHealthScripts/net-new-id", fix.Http.Requests[0]);
        // Per-ID fetch also writes to cache.
        Assert.True(fix.Repo.Entries.ContainsKey((TenantId, ScriptKind.Remediation, "net-new-id")));
    }

    [Fact]
    public async Task PerId_404_writes_negative_cache_returns_null()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        fix.Repo.Meta[(TenantId, ScriptKind.Platform)] = new ScriptNameCacheMeta
        {
            TenantId = TenantId,
            Kind = ScriptKind.Platform,
            LastFullRefreshAt = fix.FixedNow - TimeSpan.FromDays(1),
        };
        var r = new ScriptRef(ScriptKind.Platform, "deleted-id");
        fix.Http.When("deviceManagementScripts/deleted-id", HttpStatusCode.NotFound, "{\"error\":{\"code\":\"NotFound\"}}");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(result.Names[r]);
        Assert.True(fix.Repo.Entries.ContainsKey((TenantId, ScriptKind.Platform, "deleted-id")));
        Assert.True(fix.Repo.Entries[(TenantId, ScriptKind.Platform, "deleted-id")].IsNotFound);
        Assert.Empty(fix.IncompleteLines); // a 404 is an answer
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Timed_out_single_lookup_is_reported_and_ends_the_loop()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        fix.Repo.Meta[(TenantId, ScriptKind.Remediation)] = new ScriptNameCacheMeta
        {
            TenantId = TenantId,
            Kind = ScriptKind.Remediation,
            LastFullRefreshAt = fix.FixedNow - TimeSpan.FromDays(1),
        };
        fix.Http.WhenTimedOut("deviceHealthScripts/first");
        fix.Http.When("deviceHealthScripts/second", HttpStatusCode.OK,
            "{\"id\":\"second\",\"displayName\":\"SHOULD-NOT-BE-RETURNED\"}");
        var refs = new[]
        {
            new ScriptRef(ScriptKind.Remediation, "first"),
            new ScriptRef(ScriptKind.Remediation, "second"),
        };

        var result = await fix.Sut.ResolveAsync(TenantId, refs);

        Assert.All(refs, r => Assert.Null(result.Names[r]));
        Assert.Single(fix.Http.Requests);
        Assert.Empty(fix.Repo.Entries); // a cut is not an answer: nothing is cached as not found
        Assert.Contains("reasons=LookupTimeout ", Assert.Single(fix.IncompleteLines));
        Assert.True(result.TimedOut);
    }

    [Theory]
    [InlineData("{\"id\":\"nameless\",\"displayName\":null}")]
    [InlineData("{\"id\":\"nameless\",\"displayName\":42}")]
    [InlineData("{\"id\":\"nameless\"}")]
    public async Task PerId_script_without_a_name_is_cached_and_not_fetched_again(string body)
    {
        // JSON null and a non-text name read as absent (D-420); the answer is still final, so the second view
        // is a cache hit instead of another Graph call.
        var fix = new Fixture();
        fix.GivenPermission();
        fix.Repo.Meta[(TenantId, ScriptKind.Platform)] = new ScriptNameCacheMeta
        {
            TenantId = TenantId,
            Kind = ScriptKind.Platform,
            LastFullRefreshAt = fix.FixedNow - TimeSpan.FromDays(1),
        };
        var r = new ScriptRef(ScriptKind.Platform, "nameless");
        fix.Http.When("deviceManagementScripts/nameless", HttpStatusCode.OK, body);

        var first = await fix.Sut.ResolveAsync(TenantId, new[] { r });
        var second = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(first.Names[r]);
        Assert.Null(second.Names[r]);
        Assert.Single(fix.Http.Requests);
        var entry = fix.Repo.Entries[(TenantId, ScriptKind.Platform, "nameless")];
        Assert.False(entry.IsNotFound);
        Assert.Null(entry.DisplayName);
    }

    [Fact]
    public async Task Empty_refs_returns_empty_dict()
    {
        var fix = new Fixture();
        fix.GivenPermission();

        var result = await fix.Sut.ResolveAsync(TenantId, Array.Empty<ScriptRef>());

        Assert.Empty(result.Names);
        Assert.Empty(fix.Http.Requests);
    }

    [Fact]
    public async Task Graph_5xx_during_full_pull_returns_null_without_throwing()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        fix.Http.When("deviceManagementScripts?", HttpStatusCode.InternalServerError, "{}");
        var r = new ScriptRef(ScriptKind.Platform, "abc");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(result.Names[r]);
    }

    [Fact]
    public async Task FullPull_429_suppresses_per_id_fallback_for_same_kind()
    {
        // Finding 2: when full-pull is throttled (429), per-ID fallback would just
        // amplify the throttle. We must abort per-ID for that kind.
        var fix = new Fixture();
        fix.GivenPermission();
        fix.Http.When("deviceManagementScripts?", (HttpStatusCode)429, "{\"error\":{\"code\":\"TooManyRequests\"}}");
        // Add the per-ID URL too — but the test asserts it should NOT be reached.
        fix.Http.When("deviceManagementScripts/abc", HttpStatusCode.OK,
            "{\"id\":\"abc\",\"displayName\":\"SHOULD-NOT-BE-RETURNED\"}");
        var r = new ScriptRef(ScriptKind.Platform, "abc");

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(result.Names[r]); // per-ID never ran
        Assert.Single(fix.Http.Requests); // only the failed full-pull
        Assert.Contains("deviceManagementScripts?", fix.Http.Requests[0]);
        Assert.Contains("reasons=ListError:Platform ", Assert.Single(fix.IncompleteLines));
        Assert.False(result.TimedOut); // Graph refused: an immediate second lookup would add to the throttle
    }

    [Fact]
    public async Task PerId_429_breaks_loop_on_first_transient()
    {
        // Finding 2: in the per-ID fallback path (after a recent full-pull), the
        // first transient HTTP failure aborts the remaining per-ID calls so we
        // don't dig the throttling hole deeper.
        var fix = new Fixture();
        fix.GivenPermission();
        // Recent meta -> per-ID fallback path for net-new ids.
        fix.Repo.Meta[(TenantId, ScriptKind.Remediation)] = new ScriptNameCacheMeta
        {
            TenantId = TenantId,
            Kind = ScriptKind.Remediation,
            LastFullRefreshAt = fix.FixedNow - TimeSpan.FromDays(1),
        };

        fix.Http.When("deviceHealthScripts/first", (HttpStatusCode)429, "{}");
        // Stub a happy second call to prove it would have succeeded if we kept going.
        fix.Http.When("deviceHealthScripts/second", HttpStatusCode.OK,
            "{\"id\":\"second\",\"displayName\":\"SHOULD-NOT-BE-RETURNED\"}");

        var refs = new[]
        {
            new ScriptRef(ScriptKind.Remediation, "first"),
            new ScriptRef(ScriptKind.Remediation, "second"),
        };

        var result = await fix.Sut.ResolveAsync(TenantId, refs);

        Assert.All(refs, r => Assert.Null(result.Names[r]));
        Assert.Single(fix.Http.Requests); // second call never issued
        Assert.Contains("deviceHealthScripts/first", fix.Http.Requests[0]);
        Assert.Contains("reasons=LookupError ", Assert.Single(fix.IncompleteLines));
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Negative_cache_hit_returns_null_no_graph_call()
    {
        var fix = new Fixture();
        fix.GivenPermission();
        var r = new ScriptRef(ScriptKind.Remediation, "deleted");
        fix.Repo.Entries[(TenantId, r.Kind, r.Id)] = new ScriptDisplayNameEntry
        {
            TenantId = TenantId,
            Kind = r.Kind,
            ScriptId = r.Id,
            DisplayName = null,
            FetchedAt = fix.FixedNow - TimeSpan.FromMinutes(30),
            IsNotFound = true,
        };

        var result = await fix.Sut.ResolveAsync(TenantId, new[] { r });

        Assert.Null(result.Names[r]);
        Assert.Empty(fix.Http.Requests);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public FakeGraphFeatureDetector Detector { get; } = new();
        public FakeScriptNameCacheRepository Repo { get; } = new();
        public StubHttpMessageHandler Http { get; } = new();
        public StubHttpClientFactory HttpFactory { get; }
        public Mock<ILogger<ScriptDisplayNameResolver>> Logger { get; } = new();
        public ScriptDisplayNameResolver Sut { get; }
        public DateTimeOffset FixedNow { get; } = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);

        public Fixture(TimeSpan? tokenAcquireBudget = null)
        {
            HttpFactory = new StubHttpClientFactory(Http);
            Sut = new ScriptDisplayNameResolver(
                Detector, Repo, HttpFactory,
                Logger.Object,
                new TelemetryClient(new TelemetryConfiguration()),
                new FakeTimeProvider(FixedNow),
                tokenAcquireBudget);
        }

        /// <summary>The summary lines written for lookups that ended without every answer.</summary>
        public List<string> IncompleteLines => Logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log) && i.Arguments[0] is LogLevel.Warning)
            .Select(i => i.Arguments[2]?.ToString() ?? string.Empty)
            .Where(m => m.StartsWith("Script-name lookup incomplete:", StringComparison.Ordinal))
            .ToList();

        public void GivenPermission()
        {
            Detector.ContextByTenant[TenantId] = new GraphTenantTokenContext
            {
                TenantId = TenantId,
                AccessToken = "fake-token",
                GrantedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    GraphAppPermissions.DeviceManagementScriptsReadAll,
                },
                ExpiresAt = FixedNow.AddHours(1),
            };
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
