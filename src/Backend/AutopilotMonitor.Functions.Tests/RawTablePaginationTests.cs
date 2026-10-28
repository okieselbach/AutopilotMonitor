using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pagination wiring on <c>GET /api/global/raw/tables/{tableName}</c>. Generic
/// OData passthrough used by the <c>query_table</c> diagnostic tool. The
/// fingerprint binds tableName + filter expressions so a token from one
/// (table, filter) pair cannot be replayed against a different one.
/// </summary>
public class RawTablePaginationTests
{
    private const string Caller = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string OtherCaller = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public void ParsePagination_with_no_params_returns_default_pageSize()
    {
        var parsed = RawTablePagination.ParsePagination(new NameValueCollection());

        Assert.Null(parsed.Error);
        Assert.Equal(RawTablePagination.DefaultPageSize, parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("notanumber")]
    public void ParsePagination_rejects_invalid_pageSize(string raw)
    {
        var parsed = RawTablePagination.ParsePagination(new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_table_and_filter()
    {
        var fp = RawTablePagination.Fingerprint(
            Caller, "Sessions", partitionKey: Caller, rowKeyPrefix: null,
            customFilter: "Status eq 'Failed'");
        var encoded = ContinuationToken.Encode("rawAzure", Caller, fp);

        var ok = RawTablePagination.TryAcceptContinuation(
            encoded, Caller, "Sessions", Caller, null, "Status eq 'Failed'",
            out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_tableName_changes()
    {
        // Token issued for the `Sessions` table replayed against `Events` —
        // the underlying schema and PK semantics differ; the cursor would
        // re-seek into a structurally different result set.
        var fp = RawTablePagination.Fingerprint(Caller, "Sessions", null, null, null);
        var encoded = ContinuationToken.Encode("rawAzure", Caller, fp);

        var ok = RawTablePagination.TryAcceptContinuation(
            encoded, Caller, "Events", null, null, null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_filter_expression_changes()
    {
        var fp = RawTablePagination.Fingerprint(Caller, "Sessions", null, null, "Status eq 'Failed'");
        var encoded = ContinuationToken.Encode("rawAzure", Caller, fp);

        var ok = RawTablePagination.TryAcceptContinuation(
            encoded, Caller, "Sessions", null, null, "Status eq 'Succeeded'",
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_partition_or_rowkey_filter_changes()
    {
        var fp = RawTablePagination.Fingerprint(Caller, "Sessions", "tenant-A", null, null);
        var encoded = ContinuationToken.Encode("rawAzure", Caller, fp);

        var ok = RawTablePagination.TryAcceptContinuation(
            encoded, Caller, "Sessions", "tenant-B", null, null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_for_a_different_caller()
    {
        var fp = RawTablePagination.Fingerprint(Caller, "Sessions", null, null, null);
        var encoded = ContinuationToken.Encode("rawAzure", Caller, fp);

        var ok = RawTablePagination.TryAcceptContinuation(
            encoded, OtherCaller, "Sessions", null, null, null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void BuildNextLink_uses_table_path_and_echoes_filter_params()
    {
        var query = new NameValueCollection
        {
            { "partitionKey", "tenant-A" },
            { "filter", "Status eq 'Failed'" },
            { "limit", "999" },           // legacy → dropped
            { "pageSize", "500" },        // overwritten by the new contract
            { "continuation", "STALE" },  // overwritten by the new contract
        };

        var link = RawTablePagination.BuildNextLink(
            "Sessions", 200, "FRESH+/=", query);

        Assert.StartsWith("/api/global/raw/tables/Sessions?", link);
        Assert.Contains("pageSize=200", link);
        Assert.Contains("continuation=FRESH%2B%2F%3D", link);
        Assert.Contains("partitionKey=tenant-A", link);
        Assert.Contains("filter=Status%20eq%20%27Failed%27", link);
        Assert.DoesNotContain("limit=", link);
        Assert.DoesNotContain("STALE", link);
    }

    [Fact]
    public void BuildNextLink_echoes_fields_so_a_follow_up_page_keeps_the_projection()
    {
        var query = new NameValueCollection { { "fields", "Status,StartedAt" } };

        var link = RawTablePagination.BuildNextLink("Sessions", 200, "abc", query);

        Assert.Contains("fields=Status%2CStartedAt", link);
    }

    [Fact]
    public void BuildNextLink_url_encodes_table_name_with_special_chars()
    {
        // Table names are always alpha by convention, but defensive encoding
        // matches what TableQueryFunction does when constructing the response path.
        var link = RawTablePagination.BuildNextLink("My Table", 100, "abc", new NameValueCollection());
        Assert.StartsWith("/api/global/raw/tables/My%20Table?", link);
    }
}
