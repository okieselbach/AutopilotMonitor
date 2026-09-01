using System;
using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The annotation list's free-text note search (<c>?q=</c>): parsed, bound into the
/// continuation fingerprint (a cursor issued for one search must not be replayed against
/// another), and echoed on nextLink so a client following the link keeps the filter.
/// </summary>
public class SessionAnnotationsPaginationTests
{
    private const string Caller = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void ParseQuery_reads_and_trims_q()
    {
        var parsed = SessionAnnotationsPagination.ParseQuery(new NameValueCollection { { "q", "  wifi switch " } });
        Assert.Null(parsed.Error);
        Assert.Equal("wifi switch", parsed.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseQuery_blank_q_is_absent(string raw)
    {
        var parsed = SessionAnnotationsPagination.ParseQuery(new NameValueCollection { { "q", raw } });
        Assert.Null(parsed.Error);
        Assert.Null(parsed.Query);
    }

    [Fact]
    public void ParseQuery_rejects_overlong_q()
    {
        var raw = new string('x', SessionAnnotationsPagination.MaxQueryLength + 1);
        var parsed = SessionAnnotationsPagination.ParseQuery(new NameValueCollection { { "q", raw } });
        Assert.NotNull(parsed.Error);
        Assert.Contains("q must be at most", parsed.Error);
    }

    [Fact]
    public void Continuation_issued_for_one_search_is_rejected_for_another()
    {
        var wifi = SessionAnnotationsPagination.ParseQuery(new NameValueCollection { { "q", "wifi" } });
        var token = SessionAnnotationsPagination.EncodeContinuation(wifi, Caller, "raw-azure-token");

        var lan = SessionAnnotationsPagination.ParseQuery(new NameValueCollection
        {
            { "q", "lan" }, { "continuation", token },
        });
        Assert.False(SessionAnnotationsPagination.TryAcceptContinuation(lan, Caller, out _, out var reason));
        Assert.NotNull(reason);

        var none = SessionAnnotationsPagination.ParseQuery(new NameValueCollection { { "continuation", token } });
        Assert.False(SessionAnnotationsPagination.TryAcceptContinuation(none, Caller, out _, out _));
    }

    [Fact]
    public void Continuation_round_trips_for_the_same_search()
    {
        var first = SessionAnnotationsPagination.ParseQuery(new NameValueCollection { { "q", "wifi" } });
        var token = SessionAnnotationsPagination.EncodeContinuation(first, Caller, "raw-azure-token");

        var next = SessionAnnotationsPagination.ParseQuery(new NameValueCollection
        {
            { "q", "wifi" }, { "continuation", token },
        });
        Assert.True(SessionAnnotationsPagination.TryAcceptContinuation(next, Caller, out var azureToken, out _));
        Assert.Equal("raw-azure-token", azureToken);
    }

    [Fact]
    public void NextLink_echoes_q_url_encoded()
    {
        var parsed = SessionAnnotationsPagination.ParseQuery(new NameValueCollection { { "q", "wifi switch & lan" } });
        var link = SessionAnnotationsPagination.BuildNextLink(parsed, "wire-token", SessionAnnotationsPagination.TenantBasePath);
        Assert.StartsWith(SessionAnnotationsPagination.TenantBasePath, link);
        Assert.Contains("&q=" + Uri.EscapeDataString("wifi switch & lan"), link);
    }

    [Fact]
    public void NextLink_omits_q_when_absent()
    {
        var parsed = SessionAnnotationsPagination.ParseQuery(new NameValueCollection());
        var link = SessionAnnotationsPagination.BuildNextLink(parsed, "wire-token");
        Assert.DoesNotContain("&q=", link);
    }
}
