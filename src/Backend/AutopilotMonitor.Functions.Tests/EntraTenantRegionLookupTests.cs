using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Tests.GraphResolution;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The tenant region comes from the sign-in metadata Microsoft Entra publishes without authentication. Every
/// failure is an answer with a reason, never an exception, and only a code-shaped value counts as a region.
/// </summary>
public class EntraTenantRegionLookupTests
{
    private const string TenantId = "6a6a35a2-30b2-4f2f-9a1b-6d9f1a2b3c4d";

    private static string Metadata(string regionMembers) =>
        "{\"issuer\":\"https://issuer.example/" + TenantId + "/v2.0\"," + regionMembers + ",\"cloud_instance_name\":\"microsoftonline.com\"}";

    private static (EntraTenantRegionLookup Sut, StubHttpMessageHandler Http) Build(HttpStatusCode status, string body)
    {
        var http = new StubHttpMessageHandler().When("/.well-known/openid-configuration", status, body);
        return (new EntraTenantRegionLookup(new StubHttpClientFactory(http)), http);
    }

    [Fact]
    public async Task Reads_the_region_from_the_tenant_specific_v2_discovery_document()
    {
        var (sut, http) = Build(HttpStatusCode.OK, Metadata("\"tenant_region_scope\":\"EU\""));

        var result = await sut.LookupAsync(TenantId, CancellationToken.None);

        Assert.Equal(new EntraTenantRegion("EU", null), result.Region);
        Assert.Null(result.Failure);
        Assert.Equal($"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration", Assert.Single(http.Requests));
    }

    [Fact]
    public async Task Reads_the_sub_scope_of_a_government_community_tenant()
    {
        var (sut, _) = Build(HttpStatusCode.OK, Metadata("\"tenant_region_scope\":\"NA\",\"tenant_region_sub_scope\":\"GCC\""));

        var result = await sut.LookupAsync(TenantId, CancellationToken.None);

        Assert.Equal(new EntraTenantRegion("NA", "GCC"), result.Region);
    }

    [Theory]
    [InlineData("\"tenant_region_scope\":null")]
    [InlineData("\"tenant_region_scope\":\"\"")]
    [InlineData("\"tenant_region_scope\":\"E\"")]
    [InlineData("\"tenant_region_scope\":\"TOOLONGCODE\"")]
    [InlineData("\"tenant_region_scope\":\"EU\\n\"")]
    [InlineData("\"tenant_region_scope\":\" EU\"")]
    [InlineData("\"tenant_region_scope\":\"<b>EU</b>\"")]
    [InlineData("\"tenant_region_scope\":42")]
    [InlineData("\"tenant_region_scope\":[\"EU\"]")]
    [InlineData("\"other\":\"EU\"")]
    public async Task A_missing_or_not_code_shaped_scope_is_no_region(string members)
    {
        var (sut, _) = Build(HttpStatusCode.OK, Metadata(members));

        var result = await sut.LookupAsync(TenantId, CancellationToken.None);

        Assert.Null(result.Region);
        Assert.Contains("tenant_region_scope", result.Failure);
    }

    [Fact]
    public async Task A_sub_scope_of_another_shape_is_dropped_but_the_scope_stays()
    {
        var (sut, _) = Build(HttpStatusCode.OK, Metadata("\"tenant_region_scope\":\"NA\",\"tenant_region_sub_scope\":\"G C C\""));

        var result = await sut.LookupAsync(TenantId, CancellationToken.None);

        Assert.Equal(new EntraTenantRegion("NA", null), result.Region);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_tenant\",\"error_description\":\"AADSTS90002: Tenant not found.\"}", "HTTP 400")]
    [InlineData(HttpStatusCode.InternalServerError, "busy", "HTTP 500")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>", "the metadata is no readable JSON object")]
    public async Task An_unusable_answer_is_a_failure_with_its_reason(HttpStatusCode status, string body, string reason)
    {
        var (sut, _) = Build(status, body);

        var result = await sut.LookupAsync(TenantId, CancellationToken.None);

        Assert.Null(result.Region);
        Assert.Equal(reason, result.Failure);
    }

    [Fact]
    public async Task A_timeout_is_a_failure_not_an_exception()
    {
        var http = new StubHttpMessageHandler().WhenTimedOut("/.well-known/openid-configuration");
        var sut = new EntraTenantRegionLookup(new StubHttpClientFactory(http));

        var result = await sut.LookupAsync(TenantId, CancellationToken.None);

        Assert.Null(result.Region);
        Assert.Equal("timed out", result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("../common")]
    public async Task A_tenant_id_that_is_no_GUID_never_leaves_the_process(string tenantId)
    {
        var (sut, http) = Build(HttpStatusCode.OK, Metadata("\"tenant_region_scope\":\"EU\""));

        var result = await sut.LookupAsync(tenantId, CancellationToken.None);

        Assert.Null(result.Region);
        Assert.Empty(http.Requests);
    }

    // ---- The one mutation both writers apply ----------------------------------------------

    [Fact]
    public void ApplyTo_declines_when_the_row_already_holds_the_pair()
    {
        var row = new TenantConfiguration { TenantId = TenantId, TenantRegionScope = "EU" };

        Assert.False(new EntraTenantRegion("EU", null).ApplyTo(row));
    }

    [Fact]
    public void ApplyTo_writes_both_codes_and_clears_a_sub_scope_Microsoft_no_longer_sends()
    {
        var row = new TenantConfiguration { TenantId = TenantId, TenantRegionScope = "NA", TenantRegionSubScope = "GCC" };

        Assert.True(new EntraTenantRegion("NA", null).ApplyTo(row));

        Assert.Equal("NA", row.TenantRegionScope);
        Assert.Null(row.TenantRegionSubScope);
    }
}
