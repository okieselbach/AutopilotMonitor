using System.Net;
using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

public class WebPushSenderTests
{
    private const string Subject = "mailto:push@contoso.invalid";
    private static readonly PushSendOptions DefaultOptions = new(TimeSpan.FromHours(4), PushUrgency.High, "c2Vzc2lvbi10b3BpYy0xMjM0NTY3ODkw");

    private sealed class Harness : IDisposable
    {
        public FakeHandler Handler { get; } = new();
        public FakeTime Time { get; } = new();
        public VapidKey Key { get; } = VapidKey.Generate();
        public WebPushSubscription Subscription { get; } = TestKeys.Subscription();
        public byte[] Body { get; } = WebPushEncryptor.Encrypt("{\"id\":\"1\"}"u8, TestKeys.Subscription());

        public WebPushSender Sender => new(Handler.Client(), new VapidAuthorization(), Time);

        public Task<PushSendResult> SendAsync(PushSendOptions? options = null, byte[]? body = null, CancellationToken ct = default) =>
            Sender.SendAsync(Subscription, body ?? Body, options ?? DefaultOptions, Key, Subject, ct);

        public void Dispose() => Key.Dispose();
    }

    [Fact]
    public async Task Created_is_Delivered_and_the_request_carries_every_header()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.Created);

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Delivered, result.Outcome);
        Assert.Equal(201, result.StatusCode);
        Assert.Null(result.RetryAfter);
        Assert.Null(result.Reason);

        var request = Assert.Single(h.Handler.Requests);
        Assert.Equal(h.Subscription.Endpoint, request.Uri);
        Assert.Equal(h.Body, request.Body);
        Assert.Equal("14400", request.Headers["TTL"]);
        Assert.Equal("high", request.Headers["Urgency"]);
        Assert.Equal("c2Vzc2lvbi10b3BpYy0xMjM0NTY3ODkw", request.Headers["Topic"]);
        Assert.Equal("aes128gcm", request.Headers["Content-Encoding"]);
        Assert.Equal("application/octet-stream", request.Headers["Content-Type"]);
        Assert.Equal(h.Body.Length.ToString(), request.Headers["Content-Length"]);
        Assert.StartsWith("vapid t=", request.Headers["Authorization"]);
        Assert.EndsWith(", k=" + h.Key.PublicKeyBase64Url, request.Headers["Authorization"]);
    }

    [Fact]
    public async Task Topic_header_is_omitted_when_no_topic_is_set()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.Created);

        await h.SendAsync(new PushSendOptions(TimeSpan.FromMinutes(5), PushUrgency.Low));

        var request = Assert.Single(h.Handler.Requests);
        Assert.False(request.Headers.ContainsKey("Topic"));
        Assert.Equal("low", request.Headers["Urgency"]);
        Assert.Equal("300", request.Headers["TTL"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, PushOutcome.Delivered)]
    [InlineData(HttpStatusCode.NotFound, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.Gone, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PushOutcome.PayloadTooLarge)]
    [InlineData(HttpStatusCode.BadRequest, PushOutcome.ConfigError)]
    [InlineData(HttpStatusCode.Unauthorized, PushOutcome.ConfigError)]
    [InlineData(HttpStatusCode.Forbidden, PushOutcome.ConfigError)]
    [InlineData(HttpStatusCode.MethodNotAllowed, PushOutcome.Failed)]
    [InlineData((HttpStatusCode)418, PushOutcome.Failed)]
    public async Task Non_retryable_statuses_are_classified_after_a_single_request(HttpStatusCode status, PushOutcome expected)
    {
        using var h = new Harness();
        h.Handler.Respond(status, "{\"reason\":\"x\"}");

        var result = await h.SendAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Single(h.Handler.Requests);
        Assert.Empty(h.Time.Delays);
    }

    [Fact]
    public async Task Too_many_requests_is_RetryLater_with_the_Retry_After_and_no_in_process_retry()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.TooManyRequests, "{\"reason\":\"TooManyRequests\"}", TimeSpan.FromSeconds(120));

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.RetryLater, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Equal("{\"reason\":\"TooManyRequests\"}", result.Reason);
        Assert.Single(h.Handler.Requests);
        Assert.Empty(h.Time.Delays);
    }

    [Fact]
    public async Task Server_error_is_retried_once_after_2_seconds_and_the_retry_can_succeed()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.InternalServerError).Respond(HttpStatusCode.Created);

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Delivered, result.Outcome);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, h.Time.Delays);
    }

    [Fact]
    public async Task Server_error_honours_a_Retry_After_of_at_most_30_seconds()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.ServiceUnavailable, null, TimeSpan.FromSeconds(5)).Respond(HttpStatusCode.Created);

        await h.SendAsync();

        Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, h.Time.Delays);
    }

    [Fact]
    public async Task Server_error_with_a_long_Retry_After_falls_back_to_2_seconds()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.ServiceUnavailable, null, TimeSpan.FromSeconds(60)).Respond(HttpStatusCode.Created);

        await h.SendAsync();

        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, h.Time.Delays);
    }

    [Fact]
    public async Task Two_server_errors_are_Failed_with_the_last_status()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.InternalServerError, "boom").Respond(HttpStatusCode.BadGateway, "still boom");

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("still boom", result.Reason);
        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.Single(h.Time.Delays);
    }

    [Fact]
    public async Task Ssrf_refusal_is_Failed_without_retry_and_without_the_endpoint()
    {
        using var h = new Harness();
        h.Handler.Throw(new HttpRequestException("blocked", new SsrfException("Destination resolves to a private address.")));

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal(WebPushSender.SsrfRefusedReason, result.Reason);
        Assert.Single(h.Handler.Requests);
        Assert.Empty(h.Time.Delays);
    }

    [Fact]
    public async Task Network_error_is_retried_once_then_Failed_with_a_fixed_reason()
    {
        using var h = new Harness();
        h.Handler
            .Throw(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (web.push.apple.com:443)"))
            .Throw(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal("network_error:ConnectionError", result.Reason);
        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, h.Time.Delays);
    }

    [Fact]
    public async Task Network_error_followed_by_success_is_Delivered()
    {
        using var h = new Harness();
        h.Handler.Throw(new HttpRequestException("reset")).Respond(HttpStatusCode.Created);

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Delivered, result.Outcome);
        Assert.Equal(2, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Timeout_is_retried_once_then_Failed()
    {
        using var h = new Harness();
        h.Handler
            .Throw(new TaskCanceledException("timeout", new TimeoutException()))
            .Throw(new TaskCanceledException("timeout", new TimeoutException()));

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Failed, result.Outcome);
        Assert.Equal("timeout", result.Reason);
        Assert.Equal(2, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.SendAsync(ct: cts.Token));
        Assert.Empty(h.Time.Delays);
    }

    [Fact]
    public async Task Body_above_4096_bytes_is_refused_before_any_request()
    {
        using var h = new Harness();

        var result = await h.SendAsync(body: new byte[4097]);

        Assert.Equal(PushOutcome.PayloadTooLarge, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal("payload_exceeds_4096", result.Reason);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task Body_of_exactly_4096_bytes_is_sent()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.Created);

        var result = await h.SendAsync(body: new byte[4096]);

        Assert.Equal(PushOutcome.Delivered, result.Outcome);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Reason_strips_control_characters_and_is_capped_at_200_characters()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.BadRequest, "bad\r\n\tthing\u0001" + new string('x', 300));

        var result = await h.SendAsync();

        Assert.NotNull(result.Reason);
        Assert.Equal(200, result.Reason.Length);
        Assert.StartsWith("badthing", result.Reason);
        Assert.DoesNotContain('\n', result.Reason);
    }

    [Fact]
    public async Task Empty_response_body_yields_no_reason()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.Gone, "   ");

        var result = await h.SendAsync();

        Assert.Equal(PushOutcome.Gone, result.Outcome);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Authorization_token_is_reused_across_sends_to_the_same_service()
    {
        using var h = new Harness();
        h.Handler.Respond(HttpStatusCode.Created).Respond(HttpStatusCode.Created);
        var sender = h.Sender;

        await sender.SendAsync(h.Subscription, h.Body, DefaultOptions, h.Key, Subject, CancellationToken.None);
        h.Time.UtcNow = h.Time.UtcNow.AddHours(1);
        await sender.SendAsync(h.Subscription, h.Body, DefaultOptions, h.Key, Subject, CancellationToken.None);

        Assert.Equal(h.Handler.Requests[0].Headers["Authorization"], h.Handler.Requests[1].Headers["Authorization"]);
    }
}
