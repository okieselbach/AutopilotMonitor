using System.Net;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// POST vulnerability/cpe-product-flags sets whether a product counts as self-updating and
/// answers with the value in effect and the default the seed ships. A product no mapping points
/// to is not found, and mappings that cannot be read change nothing.
/// </summary>
public class SetCpeProductFlagsFunctionTests
{
    private const string Flags = CpeMappingStore.ProductFlagsPartition;
    private const string Widget = "contoso:widget";

    private sealed class Rig
    {
        public Rig()
        {
            Tables.Put(VulnerabilityCacheRows.Mapping("contoso", "contoso widget", "cpe:2.3:a:contoso:widget", "contoso widget"));
            var store = new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance);
            Service = new CpeProductFlagsService(Tables.Repo.Object, store);
            Function = new SetCpeProductFlagsFunction(Service, NullLogger<SetCpeProductFlagsFunction>.Instance);
        }

        public MappingTables Tables { get; } = new();
        public CpeProductFlagsService Service { get; }
        public SetCpeProductFlagsFunction Function { get; }
    }

    [Fact]
    public void Route_IsRegisteredForGlobalAdminsOnly()
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy("POST", "vulnerability/cpe-product-flags");

        Assert.Equal(EndpointPolicy.GlobalAdminOnly, entry!.Policy);
    }

    [Fact]
    public async Task Post_AwayFromTheDefault_StoresTheOperatorsValueUnderTheirName_AndAnswersWithBoth()
    {
        var rig = new Rig();
        await rig.Service.ApplySeedDefaultsAsync(new[] { Widget });

        var response = await rig.Function.Run(OperatorCall.Request(
            new SetCpeProductFlagsRequest { Product = " Contoso:Widget ", SelfUpdating = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(Widget, body.GetProperty("product").GetString());
        Assert.False(body.GetProperty("selfUpdating").GetBoolean());
        Assert.True(body.GetProperty("selfUpdatingDefault").GetBoolean());

        var row = rig.Tables.Row(Flags, Widget)!;
        Assert.Equal(false, row["AdminSelfUpdating"]);
        Assert.Equal(OperatorCall.Upn, row["UpdatedBy"]);
    }

    [Fact]
    public async Task Post_ForAProductTheSeedDoesNotList_AnswersWithADefaultOfFalse()
    {
        var rig = new Rig();

        var response = await rig.Function.Run(OperatorCall.Request(
            new SetCpeProductFlagsRequest { Product = Widget, SelfUpdating = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.True(body.GetProperty("selfUpdating").GetBoolean());
        Assert.False(body.GetProperty("selfUpdatingDefault").GetBoolean());
        Assert.Equal(true, rig.Tables.Row(Flags, Widget)!["AdminSelfUpdating"]);
    }

    [Fact]
    public async Task Post_ProductNoMappingPointsTo_IsNotFound()
    {
        var rig = new Rig();

        var response = await rig.Function.Run(OperatorCall.Request(
            new SetCpeProductFlagsRequest { Product = "contoso:gadget", SelfUpdating = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.NotFound, EndpointHarness.ErrorCode(response));
        Assert.Empty(rig.Tables.Writes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "selfUpdating": true }""")]
    [InlineData("""{ "product": "   ", "selfUpdating": true }""")]
    [InlineData("""{ "product": "contoso:widget" }""")]
    [InlineData("""{ "product": "contoso:widget", "selfUpdating": null }""")]
    public async Task Post_WithoutProductOrValue_IsABadRequest(string json)
    {
        var rig = new Rig();

        var response = await rig.Function.Run(OperatorCall.RequestWithJson(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.BadRequest, EndpointHarness.ErrorCode(response));
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Post_MappingsUnreadable_IsServiceUnavailable_AndChangesNothing()
    {
        var rig = new Rig();
        rig.Tables.ReadFailure = new InvalidOperationException("storage down");

        var response = await rig.Function.Run(OperatorCall.Request(
            new SetCpeProductFlagsRequest { Product = Widget, SelfUpdating = true }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.ServiceUnavailable, EndpointHarness.ErrorCode(response));
        Assert.Equal(new[] { "60" }, response.Headers.GetValues("Retry-After"));
        Assert.Empty(rig.Tables.Writes);
    }
}
