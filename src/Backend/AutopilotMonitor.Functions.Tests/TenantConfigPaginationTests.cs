using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Opt-in pagination wiring on <c>GET /api/config/all</c> (fixes the MCP
/// list_tenants size-cap). The endpoint is GlobalAdminOnly with no filter
/// params; when <c>pageSize</c> is absent the function returns the legacy
/// unpaginated bare array, so these helpers only engage once a caller opts in.
/// The continuation token binds to the caller's identity to block cross-caller
/// replay of deep paginated bookmark links.
/// </summary>
public class TenantConfigPaginationTests
{
    private const string CallerTenant = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string CallerTenantOther = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // ────────── ParseQuery ──────────────────────────────────────────────────

    [Fact]
    public void ParseQuery_with_no_params_signals_unpaginated_mode()
    {
        var parsed = TenantConfigPagination.ParseQuery(new NameValueCollection());

        Assert.Null(parsed.Error);
        Assert.Null(parsed.PageSize);          // null PageSize → legacy bare-array path
        Assert.Null(parsed.Continuation);
    }

    [Fact]
    public void ParseQuery_with_pageSize_activates_pagination()
    {
        var parsed = TenantConfigPagination.ParseQuery(new NameValueCollection
        {
            { "pageSize", "100" },
            { "continuation", "ENCODED" },
            { "fields", "tenantId,domainName" },
        });

        Assert.Null(parsed.Error);
        Assert.Equal(100, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
        Assert.Equal("tenantId,domainName", parsed.Fields);
    }

    [Fact]
    public void ParseQuery_leaves_fields_null_when_absent()
    {
        var parsed = TenantConfigPagination.ParseQuery(new NameValueCollection { { "pageSize", "100" } });
        Assert.Null(parsed.Fields);
    }

    [Fact]
    public void ParseQuery_drops_continuation_when_pageSize_absent()
    {
        var parsed = TenantConfigPagination.ParseQuery(new NameValueCollection
        {
            { "continuation", "STALE" },
        });

        Assert.Null(parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParseQuery_rejects_invalid_pageSize(string raw)
    {
        var parsed = TenantConfigPagination.ParseQuery(new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1000")]
    public void ParseQuery_accepts_pageSize_bounds(string raw)
    {
        var parsed = TenantConfigPagination.ParseQuery(new NameValueCollection { { "pageSize", raw } });
        Assert.Null(parsed.Error);
        Assert.Equal(int.Parse(raw), parsed.PageSize);
    }

    // ────────── Token round-trip + cross-caller rejection ───────────────────

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_caller()
    {
        var fp = TenantConfigPagination.Fingerprint(CallerTenant);
        var encoded = ContinuationToken.Encode("rawAzure", CallerTenant, fp);

        var ok = TenantConfigPagination.TryAcceptContinuation(
            encoded, CallerTenant, out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_replayed_by_a_different_caller()
    {
        // A deep-page bookmark issued for caller A must not be replayable by caller B.
        var fp = TenantConfigPagination.Fingerprint(CallerTenant);
        var encoded = ContinuationToken.Encode("rawAzure", CallerTenant, fp);

        var ok = TenantConfigPagination.TryAcceptContinuation(
            encoded, CallerTenantOther, out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_garbage_token()
    {
        var ok = TenantConfigPagination.TryAcceptContinuation(
            "not-a-real-token", CallerTenant, out _, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    // ────────── BuildNextLink ───────────────────────────────────────────────

    [Fact]
    public void BuildNextLink_targets_config_all_with_pageSize_and_escaped_continuation()
    {
        var link = TenantConfigPagination.BuildNextLink(100, "TOKEN+/=", fields: null);

        Assert.StartsWith("/api/config/all?", link);
        Assert.Contains("pageSize=100", link);
        Assert.Contains("continuation=TOKEN%2B%2F%3D", link);
        Assert.DoesNotContain("fields=", link);
    }

    [Fact]
    public void BuildNextLink_echoes_fields_so_the_projection_round_trips()
    {
        var link = TenantConfigPagination.BuildNextLink(100, "abc", fields: "tenantId,domainName");

        Assert.Contains("pageSize=100", link);
        Assert.Contains("continuation=abc", link);
        Assert.Contains("fields=tenantId%2CdomainName", link);
    }
}
