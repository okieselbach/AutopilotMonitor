using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

internal static class TestKeys
{
    public static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static byte[] Decode(string base64Url) => System.Buffers.Text.Base64Url.DecodeFromChars(base64Url.AsSpan());

    /// <summary>A fresh user-agent key pair as a browser would create it: the 65-byte point plus the private key.</summary>
    public static (byte[] Point, ECDiffieHellman Key) UserAgentKey()
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (UncompressedPoint(key), key);
    }

    public static byte[] UncompressedPoint(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(false);
        var point = new byte[65];
        point[0] = 0x04;
        VapidKey.LeftPad(parameters.Q.X!, point.AsSpan(1, 32));
        VapidKey.LeftPad(parameters.Q.Y!, point.AsSpan(33, 32));
        return point;
    }

    public static WebPushSubscription Subscription(string endpoint = "https://web.push.apple.com/QGxxxxxxxxxxxxxxxxxxxx")
    {
        var (point, key) = UserAgentKey();
        key.Dispose();
        return new WebPushSubscription(new Uri(endpoint), point, RandomNumberGenerator.GetBytes(16));
    }
}

internal sealed record CapturedRequest(Uri Uri, byte[] Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>Scripted handler: one scripted answer per request, in order; records every request it saw.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();

    public List<CapturedRequest> Requests { get; } = new();

    public FakeHandler Respond(HttpStatusCode status, string? body = null, TimeSpan? retryAfter = null)
    {
        _script.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? string.Empty) };
            if (retryAfter is { } delta)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
            }

            return response;
        });
        return this;
    }

    public FakeHandler Throw(Exception exception)
    {
        _script.Enqueue(_ => throw exception);
        return this;
    }

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        Requests.Add(new CapturedRequest(request.RequestUri!, body, headers));
        cancellationToken.ThrowIfCancellationRequested();
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("No scripted response left.");
        }

        return _script.Dequeue()(request);
    }
}

/// <summary>Clock under test control; timers fire immediately and record the requested delay.</summary>
internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = new();

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        callback(state);
        return new CompletedTimer();
    }

    private sealed class CompletedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Stands in for the host's SsrfException; the sender matches the type by name.</summary>
internal sealed class SsrfException : Exception
{
    public SsrfException(string message) : base(message)
    {
    }
}
