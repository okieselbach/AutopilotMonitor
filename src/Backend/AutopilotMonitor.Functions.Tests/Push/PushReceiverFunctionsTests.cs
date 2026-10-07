using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Push;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The receiver's own routes over HTTP. On <c>push/device</c> the token in
/// <see cref="Constants.Push.DeviceTokenHeader"/> is the whole authority: 401 before anything else
/// (the body included), then the per-device throttle, then the route. Re-subscribe re-arms a Stale
/// row and never echoes the refused host; unpair deletes the row and the token dies with it. The
/// anonymous <c>push/pair/begin</c> gives one 404 for an unusable code and is throttled per client IP
/// (the trusted X-Forwarded-For hop, never a client-written prefix).
/// </summary>
public class PushReceiverFunctionsTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string PhoneIp = "198.51.100.7";
    private const string OtherPhoneIp = "198.51.100.8";
    private const string UnknownCode = "ZZZZZZZZZZZ";
    private static readonly NotificationScope Tenant = NotificationScope.Tenant(TenantId);
    private static readonly PushCaller Admin = new("admin@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000001", TenantId, IsScopeAdmin: true);

    // ── GET push/device ──────────────────────────────────────────────────────

    [Fact]
    public async Task Get_without_the_device_token_header_is_401_InvalidDeviceToken()
    {
        var h = new Harness();
        await h.PairAsync();

        var response = await h.Sut.Get(Harness.Request());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await ReadAsync<ApiErrorResponse>(response);
        Assert.Equal(Constants.ApiErrorCodes.InvalidDeviceToken, envelope.Code);
        Assert.Equal("Unknown device token.", envelope.Error);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData(TenantId + ".nosuchdevice.AAAA")]
    public async Task Get_with_a_token_that_names_no_device_is_401(string token)
    {
        var h = new Harness();
        await h.PairAsync();

        var response = await h.Sut.Get(Harness.Request(token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.InvalidDeviceToken, EndpointHarness.ErrorCode(response));
    }

    [Fact]
    public async Task Get_with_the_right_device_but_a_wrong_secret_is_401()
    {
        var h = new Harness();
        var (token, _) = await h.PairAsync();
        var parts = token.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{new string('A', 43)}";   // same scope and device, a secret of 32 zero bytes

        var response = await h.Sut.Get(Harness.Request(forged));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.InvalidDeviceToken, EndpointHarness.ErrorCode(response));
    }

    [Fact]
    public async Task Get_with_the_device_token_returns_the_status_and_stamps_LastOpenedUtc()
    {
        var h = new Harness();
        var (token, row) = await h.PairAsync();
        Assert.Null(row.LastOpenedUtc);

        var response = await h.Sut.Get(Harness.Request(token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await ReadAsync<PushDeviceStatusResponse>(response);
        Assert.Equal(row.DeviceId, status.DeviceId);
        Assert.Equal(Constants.Push.DeviceStatus.Active, status.Status);
        Assert.NotNull(row.LastOpenedUtc);
    }

    [Fact]
    public async Task Device_status_body_never_carries_the_subscription()
    {
        var h = new Harness();
        var (token, row) = await h.PairAsync();

        var response = await h.Sut.Get(Harness.Request(token));

        var body = await BodyTextAsync(response);
        Assert.DoesNotContain(row.Endpoint, body);
        Assert.DoesNotContain(row.Auth, body);
    }

    // ── Per-device throttle (after the token matched) ────────────────────────

    [Fact]
    public async Task Device_routes_allow_ten_calls_a_minute_then_429_with_Retry_After()
    {
        var h = new Harness();
        var (token, _) = await h.PairAsync();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await h.Sut.Get(Harness.Request(token))).StatusCode);

        var throttled = await h.Sut.Get(Harness.Request(token));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        var envelope = await ReadAsync<ApiErrorResponse>(throttled);
        Assert.Equal(Constants.ApiErrorCodes.RateLimited, envelope.Code);
        var retryAfter = int.Parse(Assert.Single(throttled.Headers.GetValues("Retry-After")));
        Assert.InRange(retryAfter, 1, 60);
        Assert.Equal(retryAfter, envelope.RetryAfterSeconds ?? -1);
    }

    [Fact]
    public async Task Device_throttle_is_keyed_per_device()
    {
        var h = new Harness();
        var (first, _) = await h.PairAsync();
        var (second, _) = await h.PairAsync();
        for (var i = 0; i <= 10; i++)
            await h.Sut.Get(Harness.Request(first));   // the 11th is the first device's 429

        var response = await h.Sut.Get(Harness.Request(second));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Device_throttle_spans_the_three_device_routes_and_a_throttled_unpair_deletes_nothing()
    {
        var h = new Harness();
        var (token, _) = await h.PairAsync();
        for (var i = 0; i < 10; i++)
            await h.Sut.Get(Harness.Request(token));

        var response = await h.Sut.Unpair(Harness.Request(token));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(h.Repo.Devices);
    }

    // ── PUT push/device ──────────────────────────────────────────────────────

    [Fact]
    public async Task Put_re_arms_a_stale_device_with_the_new_subscription()
    {
        var h = new Harness();
        var (token, row) = await h.PairAsync();
        row.Status = Constants.Push.DeviceStatus.Stale;
        row.StatusReason = "gone";
        const string newEndpoint = "https://web.push.apple.com/QF/new-token";

        var response = await h.Sut.Resubscribe(Harness.Request(token, h.ResubscribeBody(newEndpoint)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await ReadAsync<PushDeviceStatusResponse>(response);
        Assert.Equal(Constants.Push.DeviceStatus.Active, status.Status);
        Assert.Equal(Constants.Push.DeviceStatus.Active, row.Status);
        Assert.Null(row.StatusReason);
        Assert.Equal(newEndpoint, row.Endpoint);
    }

    [Fact]
    public async Task Put_with_an_unknown_push_host_is_400_InvalidSubscription_and_leaves_the_row_alone()
    {
        var h = new Harness();
        var (token, row) = await h.PairAsync();
        var endpointBefore = row.Endpoint;

        var response = await h.Sut.Resubscribe(Harness.Request(token, h.ResubscribeBody("https://attacker.invalid/push")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await ReadAsync<ApiErrorResponse>(response);
        Assert.Equal(Constants.ApiErrorCodes.InvalidSubscription, envelope.Code);
        Assert.DoesNotContain("attacker.invalid", envelope.Error);   // no response names an endpoint
        Assert.Equal(endpointBefore, row.Endpoint);
        Assert.Equal(Constants.Push.DeviceStatus.Active, row.Status);
    }

    [Fact]
    public async Task Put_checks_the_token_before_it_reads_the_body()
    {
        var h = new Harness();
        await h.PairAsync();

        var response = await h.Sut.Resubscribe(Harness.Request("garbage", "not json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.InvalidDeviceToken, EndpointHarness.ErrorCode(response));
    }

    // ── DELETE push/device ───────────────────────────────────────────────────

    [Fact]
    public async Task Delete_unpairs_the_device_and_the_token_dies_with_the_row()
    {
        var h = new Harness();
        var (token, _) = await h.PairAsync();

        var response = await h.Sut.Unpair(Harness.Request(token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(h.Repo.Devices);
        var again = await h.Sut.Unpair(Harness.Request(token));
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.InvalidDeviceToken, EndpointHarness.ErrorCode(again));
    }

    // ── POST push/pair/begin (anonymous, code-gated) ─────────────────────────

    [Fact]
    public async Task Begin_with_an_unknown_code_is_404_PairingCodeInvalid()
    {
        var h = new Harness();

        var response = await h.Sut.Begin(Harness.BeginRequest(UnknownCode, PhoneIp));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.PairingCodeInvalid, EndpointHarness.ErrorCode(response));
    }

    [Fact]
    public async Task Begin_with_a_live_code_answers_the_active_server_key()
    {
        var h = new Harness();
        var created = await h.Pairing.Sut.CreateAsync(Tenant, Admin);

        var response = await h.Sut.Begin(Harness.BeginRequest(created.Value!.Code, PhoneIp));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var begin = await ReadAsync<BeginPairResponse>(response);
        Assert.Equal(h.Pairing.Keys.Active.Kid, begin.Kid);
    }

    [Fact]
    public async Task Begin_allows_ten_attempts_per_client_ip_a_minute_then_429_with_Retry_After()
    {
        var h = new Harness();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await h.Sut.Begin(Harness.BeginRequest(UnknownCode, PhoneIp))).StatusCode);

        var throttled = await h.Sut.Begin(Harness.BeginRequest(UnknownCode, PhoneIp));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.RateLimited, EndpointHarness.ErrorCode(throttled));
        Assert.InRange(int.Parse(Assert.Single(throttled.Headers.GetValues("Retry-After"))), 1, 60);
    }

    [Fact]
    public async Task Begin_throttle_is_keyed_per_client_ip()
    {
        var h = new Harness();
        for (var i = 0; i <= 10; i++)
            await h.Sut.Begin(Harness.BeginRequest(UnknownCode, PhoneIp));   // the 11th is this IP's 429

        var response = await h.Sut.Begin(Harness.BeginRequest(UnknownCode, OtherPhoneIp));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Begin_throttle_keys_on_the_trusted_hop_not_a_client_written_prefix()
    {
        // App Service appends the real peer as the RIGHTMOST hop; anything to its left is the caller's.
        var h = new Harness();
        for (var i = 0; i < 10; i++)
            await h.Sut.Begin(Harness.BeginRequest(UnknownCode, $"203.0.113.{i}, {PhoneIp}"));

        var response = await h.Sut.Begin(Harness.BeginRequest(UnknownCode, $"203.0.113.99, {PhoneIp}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<T> ReadAsync<T>(HttpResponseData response)
    {
        response.Body.Position = 0;
        return (await JsonSerializer.DeserializeAsync<T>(response.Body, ApiJsonOptions.Read))!;
    }

    private static async Task<string> BodyTextAsync(HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// The functions over the pairing-service harness (in-memory repository, fake push transport)
    /// and a real <see cref="RateLimitService"/> — one instance per test, so the throttle buckets
    /// are the test's own.
    /// </summary>
    private sealed class Harness
    {
        public PushPairingServiceTests.Harness Pairing { get; } = new();
        public PushReceiverFunctions Sut { get; }

        public Harness()
        {
            var rateLimit = new RateLimitService(new MemoryCache(new MemoryCacheOptions()), NullLogger<RateLimitService>.Instance);
            Sut = new PushReceiverFunctions(Pairing.Sut, rateLimit, NullLogger<PushReceiverFunctions>.Instance);
        }

        public InMemoryPushDeviceRepository Repo => Pairing.Repo;

        /// <summary>create → redeem → confirm: an Active device, the token its phone holds, and the stored row (the repository mutates it in place).</summary>
        public async Task<(string Token, PushDevice Row)> PairAsync()
        {
            var created = await Pairing.Sut.CreateAsync(Tenant, Admin);
            var redeemed = await Pairing.Sut.RedeemAsync(Pairing.RedeemRequest(created.Value!.Code));
            await Pairing.Sut.ConfirmAsync(Tenant, created.Value.PairingId, Admin);
            return (redeemed.Value!.DeviceToken, Repo.Devices[(Tenant.Key, redeemed.Value.DeviceId)]);
        }

        public string ResubscribeBody(string endpoint) => TestWire.Serialize(new ResubscribeRequest
        {
            Endpoint = endpoint, P256dh = Pairing.Subscription.P256dh, Auth = Pairing.Subscription.Auth, Kid = Pairing.Keys.Active.Kid,
        });

        /// <summary>A device-token route request: the token in its header (none by default), an optional JSON body.</summary>
        public static HttpRequestData Request(string? token = null, string? body = null)
        {
            var headers = new Dictionary<string, string>();
            if (token != null)
                headers[Constants.Push.DeviceTokenHeader] = token;
            return EndpointHarness.Request(TenantId, jsonBody: body, headers: headers).Req;
        }

        /// <summary>An anonymous begin from one phone; X-Forwarded-For is the hop the IP throttle keys on.</summary>
        public static HttpRequestData BeginRequest(string code, string forwardedFor)
            => EndpointHarness.Request(TenantId, jsonBody: TestWire.Serialize(new BeginPairRequest { Code = code }),
                headers: new Dictionary<string, string> { ["X-Forwarded-For"] = forwardedFor }).Req;
    }
}
