using System.Security.Cryptography;
using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

public class WebPushSubscriptionTests
{
    private const string Endpoint = "https://fcm.googleapis.com/wp/dGVzdA";

    private static (string P256dh, string Auth) ValidKeys()
    {
        var (point, key) = TestKeys.UserAgentKey();
        key.Dispose();
        return (TestKeys.Base64Url(point), TestKeys.Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public void Valid_input_parses()
    {
        var (p256dh, auth) = ValidKeys();

        Assert.True(WebPushSubscription.TryParse(Endpoint, p256dh, auth, out var subscription, out var error));
        Assert.Null(error);
        Assert.Equal(new Uri(Endpoint), subscription.Endpoint);
        Assert.Equal(65, subscription.P256dh.Length);
        Assert.Equal(0x04, subscription.P256dh.Span[0]);
        Assert.Equal(16, subscription.Auth.Length);
        Assert.Equal(TestKeys.Decode(p256dh), subscription.P256dh.ToArray());
        Assert.Equal(TestKeys.Decode(auth), subscription.Auth.ToArray());
    }

    [Fact]
    public void Padded_base64_is_tolerated()
    {
        var (p256dh, auth) = ValidKeys();
        Assert.True(WebPushSubscription.TryParse(Endpoint, p256dh, auth + "==", out _, out _));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(66)]
    [InlineData(33)]
    public void P256dh_with_wrong_length_is_rejected(int length)
    {
        var (_, auth) = ValidKeys();
        var bytes = new byte[length];
        bytes[0] = 0x04;

        Assert.False(WebPushSubscription.TryParse(Endpoint, TestKeys.Base64Url(bytes), auth, out var subscription, out var error));
        Assert.Null(subscription);
        Assert.Contains("p256dh", error);
    }

    [Fact]
    public void P256dh_without_the_0x04_prefix_is_rejected()
    {
        var (p256dh, auth) = ValidKeys();
        var bytes = TestKeys.Decode(p256dh);
        bytes[0] = 0x02;

        Assert.False(WebPushSubscription.TryParse(Endpoint, TestKeys.Base64Url(bytes), auth, out _, out var error));
        Assert.Contains("p256dh", error);
    }

    [Fact]
    public void P256dh_that_is_not_on_the_curve_is_rejected()
    {
        var (p256dh, auth) = ValidKeys();
        var bytes = TestKeys.Decode(p256dh);
        bytes[64] ^= 0x01;

        Assert.False(WebPushSubscription.TryParse(Endpoint, TestKeys.Base64Url(bytes), auth, out _, out var error));
        Assert.Contains("p256dh", error);
    }

    [Theory]
    [InlineData("not*base64url")]
    [InlineData("")]
    [InlineData(null)]
    public void P256dh_that_is_not_base64url_is_rejected(string? p256dh)
    {
        var (_, auth) = ValidKeys();
        Assert.False(WebPushSubscription.TryParse(Endpoint, p256dh, auth, out _, out var error));
        Assert.Contains("p256dh", error);
    }

    [Theory]
    [InlineData("not*base64url")]
    [InlineData("")]
    [InlineData(null)]
    public void Auth_that_is_not_base64url_is_rejected(string? auth)
    {
        var (p256dh, _) = ValidKeys();
        Assert.False(WebPushSubscription.TryParse(Endpoint, p256dh, auth, out _, out var error));
        Assert.Contains("auth secret", error);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void Auth_with_wrong_length_is_rejected(int length)
    {
        var (p256dh, _) = ValidKeys();
        Assert.False(WebPushSubscription.TryParse(Endpoint, p256dh, TestKeys.Base64Url(new byte[length]), out _, out var error));
        Assert.Equal("The auth secret must be 16 bytes.", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative/path")]
    [InlineData("https://attacker.push.apple.com.evil.invalid/x")]
    public void Endpoint_that_fails_the_policy_is_rejected(string? endpoint)
    {
        var (p256dh, auth) = ValidKeys();
        Assert.False(WebPushSubscription.TryParse(endpoint, p256dh, auth, out _, out var error));
        Assert.StartsWith("Endpoint", error);
        if (!string.IsNullOrEmpty(endpoint))
        {
            Assert.DoesNotContain(endpoint, error);
        }
    }

    [Fact]
    public void Errors_never_echo_the_input()
    {
        const string bogus = "ZZZZ*bogus-key-material";
        Assert.False(WebPushSubscription.TryParse(Endpoint, bogus, bogus, out _, out var error));
        Assert.DoesNotContain(bogus, error);
    }
}
