using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Plan v2 §5 (bootstrap-path interaction): when a tenant is mid-offboarding,
/// Phase 1 flips <c>TenantConfiguration.Disabled = true</c> and Phase 2 starts after
/// the cache-drain barrier. The auth pipeline (cert + bootstrap-token) must reject
/// requests against a Disabled tenant — otherwise a fresh OOBE device with a bootstrap
/// token could keep writing into a tenant about to be wiped.
/// <para>
/// The §0 Disabled-gate in <see cref="SecurityValidator.ValidateRequestAsync"/> runs
/// BEFORE the §0.5 bootstrap-token branch, so this property is enforced by the order
/// of checks. These tests pin that ordering so future refactors cannot accidentally
/// move the bootstrap branch above the Disabled gate.
/// </para>
/// </summary>
public sealed class BootstrapAuthDisabledTenantTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string BootstrapToken = "11111111-2222-3333-4444-555555555555";

    private static SecurityValidator BuildValidator(TenantConfiguration config)
    {
        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        var configServiceMock = new Mock<TenantConfigurationService>(
            configRepo, Mock.Of<ILogger<TenantConfigurationService>>(), cache)
        { CallBase = false };
        configServiceMock
            .Setup(x => x.TryGetConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((config, true));

        var adminConfigServiceMock = new Mock<AdminConfigurationService>(
            configRepo, Mock.Of<ILogger<AdminConfigurationService>>(), cache)
        { CallBase = false };
        adminConfigServiceMock
            .Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration());

        return new SecurityValidator(
            configService: configServiceMock.Object,
            adminConfigService: adminConfigServiceMock.Object,
            rateLimitService: null!,
            autopilotDeviceValidator: null!,
            corporateIdentifierValidator: null!,
            logger: Mock.Of<ILogger>(),
            bootstrapSessionService: null,
            deviceAssociationValidator: null);
    }

    private static HttpRequestData BuildRequestWithBootstrapToken()
    {
        var contextMock = new Mock<Microsoft.Azure.Functions.Worker.FunctionContext>();
        var reqMock = new Mock<HttpRequestData>(contextMock.Object);

        var headers = new Microsoft.Azure.Functions.Worker.Http.HttpHeadersCollection
        {
            { "X-Bootstrap-Token", BootstrapToken },
        };
        reqMock.SetupGet(r => r.Headers).Returns(headers);

        return reqMock.Object;
    }

    [Fact]
    public async Task BootstrapToken_RejectedWhenTenantDisabled()
    {
        var disabled = TenantConfiguration.CreateDefault(TenantId);
        disabled.Disabled = true;
        disabled.DisabledReason = "Offboarding in progress";
        disabled.DisabledUntil = null;

        var sut = BuildValidator(disabled);

        var result = await sut.ValidateRequestAsync(BuildRequestWithBootstrapToken(), TenantId);

        // Must be the §0 Disabled-gate, not the §0.5 bootstrap branch. The bootstrap
        // branch lives AFTER the Disabled check; if anyone ever reorders these, the
        // assertion below catches it (Disabled returns "Tenant is suspended" with the
        // configured reason; bootstrap-branch failures have different ErrorMessages).
        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Tenant is suspended", result.ErrorMessage);
        Assert.Equal("Offboarding in progress", result.Details);
    }

    [Fact]
    public async Task BootstrapToken_AcceptedWhenTenantNotDisabled_StillEvaluatesBootstrapBranch()
    {
        // Sanity: a non-disabled tenant DOES reach the bootstrap branch (which will then
        // run its own validation against _bootstrapSessionService). With _bootstrapSessionService
        // null in this test, the bootstrap branch falls through to the cert path — proving
        // that the §0 gate did NOT block.
        var enabled = TenantConfiguration.CreateDefault(TenantId);
        enabled.Disabled = false;
        enabled.ValidateAutopilotDevice = false;
        enabled.ValidateCorporateIdentifier = false;
        enabled.AllowInsecureAgentRequests = false;

        var sut = BuildValidator(enabled);

        var result = await sut.ValidateRequestAsync(BuildRequestWithBootstrapToken(), TenantId);

        // Falls through to the "device validation is required" gate at §1 (not the §0 Disabled gate).
        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Device validation is required", result.ErrorMessage);
    }
}
