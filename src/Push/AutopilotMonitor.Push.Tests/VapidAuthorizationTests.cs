using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

public class VapidAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Endpoint = new("https://web.push.apple.com/QGxxxxxxxxxxxxxxxx?token=abc");
    private const string Subject = "mailto:push@contoso.invalid";

    private static (string Token, string PublicKey) Split(string headerValue)
    {
        Assert.StartsWith("vapid t=", headerValue);
        var parts = headerValue["vapid ".Length..].Split(", ");
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("t=", parts[0]);
        Assert.StartsWith("k=", parts[1]);
        return (parts[0][2..], parts[1][2..]);
    }

    private static JsonDocument DecodeSegment(string segment) => JsonDocument.Parse(TestKeys.Decode(segment));

    [Fact]
    public void Header_carries_a_three_part_ES256_JWT_and_the_public_key()
    {
        using var key = VapidKey.Generate();
        var sut = new VapidAuthorization();

        var (token, publicKey) = Split(sut.BuildHeaderValue(Endpoint, key, Subject, Now));

        Assert.Equal(key.PublicKeyBase64Url, publicKey);
        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);

        using var header = DecodeSegment(segments[0]);
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(2, header.RootElement.EnumerateObject().Count());

        using var claims = DecodeSegment(segments[1]);
        Assert.Equal("https://web.push.apple.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(Now.AddHours(24).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal(Subject, claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(3, claims.RootElement.EnumerateObject().Count());

        var signature = TestKeys.Decode(segments[2]);
        Assert.Equal(64, signature.Length);
        var point = key.PublicKeyBytes.ToArray();
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
        var signingInput = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
        Assert.True(verifier.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/wp/abc", "https://fcm.googleapis.com")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc?x=1#f", "https://updates.push.services.mozilla.com")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=abc", "https://wns2-par02p.notify.windows.com")]
    [InlineData("https://push.example.net:8443/push/abc", "https://push.example.net:8443")]
    [InlineData("https://Push.Example.NET:443/push/abc", "https://push.example.net")]
    public void Audience_is_the_origin_only(string endpoint, string expectedAudience)
    {
        using var key = VapidKey.Generate();
        var (token, _) = Split(new VapidAuthorization().BuildHeaderValue(new Uri(endpoint), key, Subject, Now));
        using var claims = DecodeSegment(token.Split('.')[1]);
        Assert.Equal(expectedAudience, claims.RootElement.GetProperty("aud").GetString());
    }

    [Fact]
    public void Token_is_reused_for_the_same_audience_and_kid_until_12_hours_remain_then_minted_again()
    {
        using var key = VapidKey.Generate();
        var sut = new VapidAuthorization();

        var first = sut.BuildHeaderValue(Endpoint, key, Subject, Now);
        var sameAudienceOtherPath = sut.BuildHeaderValue(new Uri("https://web.push.apple.com/other"), key, Subject, Now.AddHours(11).AddMinutes(59));
        Assert.Equal(first, sameAudienceOtherPath);

        var exactlyTwelveLeft = sut.BuildHeaderValue(Endpoint, key, Subject, Now.AddHours(12));
        Assert.Equal(first, exactlyTwelveLeft);

        var reminted = sut.BuildHeaderValue(Endpoint, key, Subject, Now.AddHours(12).AddSeconds(1));
        Assert.NotEqual(first, reminted);
        using var claims = DecodeSegment(Split(reminted).Token.Split('.')[1]);
        Assert.Equal(Now.AddHours(36).AddSeconds(1).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());

        Assert.Equal(reminted, sut.BuildHeaderValue(Endpoint, key, Subject, Now.AddHours(13)));
    }

    [Fact]
    public void Cache_is_keyed_by_audience_and_kid()
    {
        using var keyA = VapidKey.Generate();
        using var keyB = VapidKey.Generate();
        var sut = new VapidAuthorization();

        var apple = sut.BuildHeaderValue(Endpoint, keyA, Subject, Now);
        var fcm = sut.BuildHeaderValue(new Uri("https://fcm.googleapis.com/wp/abc"), keyA, Subject, Now);
        var appleOtherKey = sut.BuildHeaderValue(Endpoint, keyB, Subject, Now);

        Assert.NotEqual(apple, fcm);
        Assert.NotEqual(apple, appleOtherKey);
        Assert.Equal(apple, sut.BuildHeaderValue(Endpoint, keyA, Subject, Now));
    }

    [Theory]
    [InlineData("push@contoso.invalid")]
    [InlineData("http://contoso.invalid")]
    [InlineData("")]
    public void Subject_must_be_mailto_or_https(string subject)
    {
        using var key = VapidKey.Generate();
        Assert.Throws<ArgumentException>(() => new VapidAuthorization().BuildHeaderValue(Endpoint, key, subject, Now));
    }

    [Fact]
    public void Https_subject_is_accepted()
    {
        using var key = VapidKey.Generate();
        var (token, _) = Split(new VapidAuthorization().BuildHeaderValue(Endpoint, key, "https://contoso.invalid/contact", Now));
        using var claims = DecodeSegment(token.Split('.')[1]);
        Assert.Equal("https://contoso.invalid/contact", claims.RootElement.GetProperty("sub").GetString());
    }

    [Fact]
    public void Relative_endpoint_is_refused()
    {
        using var key = VapidKey.Generate();
        Assert.Throws<ArgumentException>(() => new VapidAuthorization().BuildHeaderValue(new Uri("/push/abc", UriKind.Relative), key, Subject, Now));
    }
}
