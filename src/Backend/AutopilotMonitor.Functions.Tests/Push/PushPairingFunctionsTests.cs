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
