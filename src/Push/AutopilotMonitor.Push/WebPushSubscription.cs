using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace AutopilotMonitor.Push;

/// <summary>
/// A browser push subscription as handed over by <c>PushManager.subscribe()</c>: the push
/// resource URL, the user agent's P-256 public key and the 16-byte authentication secret.
/// </summary>
public sealed record WebPushSubscription
{
    /// <summary>Length of the uncompressed user-agent public point.</summary>
    public const int P256dhLength = 65;

    /// <summary>Length of the authentication secret (RFC 8291 section 3.2).</summary>
    public const int AuthLength = 16;

    internal WebPushSubscription(Uri endpoint, byte[] p256dh, byte[] auth)
    {
        Endpoint = endpoint;
        P256dh = p256dh;
        Auth = auth;
    }

    /// <summary>The push resource URL. It is a credential: never log it or put it into a message.</summary>
    public Uri Endpoint { get; }

    /// <summary>The user agent's public point, 65 bytes starting with 0x04.</summary>
    public ReadOnlyMemory<byte> P256dh { get; }

    /// <summary>The 16-byte authentication secret.</summary>
    public ReadOnlyMemory<byte> Auth { get; }

    /// <summary>
    /// Parses the wire form (endpoint URL, base64url keys). The endpoint must pass
    /// <see cref="PushEndpointPolicy"/>. <paramref name="error"/> is a generic, user-facing
    /// sentence that never echoes the input.
    /// </summary>
    public static bool TryParse(
        string? endpoint,
        string? p256dhBase64Url,
        string? authBase64Url,
        [NotNullWhen(true)] out WebPushSubscription? subscription,
        [NotNullWhen(false)] out string? error)
    {
        subscription = null;

        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            error = "Endpoint must be an absolute URL.";
            return false;
        }

        error = PushEndpointPolicy.Validate(uri);
        if (error is not null)
        {
            return false;
        }

        if (!TryDecode(p256dhBase64Url, out var p256dh))
        {
            error = "The p256dh key is not valid base64url.";
            return false;
        }

        if (p256dh.Length != P256dhLength || p256dh[0] != 0x04 || !P256.IsOnCurve(p256dh.AsSpan(1, 32), p256dh.AsSpan(33, 32)))
        {
            error = "The p256dh key must be a 65-byte uncompressed P-256 point.";
            return false;
        }

        if (!TryDecode(authBase64Url, out var auth))
        {
            error = "The auth secret is not valid base64url.";
            return false;
        }

        if (auth.Length != AuthLength)
        {
            error = "The auth secret must be 16 bytes.";
            return false;
        }

        subscription = new WebPushSubscription(uri, p256dh, auth);
        error = null;
        return true;
    }

    /// <summary>The user agent's public key as an ECDH public key; the caller disposes it.</summary>
    internal ECDiffieHellman CreateUserAgentKey()
    {
        var point = P256dh.Span;
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33].ToArray(), Y = point[33..].ToArray() },
        });
    }

    private static bool TryDecode(string? value, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            bytes = Base64Url.DecodeFromChars(value.AsSpan());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
