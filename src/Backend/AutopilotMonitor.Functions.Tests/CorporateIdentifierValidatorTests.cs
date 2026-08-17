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
/// Graph contract tests for <see cref="CorporateIdentifierValidator"/>. WDP regression
/// (2026-08-17, first live Device Preparation enrollment): admins following the Device
/// Preparation guidance upload SERIAL-type corporate identifiers, but the validator only
/// searched the manufacturerModelSerial type — every WDP device 403'd as "not registered".
/// The validator must probe both identifier types in a single searchExistingIdentities call.
/// </summary>
public class CorporateIdentifierValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Serial = "7801-5131-4473-3387-5637-4002-18";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public string? LastRequestBody { get; private set; }

        public CapturingHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
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
    public async Task ValidateAsync_sendsBothIdentifierTypes_inOneSearchCall()
    {
        var (sut, handler) = BuildSut("""{"value":[]}""");

        await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.NotNull(handler.LastRequestBody);
        var candidates = (JArray)JObject.Parse(handler.LastRequestBody!)["importedDeviceIdentities"]!;
        Assert.Collection(candidates,
            mms =>
            {
                Assert.Equal("manufacturerModelSerial", mms["importedDeviceIdentityType"]?.ToString());
                Assert.Equal($"Microsoft Corporation,Virtual Machine,{Serial}", mms["importedDeviceIdentifier"]?.ToString());
            },
            serialOnly =>
            {
                Assert.Equal("serialNumber", serialOnly["importedDeviceIdentityType"]?.ToString());
                Assert.Equal(Serial, serialOnly["importedDeviceIdentifier"]?.ToString());
            });
    }

    [Fact]
    public async Task ValidateAsync_serialNumberTypeMatch_isValid()
    {
        // The WDP case: only a serial-type corporate identifier exists in Intune.
        var (sut, _) = BuildSut($$"""
            {"value":[{"importedDeviceIdentityType":"serialNumber","importedDeviceIdentifier":"{{Serial}}"}]}
            """);

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(Serial, result.Identifier);
    }

    [Fact]
    public async Task ValidateAsync_manufacturerModelSerialMatch_staysValid()
    {
        var identifier = $"Microsoft Corporation,Virtual Machine,{Serial}";
        var (sut, _) = BuildSut($$"""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{identifier}}"}]}
            """);

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.Equal(identifier, result.Identifier);
    }

    [Fact]
    public async Task ValidateAsync_noMatch_isDefinitive_andNamesBothSearchedForms()
    {
        var (sut, _) = BuildSut("""{"value":[]}""");

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        // The message must expose both searched identifier forms so a 403 is diagnosable
        // from the rejection log alone (the 2026-08-17 WDP session was not).
        Assert.Contains($"Microsoft Corporation,Virtual Machine,{Serial}", result.ErrorMessage);
        Assert.Contains($"serialNumber '{Serial}'", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_graphError_isTransient()
    {
        var (sut, _) = BuildSut("""{"error":{"code":"InternalServerError"}}""", HttpStatusCode.InternalServerError);

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.True(result.IsTransient);
    }
}
