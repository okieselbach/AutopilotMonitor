using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Graph contract tests for <see cref="CorporateIdentifierValidator"/>. Field case 2026-08-17
/// (first live Device Preparation enrollment): searchExistingIdentities came back 403 because
/// the app registration lacked DeviceManagementServiceConfig.ReadWrite.All — classified as
/// transient, this trapped the agent in an endless 503 Retry-After loop. Graph 401/403 must be
/// a DEFINITIVE failure with an actionable message. Windows-wise only the manufacturerModelSerial
/// identifier type is searched (serial-only entries are an Intune-side misconfiguration for
/// Windows and deliberately do not authorize).
/// </summary>
public class CorporateIdentifierValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Serial = "7801-5131-4473-3387-5637-4002-18";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public string? LastRequestBody { get; private set; }
        public int RequestCount { get; private set; }

        public CapturingHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _response;
        }
    }

    private static (CorporateIdentifierValidator Sut, CapturingHandler Handler) BuildSut(
        string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(new HttpResponseMessage(status)
        {
            Content = new StringContent(responseJson),
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntraId:ClientId"] = "aaaaaaaa-0000-0000-0000-000000000001",
            ["EntraId:ClientSecret"] = "secret",
        }).Build();
        var registry = new EntraAppRegistry(configuration, NullLogger<EntraAppRegistry>.Instance);
        var tenantConfig = new Mock<TenantConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, cache)
        { CallBase = false };
        var tokenService = new Mock<GraphTokenService>(
            NullLogger<GraphTokenService>.Instance, Mock.Of<IHttpClientFactory>(), cache,
            configuration, registry, tenantConfig.Object)
        { CallBase = false };
        tokenService.Setup(t => t.GetAccessTokenAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GraphTokenResult.Success("token"));

        var sut = new CorporateIdentifierValidator(
            NullLogger<CorporateIdentifierValidator>.Instance, factory.Object, cache, tokenService.Object);
        return (sut, handler);
    }

    [Fact]
    public async Task ValidateAsync_searchesOnlyManufacturerModelSerial()
    {
        // Serial-only corporate identifiers are not supported for Windows — a serial-type
        // entry is an Intune-side misconfiguration and must NOT be probed as authorization.
        var (sut, handler) = BuildSut("""{"value":[]}""");

        await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.NotNull(handler.LastRequestBody);
        var candidates = (JArray)JObject.Parse(handler.LastRequestBody!)["importedDeviceIdentities"]!;
        var only = Assert.Single(candidates);
        Assert.Equal("manufacturerModelSerial", only["importedDeviceIdentityType"]?.ToString());
        Assert.Equal($"Microsoft Corporation,Virtual Machine,{Serial}", only["importedDeviceIdentifier"]?.ToString());
    }

    [Fact]
    public async Task ValidateAsync_manufacturerModelSerialMatch_isValid()
    {
        var identifier = $"Microsoft Corporation,Virtual Machine,{Serial}";
        var (sut, _) = BuildSut($$"""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{identifier}}"}]}
            """);

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(identifier, result.Identifier);
    }

    [Fact]
    public async Task ValidateAsync_noMatch_isDefinitive()
    {
        var (sut, _) = BuildSut("""{"value":[]}""");

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Contains($"Microsoft Corporation,Virtual Machine,{Serial}", result.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ValidateAsync_graphPermissionDenied_isDefinitive_withActionableMessage(HttpStatusCode status)
    {
        // The 2026-08-17 field case: missing DeviceManagementServiceConfig.ReadWrite.All consent.
        // Transient classification would loop the agent on 503 Retry-After forever.
        var (sut, _) = BuildSut("""{"error":{"code":"Forbidden","message":"Application is not authorized"}}""", status);

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Contains("DeviceManagementServiceConfig.ReadWrite.All", result.ErrorMessage);
        Assert.Contains("consent", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_graphPermissionDenied_isCached_notRetried()
    {
        // Definitive permission failures are negative-cached so an agent retry storm does not
        // hammer Graph while consent is being fixed; the in-call retry loop must not fire either.
        var (sut, handler) = BuildSut("""{"error":{"code":"Forbidden"}}""", HttpStatusCode.Forbidden);

        await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);
        var second = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(second.IsValid);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ValidateAsync_graphServerError_isTransient()
    {
        var (sut, _) = BuildSut("""{"error":{"code":"InternalServerError"}}""", HttpStatusCode.InternalServerError);

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.True(result.IsTransient);
    }
}
