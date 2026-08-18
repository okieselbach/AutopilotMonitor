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
    // The form Intune actually stores (field-verified 2026-08-17 in the portal list):
    // uppercased, spaces and dashes stripped per component.
    private const string StoredIdentifier = "MICROSOFTCORPORATION,VIRTUALMACHINE,78015131447333875637400218";
    private const string StoredSerial = "78015131447333875637400218";

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
    public async Task ValidateAsync_usesGet_withContainsFilterOnNormalizedSerial()
    {
        var (sut, handler) = BuildSut(Json("""{"value":[]}"""));

        await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        var url = request.RequestUri!.ToString();
        Assert.Contains("/beta/deviceManagement/importedDeviceIdentities", url);
        Assert.DoesNotContain("searchExistingIdentities", url);
        // The stored value is normalized (dashless), so filtering with the raw dashed serial
        // could never hit — the filter must use the normalized serial.
        Assert.Contains($"contains(importedDeviceIdentifier,'{StoredSerial}')", Uri.UnescapeDataString(url));
    }

    [Fact]
    public async Task ValidateAsync_storedNormalizedIdentity_matchesRawDeviceValues()
    {
        // THE field case: device reports "Microsoft Corporation" / "Virtual Machine" / dashed
        // serial; Intune stores "MICROSOFTCORPORATION,VIRTUALMACHINE,<dashless>". Must match.
        var (sut, _) = BuildSut(Json($$"""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{StoredIdentifier}}"}]}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Equal(MmsIdentifier, result.Identifier);
    }

    [Fact]
    public async Task ValidateAsync_rawStoredIdentity_alsoMatches()
    {
        // Entries created directly via Graph may be stored raw — both sides are normalized
        // before comparison, so these match too.
        var (sut, _) = BuildSut(Json($$"""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{MmsIdentifier}}"}]}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_differentDevice_doesNotMatch()
    {
        var (sut, _) = BuildSut(Json("""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"MICROSOFTCORPORATION,VIRTUALMACHINE,39260126318839370830655125"}]}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public async Task ValidateAsync_serialOnlyIdentity_doesNotAuthorize()
    {
        // Serial-type corporate identifiers are an Intune-side misconfiguration for Windows,
        // deliberately not treated as authorization (decision 2026-08-17).
        var (sut, _) = BuildSut(Json($$"""
            {"value":[{"importedDeviceIdentityType":"serialNumber","importedDeviceIdentifier":"{{StoredSerial}}"}]}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public async Task ValidateAsync_followsNextLink_untilMatch()
    {
        var (sut, handler) = BuildSut(
            Json("""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"DELL,XPS,OTHER1"}],"@odata.nextLink":"https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities?$skiptoken=abc"}"""),
            Json($$"""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{StoredIdentifier}}"}]}"""));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("skiptoken", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task ValidateAsync_noMatch_isDefinitive_andShowsBothForms()
    {
        var (sut, _) = BuildSut(Json("""{"value":[]}"""));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        // Both the raw and the normalized form must appear so a rejection log is diagnosable
        // against the portal list (which shows only the normalized form).
        Assert.Contains(MmsIdentifier, result.ErrorMessage);
        Assert.Contains(StoredIdentifier, result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_filterRejected_fallsBackToUnfilteredScan()
    {
        // Some Intune endpoints reject $filter with 400 — the validator must then page the
        // unfiltered list instead of failing.
        var (sut, handler) = BuildSut(
            Json("""{"error":{"code":"BadRequest","message":"filter not supported"}}""", HttpStatusCode.BadRequest),
            Json($$"""{"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"{{StoredIdentifier}}"}]}"""));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.True(result.IsValid);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("$filter", handler.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("$filter", handler.Requests[1].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("Microsoft Corporation", "MICROSOFTCORPORATION")]
    [InlineData("Virtual Machine", "VIRTUALMACHINE")]
    [InlineData("7801-5131-4473-3387-5637-4002-18", "78015131447333875637400218")]
    [InlineData("ThinkPad T14 Gen 3", "THINKPADT14GEN3")]
    [InlineData("Serial.With.Periods", "SERIALWITHPERIODS")]
    public void NormalizeComponent_matchesIntunePortalNormalization(string raw, string expected)
    {
        Assert.Equal(expected, CorporateIdentifierValidator.NormalizeComponent(raw));
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

    [Theory]
    [InlineData(null, "Virtual Machine", Serial)]
    [InlineData("Microsoft Corporation", null, Serial)]
    [InlineData("Microsoft Corporation", "Virtual Machine", null)]
    [InlineData("", "Virtual Machine", Serial)]
    [InlineData("Microsoft Corporation", "", Serial)]
    [InlineData("Microsoft Corporation", "Virtual Machine", "")]
    [InlineData("   ", "Virtual Machine", Serial)]
    [InlineData("Microsoft Corporation", "Virtual Machine", "   ")]
    public async Task ValidateAsync_missingHeaderComponent_isDefinitive_withoutGraphCall(
        string? manufacturer, string? model, string? serial)
    {
        // Agents that fail to read a hardware property send the header empty — that is a
        // definitive rejection (retries cannot conjure the value), and Graph must not be hit.
        var (sut, handler) = BuildSut(Json("""{"value":[]}"""));

        var result = await sut.ValidateAsync(TenantId, manufacturer, model, serial);

        Assert.False(result.IsValid);
        Assert.False(result.IsTransient);
        Assert.Contains("not provided", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ValidateAsync_specialCharsInSerial_neverReachOdataFilter()
    {
        // The contains() narrowing uses the NORMALIZED (alphanumeric-only) serial, so an
        // OData-breaking quote in a raw serial can neither corrupt nor inject into the filter.
        const string hostileSerial = "O'Brien-123'&$top=9999";
        var (sut, handler) = BuildSut(Json("""{"value":[]}"""));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", hostileSerial);

        Assert.False(result.IsValid);
        var url = Uri.UnescapeDataString(Assert.Single(handler.Requests).RequestUri!.ToString());
        Assert.Contains("contains(importedDeviceIdentifier,'OBRIEN123TOP9999')", url);
        Assert.DoesNotContain("O'Brien", url);
    }

    [Fact]
    public async Task ValidateAsync_pageCapExceeded_isTransient_andNotCached()
    {
        // Stopping mid-list must NOT be classified "not found" — a cached false negative would
        // block a legitimately registered device for the negative-cache TTL.
        var (sut, handler) = BuildSut(Json("""
            {"value":[{"importedDeviceIdentityType":"manufacturerModelSerial","importedDeviceIdentifier":"DELL,XPS,OTHER1"}],
             "@odata.nextLink":"https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities?$skiptoken=more"}
            """));

        var result = await sut.ValidateAsync(TenantId, "Microsoft Corporation", "Virtual Machine", Serial);

        Assert.False(result.IsValid);
        Assert.True(result.IsTransient);
        Assert.Contains("page budget", result.ErrorMessage);
        // Filtered scan pages up to its cap of 5, and the in-call retry runs a second sweep —
        // proof the transient path went through the retry loop instead of caching.
        Assert.Equal(10, handler.Requests.Count);
    }
}
