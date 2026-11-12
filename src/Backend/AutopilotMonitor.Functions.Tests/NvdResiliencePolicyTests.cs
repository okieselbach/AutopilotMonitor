using System.Net;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The HTTP policy in front of the NVD client (<see cref="ResiliencePolicies.NvdApi"/>) retries
/// once for a server error or a broken connection and for nothing else. A 429 has to reach the
/// client, which opens its cooldown instead of asking again, and a page that timed out is not
/// fetched a second time inside a run with a time budget.
/// </summary>
public class NvdResiliencePolicyTests
{
    /// <summary>Runs one request through a fresh policy; <paramref name="attempt"/> gets the attempt number, from 1.</summary>
    private static async Task<(HttpStatusCode? Status, Exception? Error, int Attempts)> SendAsync(
        Func<int, HttpResponseMessage> attempt)
    {
        var policy = new ResiliencePolicies(NullLoggerFactory.Instance).NvdApi;
        var attempts = 0;
        try
        {
            var response = await policy.ExecuteAsync(() => Task.FromResult(attempt(++attempts)));
            return (response.StatusCode, null, attempts);
        }
        catch (Exception ex)
        {
            return (null, ex, attempts);
        }
    }

    [Fact]
    public async Task Refusal_IsNotRetried()
    {
        var (status, _, attempts) = await SendAsync(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Timeout_IsNotRetried()
    {
        var (_, error, attempts) = await SendAsync(_ => throw new TaskCanceledException("the request ran out of time"));

        Assert.IsType<TaskCanceledException>(error);
        Assert.Equal(1, attempts);
    }

    /// <summary>Both cases wait out the policy's pause before the retry, so they run side by side.</summary>
    [Fact]
    public async Task ServerErrorOrBrokenConnection_IsRetriedExactlyOnce()
    {
        var serverError = SendAsync(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var brokenConnection = SendAsync(n =>
            n == 1 ? throw new HttpRequestException("connection reset") : new HttpResponseMessage(HttpStatusCode.OK));

        var afterServerError = await serverError;
        var afterBrokenConnection = await brokenConnection;

        Assert.Equal(HttpStatusCode.BadGateway, afterServerError.Status);
        Assert.Equal(2, afterServerError.Attempts);
        Assert.Equal(HttpStatusCode.OK, afterBrokenConnection.Status);
        Assert.Equal(2, afterBrokenConnection.Attempts);
    }
}
