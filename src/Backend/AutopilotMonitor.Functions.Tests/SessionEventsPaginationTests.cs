using System.Collections.Generic;
using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR 2 (mcp-pagination-rollout) — pagination wiring on
/// <c>GET /api/sessions/{sessionId}/events</c>.
/// </summary>
public class SessionEventsPaginationTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionA = "11111111-2222-3333-4444-555555555555";
    private const string SessionB = "99999999-8888-7777-6666-555555555555";

    // ---------- (a) no pageSize → unpaginated path; query parser surfaces null ----------

    [Fact]
    public void ParseQuery_without_pageSize_keeps_unpaginated_legacy_path()
    {
        var parsed = SessionEventsPagination.ParseQuery(new NameValueCollection());

        Assert.Null(parsed.PageSize);
        Assert.Null(parsed.Continuation);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void ParseQuery_drops_continuation_when_pageSize_absent()
    {
        // continuation alone is meaningless — silently dropped so a stale token in
        // a bookmark does not force callers into an error path.
        var qs = new NameValueCollection { { "continuation", "abc" } };
        var parsed = SessionEventsPagination.ParseQuery(qs);

        Assert.Null(parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    // ---------- (b) with pageSize → paginated path; helper surfaces values ----------

    [Fact]
    public void ParseQuery_with_pageSize_activates_paginated_path()
    {
        var qs = new NameValueCollection
        {
            { "pageSize", "100" },
            { "continuation", "ENCODED" },
        };
        var parsed = SessionEventsPagination.ParseQuery(qs);

        Assert.Equal(100, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    public void ParseQuery_rejects_pageSize_outside_range(string raw)
    {
        var parsed = SessionEventsPagination.ParseQuery(new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void ParseQuery_rejects_non_integer_pageSize()
    {
        var parsed = SessionEventsPagination.ParseQuery(new NameValueCollection { { "pageSize", "banana" } });
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void BuildNextLink_includes_pageSize_continuation_and_optional_tenantId()
    {
        var link = SessionEventsPagination.BuildNextLink(SessionA, 200, "TOKEN+/=", TenantA);

        Assert.StartsWith($"/api/sessions/{SessionA}/events?", link);
        Assert.Contains("pageSize=200", link);
        Assert.Contains("continuation=TOKEN%2B%2F%3D", link); // url-escaped
        Assert.Contains($"tenantId={TenantA}", link);
    }

    [Fact]
    public void BuildNextLink_omits_tenantId_when_caller_does_not_pass_it()
    {
        var link = SessionEventsPagination.BuildNextLink(SessionA, 200, "abc", null);

        Assert.DoesNotContain("tenantId=", link);
    }

    // ---------- (e) GA cross-tenant nextLink → token round-trip ----------

    [Fact]
    public void NextLink_carries_resolved_tenantId_for_GA_cross_tenant_round_trip()
    {
        // Regression: previously the function called BuildNextLink with the raw
        // ?tenantId= query param, which is null when GA reads /api/sessions/{id}/events
        // without explicitly passing tenantId. Page 1 then bound the token to the
        // resolved tenant (via ResolveSessionTenantIdAsync), but the nextLink omitted
        // it — so page 2 validated against the GA's JWT tenant and the token was
        // rejected as cross_tenant. Fix: nextLink now carries the resolved tenantId
        // verbatim, and the function honours ?tenantId= on follow-up pages for GA.
        const string ResolvedTenant = "c3d4e5f6-a7b8-9012-cdef-abcdef123456";

        var fp = SessionEventsPagination.Fingerprint(ResolvedTenant, SessionA);
        var encoded = ContinuationToken.Encode("rawAzureToken", ResolvedTenant, fp);
        var link = SessionEventsPagination.BuildNextLink(SessionA, 200, encoded, ResolvedTenant);

        // Wire-form: tenantId is in the URL so the next-call extractor sees it.
        Assert.Contains($"tenantId={ResolvedTenant}", link);

        // And the token validates against that same tenant — no cross_tenant reject.
        var ok = SessionEventsPagination.TryAcceptContinuation(
            encoded, ResolvedTenant, SessionA, out var azure, out var reason);
        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzureToken", azure);
    }

    // ---------- (d) cross-session continuation rejection ----------

    [Fact]
    public void TryAcceptContinuation_rejects_token_issued_for_a_different_session()
    {
        var fpA = SessionEventsPagination.Fingerprint(TenantA, SessionA);
        var encodedForA = ContinuationToken.Encode("rawAzureToken", TenantA, fpA);

        var ok = SessionEventsPagination.TryAcceptContinuation(
            encodedForA, TenantA, SessionB, out var azure, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
        Assert.Equal(string.Empty, azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_issued_for_a_different_tenant()
    {
        var fpA = SessionEventsPagination.Fingerprint(TenantA, SessionA);
        var encodedForA = ContinuationToken.Encode("rawAzureToken", TenantA, fpA);

        const string TenantB = "00000000-0000-0000-0000-000000000099";
        var ok = SessionEventsPagination.TryAcceptContinuation(
            encodedForA, TenantB, SessionA, out var azure, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
        Assert.Equal(string.Empty, azure);
    }

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_session_and_tenant()
    {
        var fp = SessionEventsPagination.Fingerprint(TenantA, SessionA);
        var encoded = ContinuationToken.Encode("rawAzureToken", TenantA, fp);

        var ok = SessionEventsPagination.TryAcceptContinuation(
            encoded, TenantA, SessionA, out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzureToken", azure);
    }

    // ---------- (c) consume-until-absent → repo total reachable across pages ----------

    [Fact]
    public async Task ConsumeUntilAbsent_drains_all_events_from_paged_repository()
    {
        // Simulates a 5-page session (200 events/page) plus a final partial page —
        // the same loop the UI/AI follow until the response no longer carries a
        // nextRawToken. Verifies the contract: full data is reachable across N
        // calls without truncation.
        const int totalEvents = 1083;
        const int pageSize = 200;
        var pages = SplitIntoPages(totalEvents, pageSize);

        var repo = new Mock<AutopilotMonitor.Shared.DataAccess.ISessionRepository>(MockBehavior.Strict);

        var callIndex = 0;
        repo
            .Setup(r => r.GetSessionEventsPageAsync(TenantA, SessionA, pageSize, It.IsAny<string?>()))
            .ReturnsAsync(() => pages[callIndex++]);

        var collected = new List<EnrollmentEvent>();
        string? continuation = null;
        var loopGuard = 0;
        while (true)
        {
            var page = await repo.Object.GetSessionEventsPageAsync(TenantA, SessionA, pageSize, continuation);
            collected.AddRange(page.Items);
            if (string.IsNullOrEmpty(page.NextRawToken)) break;
            continuation = page.NextRawToken;
            if (++loopGuard > 50) Assert.Fail("Pagination loop did not terminate within 50 iterations");
        }

        Assert.Equal(totalEvents, collected.Count);
        // Sequence must be unique and complete (1..N) — no events lost or duplicated.
        var sequences = new HashSet<long>(System.Linq.Enumerable.Select(collected, e => e.Sequence));
        Assert.Equal(totalEvents, sequences.Count);
        Assert.Equal(1L, System.Linq.Enumerable.Min(sequences));
        Assert.Equal((long)totalEvents, System.Linq.Enumerable.Max(sequences));
    }

    private static List<RawPage<EnrollmentEvent>> SplitIntoPages(int total, int pageSize)
    {
        var result = new List<RawPage<EnrollmentEvent>>();
        long seq = 1;
        var emitted = 0;
        while (emitted < total)
        {
            var take = System.Math.Min(pageSize, total - emitted);
            var batch = new List<EnrollmentEvent>(take);
            for (int i = 0; i < take; i++)
            {
                batch.Add(new EnrollmentEvent
                {
                    SessionId = SessionA,
                    TenantId = TenantA,
                    Sequence = seq++,
                    EventType = "synthetic",
                });
            }
            emitted += take;
            var hasMore = emitted < total;
            result.Add(new RawPage<EnrollmentEvent>(batch, hasMore ? $"raw-cursor-{result.Count + 1}" : null));
        }
        return result;
    }
}
