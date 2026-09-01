using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the time budget of the Graph-backed device validators (<see cref="DeviceValidationBudget"/>).
/// Before it, a stuck Graph call ran into the unnamed HttpClient's 100-s default timeout, then
/// retried — a 102-s GetAgentConfig for an agent that had given up after 30 s. The chain token
/// handed in by SecurityValidator is the test seam: cancelling it stands in for the elapsed
/// budget without waiting the real seconds.
/// </summary>
public class AutopilotDeviceValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Serial = "PF3XKQ7";

    /// <summary>Scripted responses; a null response entry means "hang until the caller cancels".</summary>
    private sealed class DelayingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage?> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public DelayingHandler(params HttpResponseMessage?[] responses) => _responses = new Queue<HttpResponseMessage?>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var response = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            if (response == null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct); // observes the attempt token like a real socket
                throw new InvalidOperationException("unreachable");
            }
            return response;
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static (AutopilotDeviceValidator Sut, DelayingHandler Handler, MemoryCache Cache) BuildSut(params HttpResponseMessage?[] responses)
    {
        var handler = new DelayingHandler(responses);
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

        var sut = new AutopilotDeviceValidator(
            NullLogger<AutopilotDeviceValidator>.Instance, factory.Object, cache, tokenService.Object);
        return (sut, handler, cache);
    }

    private const string FoundBody = "{\"value\":[{\"id\":\"ap-1\",\"serialNumber\":\"PF3XKQ7\"}]}";

    [Fact]
    public async Task Budget_defaults_sit_inside_the_agents_30s_client_timeout()
    {
        Assert.True(DeviceValidationBudget.ChainBudget < TimeSpan.FromSeconds(30));
        Assert.True(DeviceValidationBudget.PerAttemptTimeout * 2 + TimeSpan.FromSeconds(2) <= DeviceValidationBudget.ChainBudget);
    }

    [Fact]
    public async Task HungGraphCall_IsCutByTheBudget_AndReportedTransient_NotCached()
    {
        var (sut, handler, cache) = BuildSut((HttpResponseMessage?)null);
        using var chain = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await sut.ValidateAutopilotDeviceAsync(TenantId, Serial, null, chain.Token);

        Assert.False(result.IsValid);
        Assert.True(result.IsTransient);
        Assert.Contains("timed out", result.ErrorMessage);
        Assert.False(cache.TryGetValue($"autopilot-device-validation:{TenantId}:{Serial}", out _));
    }

    [Fact]
    public async Task ChainBudgetExhausted_SkipsTheSecondAttempt()
    {
        // The chain token is already spent after the first hung attempt — no 2-s pause, no
        // second Graph call on a request the agent has long abandoned.
        var (sut, handler, _) = BuildSut((HttpResponseMessage?)null);
        using var chain = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await sut.ValidateAutopilotDeviceAsync(TenantId, Serial, null, chain.Token);

        Assert.True(result.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransientGraphError_ThenSuccess_RetriesOnce_WithinBudget()
    {
        var (sut, handler, _) = BuildSut(
            Json("{\"error\":{\"code\":\"UnknownError\"}}", HttpStatusCode.GatewayTimeout),
            Json(FoundBody));

        var result = await sut.ValidateAutopilotDeviceAsync(TenantId, Serial, null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("ap-1", result.AutopilotDeviceId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HealthyPath_IsUnchanged_AndCachesPositive()
    {
        var (sut, handler, cache) = BuildSut(Json(FoundBody));

        var first = await sut.ValidateAutopilotDeviceAsync(TenantId, Serial, null, CancellationToken.None);
        var second = await sut.ValidateAutopilotDeviceAsync(TenantId, Serial, null, CancellationToken.None);

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Single(handler.Requests);
        Assert.True(cache.TryGetValue($"autopilot-device-validation:{TenantId}:{Serial}", out _));
    }

    [Fact]
    public async Task TokenAcquisition_ObservesTheAttemptToken()
    {
        // GraphTokenService is where the uncancellable 5+15+30-s consent-propagation chain used
        // to live; the validator must hand its attempt token down so that chain is bounded too.
        var handler = new DelayingHandler(Json(FoundBody));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var registry = new EntraAppRegistry(configuration, NullLogger<EntraAppRegistry>.Instance);
        var tenantConfig = new Mock<TenantConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, cache)
        { CallBase = false };
        var tokenService = new Mock<GraphTokenService>(
            NullLogger<GraphTokenService>.Instance, Mock.Of<IHttpClientFactory>(), cache,
            configuration, registry, tenantConfig.Object)
        { CallBase = false };
        CancellationToken observed = default;
        tokenService.Setup(t => t.GetAccessTokenAsync(TenantId, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, ct) => observed = ct)
            .ReturnsAsync(GraphTokenResult.Success("token"));
        var sut = new AutopilotDeviceValidator(NullLogger<AutopilotDeviceValidator>.Instance, factory.Object, cache, tokenService.Object);

        await sut.ValidateAutopilotDeviceAsync(TenantId, Serial, null, CancellationToken.None);

        Assert.True(observed.CanBeCanceled);
    }
}
