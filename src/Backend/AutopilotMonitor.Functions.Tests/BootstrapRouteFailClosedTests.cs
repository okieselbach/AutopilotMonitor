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
/// Bootstrap routes (<c>/api/bootstrap/*</c>) are excluded from the platform's TLS client-cert
/// requirement, so nothing on those paths proves an mTLS handshake ever happened. The bootstrap
/// token must therefore be the ONLY credential accepted there — a request that reaches the
/// certificate stage on a bootstrap route would authenticate from header bytes alone (a public
/// certificate is replayable without its private key). Live probe 2026-08-29: an empty
/// <c>X-Bootstrap-Token</c> passed the function gate, skipped the token branch and had a
/// client-supplied certificate header parsed. These tests pin the fail-closed behaviour.
/// </summary>
public sealed class BootstrapRouteFailClosedTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string BootstrapRoute = "https://example.invalid/api/bootstrap/config";
    private const string AgentRoute = "https://example.invalid/api/agent/telemetry";

    // A well-formed DER certificate is not needed: what matters is whether the certificate stage
    // ran at all, and the "No certificate provided" vs. parse-error details tell the two apart.
    private const string JunkCertBase64 = "QUJD";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BootstrapRoute_TokenMissingOrEmpty_Rejected401_BeforeCertificateStage(string? token)
    {
        var sut = BuildValidator(bootstrapService: null);
        var req = BuildRequest(BootstrapRoute, token, ("X-ARR-ClientCert", JunkCertBase64));

        var result = await sut.ValidateRequestAsync(req, TenantId);

        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("X-Bootstrap-Token header is required", result.ErrorMessage);
    }

    [Fact]
    public async Task BootstrapRoute_ClientSuppliedCertificateHeader_NeverParsed()
    {
        var sut = BuildValidator(bootstrapService: null);
        var req = BuildRequest(BootstrapRoute, token: "", ("X-Client-Certificate", JunkCertBase64));

        var result = await sut.ValidateRequestAsync(req, TenantId);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("X-Bootstrap-Token header is required", result.ErrorMessage);
        Assert.DoesNotContain("certificate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentRoute_ClientCertificateHeader_IsIgnored()
    {
        // The removed X-Client-Certificate fallback: on a platform-enforced route the only
        // certificate source is X-ARR-ClientCert. A client-writable header must read as "no cert".
        var sut = BuildValidator(bootstrapService: null);
        var req = BuildRequest(AgentRoute, token: null, ("X-Client-Certificate", JunkCertBase64));

        var result = await sut.ValidateRequestAsync(req, TenantId);

        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("Invalid or missing client certificate", result.ErrorMessage);
        Assert.Equal("No certificate provided", result.Details);
    }

    [Fact]
    public async Task AgentRoute_ArrClientCertHeader_ReachesCertificateStage()
    {
        // Control: the platform-set header is still the one consulted (junk bytes → parse error,
        // proving the certificate stage ran on the supplied value).
        var sut = BuildValidator(bootstrapService: null);
        var req = BuildRequest(AgentRoute, token: null, ("X-ARR-ClientCert", JunkCertBase64));

        var result = await sut.ValidateRequestAsync(req, TenantId);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("Invalid or missing client certificate", result.ErrorMessage);
        Assert.NotEqual("No certificate provided", result.Details);
    }

    [Fact]
    public void GetBootstrapToken_TreatsEmptyAsAbsent()
    {
        Assert.Null(SecurityValidator.GetBootstrapToken(BuildRequest(BootstrapRoute, token: "")));
        Assert.Null(SecurityValidator.GetBootstrapToken(BuildRequest(BootstrapRoute, token: null)));
        Assert.Equal("abc", SecurityValidator.GetBootstrapToken(BuildRequest(BootstrapRoute, token: "abc")));
    }

    [Theory]
    [InlineData("https://example.invalid/api/bootstrap/config", true)]
    [InlineData("https://example.invalid/API/Bootstrap/register-session", true)]
    [InlineData("https://example.invalid/api/bootstrapx/config", false)]
    [InlineData("https://example.invalid/api/agent/telemetry", false)]
    public void IsBootstrapRoute_MatchesPlatformExclusionPrefix(string url, bool expected)
    {
        Assert.Equal(expected, SecurityValidator.IsBootstrapRoute(BuildRequest(url, token: null)));
    }

    // ---------------------------------------------------------------- harness

    private static SecurityValidator BuildValidator(BootstrapSessionService? bootstrapService)
    {
        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // AllowInsecureAgentRequests clears the "at least one device validator" gate so agent-route
        // requests reach the certificate stage — the stage these tests reason about.
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.AllowInsecureAgentRequests = true;

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
            rateLimitService: new RateLimitService(cache, Mock.Of<ILogger<RateLimitService>>()),
            autopilotDeviceValidator: null!,
            corporateIdentifierValidator: null!,
            logger: Mock.Of<ILogger>(),
            bootstrapSessionService: bootstrapService,
            deviceAssociationValidator: null);
    }

    private static HttpRequestData BuildRequest(string url, string? token, params (string Name, string Value)[] extraHeaders)
    {
        var contextMock = new Mock<Microsoft.Azure.Functions.Worker.FunctionContext>();
        contextMock.SetupGet(c => c.Items).Returns(new Dictionary<object, object>());
        var reqMock = new Mock<HttpRequestData>(contextMock.Object);

        var headers = new HttpHeadersCollection();
        if (token != null) headers.Add("X-Bootstrap-Token", token);
        foreach (var (name, value) in extraHeaders) headers.Add(name, value);

        reqMock.SetupGet(r => r.Headers).Returns(headers);
        reqMock.SetupGet(r => r.Url).Returns(new Uri(url));
        return reqMock.Object;
    }
}
