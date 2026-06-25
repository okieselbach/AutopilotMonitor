using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>
/// Minimal scripted HTTP handler for resolver tests. URL is matched as a substring so callers
/// can scope by route fragment (e.g. "deviceManagementScripts/abc" matches the per-ID GET).
/// First match wins; un-scripted URLs throw to force the test to declare every expected call.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(string UrlSubstring, HttpStatusCode Status, string Body)> _scripted = new();
    public List<string> Requests { get; } = new();

    public StubHttpMessageHandler When(string urlSubstring, HttpStatusCode status, string body)
    {
        _scripted.Add((urlSubstring, status, body));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        var match = _scripted.FirstOrDefault(s => url.Contains(s.UrlSubstring, StringComparison.OrdinalIgnoreCase));
        if (match.Equals(default((string, HttpStatusCode, string))))
        {
            throw new InvalidOperationException($"StubHttpMessageHandler: unscripted URL {url}");
        }
        return Task.FromResult(new HttpResponseMessage(match.Status)
        {
            Content = new StringContent(match.Body),
        });
    }
}

/// <summary>Tiny <see cref="IHttpClientFactory"/> that always returns a client wired to a stub handler.</summary>
internal sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;
    public StubHttpClientFactory(StubHttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>Factory that wires every client to one arbitrary <see cref="HttpMessageHandler"/>.</summary>
internal sealed class SingleHandlerFactory : System.Net.Http.IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// Returns a token response whose body can be swapped between calls — models AAD issuing a token
/// with a different <c>roles</c> claim after an admin grants a permission. Counts requests so a
/// test can assert a fresh acquire actually hit the wire (vs. being served from cache).
/// </summary>
internal sealed class SwappableTokenHandler : HttpMessageHandler
{
    private int _requests;
    public string Body { get; set; }
    public int Requests => System.Threading.Volatile.Read(ref _requests);

    public SwappableTokenHandler(string body) => Body = body;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        System.Threading.Interlocked.Increment(ref _requests);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"access_token\":\"{Body}\",\"expires_in\":3599}}"),
        });
    }
}

/// <summary>
/// Parks the FIRST request until <see cref="Release"/> is called (modelling a token POST that is
/// in flight while an InvalidateTenant runs), then answers it with <c>firstBody</c>; every later
/// request answers immediately with <c>laterBody</c> (the post-grant re-mint). Lets a test prove a
/// stale in-flight write is rejected and a fresh read re-mints.
/// </summary>
internal sealed class InflightRaceHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _firstBody;
    private readonly string _laterBody;
    private int _entered;

    public InflightRaceHandler(string firstBody, string laterBody)
    {
        _firstBody = firstBody;
        _laterBody = laterBody;
    }

    public int Entered => System.Threading.Volatile.Read(ref _entered);
    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = System.Threading.Interlocked.Increment(ref _entered);
        if (n == 1)
        {
            await _release.Task.ConfigureAwait(false);
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(n == 1 ? _firstBody : _laterBody),
        };
    }
}

/// <summary>
/// Parks every request until <see cref="Release"/> is called, counting how many reached the wire.
/// Lets a stampede test prove that N simultaneous cache-misses collapse to a SINGLE in-flight POST
/// — which a synchronous stub cannot show, because the first call can populate the cache before the
/// others even start.
/// </summary>
internal sealed class GatedHttpMessageHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;

    public GatedHttpMessageHandler(string body) => _body = body;
    public int Entered => System.Threading.Volatile.Read(ref _entered);
    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        System.Threading.Interlocked.Increment(ref _entered);
        await _release.Task.ConfigureAwait(false);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) };
    }
}
