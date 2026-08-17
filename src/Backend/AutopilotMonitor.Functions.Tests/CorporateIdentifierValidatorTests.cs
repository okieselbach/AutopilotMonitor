using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Graph contract tests for <see cref="CorporateIdentifierValidator"/>. Field case 2026-08-17
/// (first live Device Preparation enrollment): the searchExistingIdentities ACTION requires
/// DeviceManagementServiceConfig.ReadWrite.All, which the app does not hold, so validation
/// 403'd in every tenant — and the transient classification trapped the agent in an endless
/// 503 Retry-After loop. The validator now READS importedDeviceIdentities (Read.All suffices),
/// matches only manufacturerModelSerial identities exactly (client-side, case-insensitive),
/// and classifies Graph 401/403 as a definitive, actionable failure.
/// </summary>
public class CorporateIdentifierValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Serial = "7801-5131-4473-3387-5637-4002-18";
    private const string MmsIdentifier = $"Microsoft Corporation,Virtual Machine,{Serial}";

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public SequenceHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            // Last response is sticky so retry loops in the SUT cannot dequeue past the script.
            var response = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static (CorporateIdentifierValidator Sut, SequenceHandler Handler) BuildSut(params HttpResponseMessage[] responses)
    {
        var handler = new SequenceHandler(responses);
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
    public async Task ValidateAsync_usesGet_withContainsFilterOnSerial()
    {
        var (sut, handler) = BuildSut(Json("""{"value":[]}"""));

        await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        var url = request.RequestUri!.ToString();
        Assert.Contains("/beta/deviceManagement/importedDeviceIdentities", url);
        Assert.DoesNotContain("searchExistingIdentities", url);
        Assert.Contains("contains(importedDeviceIdentifier", Uri.UnescapeDataString(url));
    }

    [Fact]
    public async Task ValidateAsync_mmsIdentityMatch_isValid()
    {
        var (sut, _) = BuildSut(Json($$"""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{MmsIdentifier}}"}]}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(MmsIdentifier, result.Identifier);
    }

    [Fact]
    public async Task ValidateAsync_matchIsCaseInsensitive()
    {
        var (sut, _) = BuildSut(Json($$"""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{MmsIdentifier.ToUpperInvariant()}}"}]}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_serialOnlyIdentity_doesNotAuthorize()
    {
        // Serial-type corporate identifiers are unsupported for Windows — an Intune-side
        // misconfiguration, deliberately not treated as authorization (decision 2026-08-17).
        var (sut, _) = BuildSut(Json($$"""
            {"value":[{"importedDeviceIdentityType":"serialNumber","importedDeviceIdentifier":"{{Serial}}"}]}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public async Task ValidateAsync_followsNextLink_untilMatch()
    {
        var (sut, handler) = BuildSut(
            Json("""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"Dell,XPS,OTHER1"}],"@odata.nextLink":"https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities?$skiptoken=abc"}"""),
            Json($$"""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{MmsIdentifier}}"}]}"""));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("skiptoken", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task ValidateAsync_noMatch_isDefinitive()
    {
        var (sut, _) = BuildSut(Json("""{"value":[]}"""));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Contains(MmsIdentifier, result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_filterRejected_fallsBackToUnfilteredScan()
    {
        // Some Intune endpoints reject $filter with 400 — the validator must then page the
        // unfiltered list instead of failing.
        var (sut, handler) = BuildSut(
            Json("""{"error":{"code":"BadRequest","message":"filter not supported"}}""", HttpStatusCode.BadRequest),
            Json($$"""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{MmsIdentifier}}"}]}"""));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("$filter", handler.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("$filter", handler.Requests[1].RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ValidateAsync_graphPermissionDenied_isDefinitive_withActionableMessage(HttpStatusCode status)
    {
        var (sut, _) = BuildSut(Json("""{"error":{"code":"Forbidden","message":"Application is not authorized"}}""", status));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Contains("DeviceManagementServiceConfig.Read.All", result.ErrorMessage);
        Assert.Contains("consent", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_graphPermissionDenied_isCached_notRetried()
    {
        // Definitive permission failures are negative-cached so an agent retry storm does not
        // hammer Graph while consent is being fixed; the in-call retry loop must not fire either.
        var (sut, handler) = BuildSut(Json("""{"error":{"code":"Forbidden"}}""", HttpStatusCode.Forbidden));

        await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);
        var second = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(second.IsValid);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ValidateAsync_graphServerError_isTransient()
    {
        var (sut, _) = BuildSut(Json("""{"error":{"code":"InternalServerError"}}""", HttpStatusCode.InternalServerError));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.True(result.IsTransient);
    }
}
