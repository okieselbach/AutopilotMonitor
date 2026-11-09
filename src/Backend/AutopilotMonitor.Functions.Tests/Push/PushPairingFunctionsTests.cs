using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Push;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The 403 a refused pairing answers with: neutral, naming the role the scope needs — the member
/// list of the tenant on the tenant routes, the Global Administrator role on the platform routes.
/// Plus the label bodies of the portal routes: optional on confirm, required on rename.
/// </summary>
public class PushPairingFunctionsTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task A_refused_tenant_pairing_names_the_member_list()
    {
        var sut = RefusingFunctions();

        var response = await sut.Create(EndpointHarness.Request(TenantId).Req);

        await AssertForbiddenAsync(response, "Push notifications need an Admin or Operator role in this tenant's member list.");
    }

    [Fact]
    public async Task A_refused_platform_pairing_names_the_global_administrator_role()
    {
        var sut = RefusingFunctions();

        var response = await sut.CreatePlatform(EndpointHarness.Request(TenantId).Req);

        await AssertForbiddenAsync(response, "Push notifications on the platform need the Global Administrator role.");
    }

    [Theory]
    [InlineData(null, "iPhone")]                                // a portal without the name field sends no body
    [InlineData("{\"label\":\"\"}", "iPhone")]
    [InlineData("{\"label\":\"iPhone Otto\"}", "iPhone Otto")]
    public async Task Confirm_reads_an_optional_name(string? body, string expectedLabel)
    {
        var h = new PushPairingServiceTests.Harness();
        var sut = new PushPairingFunctions(h.Sut, NullLogger<PushPairingFunctions>.Instance);
        var pairingId = await RedeemedPairingAsync(h, sut);

        var response = await sut.Confirm(EndpointHarness.Request(TenantId, jsonBody: body).Req, pairingId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var device = h.Repo.Devices.Values.Single();
        Assert.Equal(Constants.Push.DeviceStatus.Active, device.Status);
        Assert.Equal(expectedLabel, device.Label);
    }

    [Fact]
    public async Task Rename_answers_204_and_maps_a_blank_name_to_400_and_an_unknown_device_to_404()
    {
        var h = new PushPairingServiceTests.Harness();
        var pairing = new PushPairingFunctions(h.Sut, NullLogger<PushPairingFunctions>.Instance);
        var pairingId = await RedeemedPairingAsync(h, pairing);
        await pairing.Confirm(EndpointHarness.Request(TenantId).Req, pairingId);
        var deviceId = h.Repo.Devices.Values.Single().DeviceId;
        var sut = new PushDevicesFunctions(h.Sut, NullLogger<PushDevicesFunctions>.Instance);

        var renamed = await sut.Rename(EndpointHarness.Request(TenantId, jsonBody: "{\"label\":\"iPhone IT\"}").Req, deviceId);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal("iPhone IT", h.Repo.Devices.Values.Single().Label);

        var blank = await sut.Rename(EndpointHarness.Request(TenantId, jsonBody: "{\"label\":\"  \"}").Req, deviceId);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        var noBody = await sut.Rename(EndpointHarness.Request(TenantId).Req, deviceId);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal("iPhone IT", h.Repo.Devices.Values.Single().Label);

        var unknown = await sut.Rename(EndpointHarness.Request(TenantId, jsonBody: "{\"label\":\"x\"}").Req, "unknownDevice");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>A pairing created through the tenant route by the harness caller and redeemed by a device named "iPhone".</summary>
    private static async Task<string> RedeemedPairingAsync(PushPairingServiceTests.Harness h, PushPairingFunctions sut)
    {
        var created = await sut.Create(EndpointHarness.Request(TenantId).Req);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        created.Body.Position = 0;
        var pairing = (await JsonSerializer.DeserializeAsync<CreatePairingResponse>(created.Body, ApiJsonOptions.Read))!;
        Assert.True((await h.Sut.RedeemAsync(h.RedeemRequest(pairing.Code, label: "iPhone"))).Ok);
        return pairing.PairingId;
    }

    private static PushPairingFunctions RefusingFunctions()
    {
        var pairing = new PushPairingServiceTests.Harness();
        pairing.Eligibility.Setup(e => e.IsEligibleAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        return new PushPairingFunctions(pairing.Sut, NullLogger<PushPairingFunctions>.Instance);
    }

    private static async Task AssertForbiddenAsync(HttpResponseData response, string expectedMessage)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        response.Body.Position = 0;
        var envelope = (await JsonSerializer.DeserializeAsync<ApiErrorResponse>(response.Body, ApiJsonOptions.Read))!;
        Assert.Equal(Constants.ApiErrorCodes.PushNotEligible, envelope.Code);
        Assert.Equal(expectedMessage, envelope.Error);
    }
}
