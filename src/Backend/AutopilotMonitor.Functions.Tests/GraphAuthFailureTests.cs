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
/// Pins the shared 401/403 contract of the Graph-backed device validators: attempt 1 drops the
/// tenant's cached token (it may predate the consent grant on this instance) and reports
/// transient so the loop re-mints; attempt 2 and every other status leave the token alone.
/// </summary>
public class GraphAuthFailureTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static Mock<GraphTokenService> TokenService()
    {
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
        return new Mock<GraphTokenService>(
            NullLogger<GraphTokenService>.Instance, Mock.Of<IHttpClientFactory>(), cache,
            configuration, registry, tenantConfig.Object)
        { CallBase = false };
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void AuthFailure_OnFirstAttempt_InvalidatesOnce_AndAsksForRetry(HttpStatusCode status)
    {
        var tokens = TokenService();

        var retry = GraphAuthFailure.TryRecoverStaleToken(tokens.Object, NullLogger.Instance, "Test", TenantId, status, attempt: 1);

        Assert.True(retry);
        tokens.Verify(t => t.InvalidateTenant(TenantId), Times.Once);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void AuthFailure_OnSecondAttempt_LeavesTheFreshToken_AndDefersToTheValidator(HttpStatusCode status)
    {
        var tokens = TokenService();

        var retry = GraphAuthFailure.TryRecoverStaleToken(tokens.Object, NullLogger.Instance, "Test", TenantId, status, attempt: 2);

        Assert.False(retry);
        tokens.Verify(t => t.InvalidateTenant(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void OtherStatuses_NeverTouchTheToken(HttpStatusCode status)
    {
        var tokens = TokenService();

        var retry = GraphAuthFailure.TryRecoverStaleToken(tokens.Object, NullLogger.Instance, "Test", TenantId, status, attempt: 1);

        Assert.False(retry);
        Assert.False(GraphAuthFailure.IsAuthFailure(status));
        tokens.Verify(t => t.InvalidateTenant(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void PermissionMissingRetryAfter_IsLongerThanTheDefault_ButInsideTheAgentsPatience()
    {
        // 30 s is the SecurityValidator default for a transient failure; a consent gap needs more
        // than that to close, and the agent's replay logic honours any Retry-After.
        Assert.True(GraphAuthFailure.PermissionMissingRetryAfterSeconds > 30);
        Assert.True(GraphAuthFailure.PermissionMissingRetryAfterSeconds <= 300);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, 120, 120)]
    [InlineData(30, null, 30)]
    [InlineData(30, 120, 120)]
    [InlineData(120, 30, 120)]
    public void SecurityValidator_KeepsTheLargestRetryAfter(int? accumulated, int? next, int? expected)
        => Assert.Equal(expected, SecurityValidator.MaxRetryAfter(accumulated, next));
}
