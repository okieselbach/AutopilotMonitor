using System.Net;
using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// What <see cref="NvdApiClient"/> makes of an answer that is no result. NVD refusing the client,
/// with 429 from the API or 403 from the gateway in front of it, opens the process-wide cooldown
/// in which nothing more is sent. A server error, a timeout or a broken connection is no answer:
/// a caller that asks in a loop ends the loop on it. Any other refusal concerns the one request.
/// </summary>
[Collection(NvdTestState.Collection)]
public sealed class NvdApiClientRefusalTests : IDisposable
{
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string GadgetCpe = "cpe:2.3:a:fabrikam:gadget";

    public NvdApiClientRefusalTests() => NvdTestState.Reset();

    public void Dispose() => NvdTestState.Reset();

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefusalOfTheClient_IsThrottled_AndNothingIsSentWhileTheCooldownRuns(HttpStatusCode status)
    {
        var nvd = new NvdStubHandler(_ => NvdFixture.Status(status));
        var client = NvdFixture.Client(nvd);

        var refused = await client.SearchCvesByCpeAsync(WidgetCpe);

        Assert.False(refused.Succeeded);
        Assert.True(refused.Throttled);
        Assert.False(refused.Unanswered);
        Assert.Empty(refused.Data.Vulnerabilities);
        Assert.True(NvdApiClient.IsInCooldown);

        // The next product is not asked about, by this client or by another one of the process.
        var otherNvd = new NvdStubHandler(_ => NvdFixture.Page());
        var next = await client.SearchCvesByCpeAsync(GadgetCpe);
        var fromAnotherClient = await NvdFixture.Client(otherNvd).SearchCvesByCpeAsync(GadgetCpe);

        Assert.All(new[] { next, fromAnotherClient }, result =>
        {
            Assert.False(result.Succeeded);
            Assert.True(result.Throttled);
        });
        Assert.Equal(new[] { WidgetCpe }, nvd.Requests.Select(r => r.Cpe));
        Assert.Empty(otherNvd.Requests);
    }

    [Fact]
    public async Task CooldownRemaining_IsHowLongARefusalStillBlocks_AndZeroWithoutOne()
    {
        Assert.Equal(TimeSpan.Zero, NvdApiClient.CooldownRemaining);

        await NvdFixture.Client(new NvdStubHandler(_ => NvdFixture.Status(HttpStatusCode.TooManyRequests))).SearchCvesByCpeAsync(WidgetCpe);

        Assert.InRange(
            NvdApiClient.CooldownRemaining, NvdApiClient.ThrottleCooldown - TimeSpan.FromSeconds(5), NvdApiClient.ThrottleCooldown);

        NvdApiClient.ResetThrottleState();
        Assert.Equal(TimeSpan.Zero, NvdApiClient.CooldownRemaining);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerError_IsNoAnswer_AndOpensNoCooldown(HttpStatusCode status)
    {
        var nvd = new NvdStubHandler(_ => NvdFixture.Status(status));
        var client = NvdFixture.Client(nvd);

        var result = await client.SearchCvesByCpeAsync(WidgetCpe);

        Assert.False(result.Succeeded);
        Assert.False(result.Throttled);
        Assert.True(result.Unanswered);
        Assert.False(NvdApiClient.IsInCooldown);

        await client.SearchCvesByCpeAsync(GadgetCpe);
        Assert.Equal(new[] { WidgetCpe, GadgetCpe }, nvd.Requests.Select(r => r.Cpe));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task RequestNvdTurnsDown_FailsThatRequestOnly(HttpStatusCode status)
    {
        var nvd = new NvdStubHandler(request => request.Cpe == WidgetCpe ? NvdFixture.Status(status) : NvdFixture.Page());
        var client = NvdFixture.Client(nvd);

        var turnedDown = await client.SearchCvesByCpeAsync(WidgetCpe);

        Assert.False(turnedDown.Succeeded);
        Assert.False(turnedDown.Throttled);
        // NVD answered: that says nothing about the next product.
        Assert.False(turnedDown.Unanswered);
        Assert.False(NvdApiClient.IsInCooldown);

        var next = await client.SearchCvesByCpeAsync(GadgetCpe);
        Assert.True(next.Succeeded);
        Assert.Equal(new[] { WidgetCpe, GadgetCpe }, nvd.Requests.Select(r => r.Cpe));
    }

    [Theory]
    [InlineData("the connection breaks")]
    [InlineData("the request runs out of time")]
    [InlineData("the body is not what NVD sends")]
    public async Task RequestWithoutAUsableAnswer_IsUnanswered_AndOpensNoCooldown(string way)
    {
        var nvd = new NvdStubHandler((_, _) => way switch
        {
            "the connection breaks" => throw new HttpRequestException("connection reset"),
            "the request runs out of time" => throw new TaskCanceledException("the request ran out of time"),
            _ => Task.FromResult(NvdFixture.Json("<html>maintenance</html>")),
        });

        var result = await NvdFixture.Client(nvd).SearchCvesByCpeAsync(WidgetCpe);

        Assert.False(result.Succeeded);
        Assert.False(result.Throttled);
        Assert.True(result.Unanswered);
        Assert.Empty(result.Data.Vulnerabilities);
        Assert.False(NvdApiClient.IsInCooldown);
    }
}
