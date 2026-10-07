using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

public class PushEndpointPolicyTests
{
    [Fact]
    public void Allowed_host_list_is_pinned()
    {
        Assert.Equal(
            new[] { ".push.apple.com", "fcm.googleapis.com", "updates.push.services.mozilla.com", ".notify.windows.com" },
            PushEndpointPolicy.AllowedHostSuffixes);
        Assert.Equal(2048, PushEndpointPolicy.MaxEndpointLength);
    }

    [Theory]
    [InlineData("https://web.push.apple.com/QGxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("https://WEB.PUSH.APPLE.COM/QGxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("https://fcm.googleapis.com/wp/dGVzdA")]
    [InlineData("https://fcm.googleapis.com/fcm/send/dGVzdA")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/dGVzdA")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=dGVzdA")]
    [InlineData("https://db5p.notify.windows.com/w/?token=dGVzdA")]
    public void Known_push_services_are_allowed(string endpoint)
    {
        Assert.Null(PushEndpointPolicy.Validate(new Uri(endpoint)));
    }

    [Theory]
    [InlineData("https://attacker.push.apple.com.evil.invalid/x", "known push service")]
    [InlineData("https://push.apple.com/x", "known push service")]
    [InlineData("https://notify.windows.com/x", "known push service")]
    [InlineData("https://evilfcm.googleapis.com/wp/x", "known push service")]
    [InlineData("https://fcm.googleapis.com.evil.invalid/wp/x", "known push service")]
    [InlineData("https://sub.updates.push.services.mozilla.com/x", "known push service")]
    [InlineData("https://contoso.invalid/push", "known push service")]
    [InlineData("http://web.push.apple.com/x", "https")]
    [InlineData("ftp://web.push.apple.com/x", "https")]
    [InlineData("https://192.0.2.10/x", "DNS name")]
    [InlineData("https://[2001:db8::1]/x", "DNS name")]
    [InlineData("https://localhost/x", "DNS name")]
    [InlineData("https://user:secret@web.push.apple.com/x", "credentials")]
    [InlineData("https://user@web.push.apple.com/x", "credentials")]
    public void Unknown_hosts_plain_http_ip_literals_and_userinfo_are_denied(string endpoint, string reasonFragment)
    {
        var error = PushEndpointPolicy.Validate(new Uri(endpoint));
        Assert.NotNull(error);
        Assert.Contains(reasonFragment, error);
        Assert.DoesNotContain(endpoint, error);
    }

    [Fact]
    public void Too_long_endpoints_are_denied()
    {
        var prefix = "https://web.push.apple.com/";
        var ok = new Uri(prefix + new string('a', 2048 - prefix.Length));
        var tooLong = new Uri(prefix + new string('a', 2049 - prefix.Length));

        Assert.Equal(2048, ok.AbsoluteUri.Length);
        Assert.Null(PushEndpointPolicy.Validate(ok));
        Assert.Equal("Endpoint is too long.", PushEndpointPolicy.Validate(tooLong));
    }

    [Fact]
    public void Relative_uri_is_denied()
    {
        Assert.Equal("Endpoint must be an absolute URL.", PushEndpointPolicy.Validate(new Uri("/push/x", UriKind.Relative)));
    }
}
