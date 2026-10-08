using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AutopilotMonitor.Push;

/// <summary>
/// RFC 8291 message encryption: one <c>aes128gcm</c> (RFC 8188) record with a fresh salt and a
/// fresh ephemeral sender key per message. The output is the complete request body:
/// 86-byte header, ciphertext and 16-byte tag.
/// </summary>
public static class WebPushEncryptor
{
    /// <summary>Record size written into the header; the single record must fit into it.</summary>
    public const int RecordSize = 4096;

    /// <summary>Header length: 16-byte salt, 4-byte record size, 1-byte key id length, 65-byte sender key.</summary>
    public const int HeaderLength = 16 + 4 + 1 + VapidKey.PublicKeyLength;

    /// <summary>Tag length of AES-128-GCM.</summary>
    public const int TagLength = 16;

    /// <summary>Longest plaintext that fits a 4096-byte body: 4096 - 86 (header) - 1 (padding delimiter) - 16 (tag).</summary>
    public const int MaxPlaintextLength = RecordSize - HeaderLength - 1 - TagLength;

    private const int SaltLength = 16;
    private const byte LastRecordDelimiter = 0x02;

    private static readonly byte[] KeyInfoPrefix = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();

    /// <summary>Encrypts <paramref name="plaintext"/> for the subscription; at most <see cref="MaxPlaintextLength"/> bytes.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, WebPushSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        using var senderKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, subscription, salt, senderKey);
    }

    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, WebPushSubscription subscription, byte[] salt, ECDiffieHellman senderKey)
    {
        if (plaintext.Length > MaxPlaintextLength)
        {
            throw new ArgumentException($"The push payload must not exceed {MaxPlaintextLength} bytes.", nameof(plaintext));
        }

        var keys = DeriveKeys(subscription, salt, senderKey);

        var body = new byte[HeaderLength + plaintext.Length + 1 + TagLength];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(SaltLength, 4), RecordSize);
        body[SaltLength + 4] = VapidKey.PublicKeyLength;
        keys.SenderPublicKey.CopyTo(body, SaltLength + 5);

        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded);
        padded[^1] = LastRecordDelimiter;

        using var aes = new AesGcm(keys.Cek, TagLength);
        aes.Encrypt(
            keys.Nonce,
            padded,
            body.AsSpan(HeaderLength, padded.Length),
            body.AsSpan(HeaderLength + padded.Length, TagLength));
        return body;
    }

    /// <summary>Runs the RFC 8291 section 3.4 derivation and returns every intermediate for verification.</summary>
    internal static WebPushKeyMaterial DeriveKeys(WebPushSubscription subscription, byte[] salt, ECDiffieHellman senderKey)
    {
        if (salt.Length != SaltLength)
        {
            throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));
        }

        var senderPublic = UncompressedPoint(senderKey);
        var uaPublic = subscription.P256dh.ToArray();
        var auth = subscription.Auth.ToArray();

        byte[] ecdhSecret;
        using (var uaKey = subscription.CreateUserAgentKey())
        {
            ecdhSecret = senderKey.DeriveRawSecretAgreement(uaKey.PublicKey);
        }

        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, auth);
        var keyInfo = new byte[KeyInfoPrefix.Length + uaPublic.Length + senderPublic.Length];
        KeyInfoPrefix.CopyTo(keyInfo, 0);
        uaPublic.CopyTo(keyInfo, KeyInfoPrefix.Length);
        senderPublic.CopyTo(keyInfo, KeyInfoPrefix.Length + uaPublic.Length);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        return new WebPushKeyMaterial(senderPublic, ecdhSecret, prkKey, keyInfo, ikm, prk, cek, nonce);
    }

    private static byte[] UncompressedPoint(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(false);
        var point = new byte[VapidKey.PublicKeyLength];
        point[0] = 0x04;
        VapidKey.LeftPad(parameters.Q.X!, point.AsSpan(1, 32));
        VapidKey.LeftPad(parameters.Q.Y!, point.AsSpan(33, 32));
        return point;
    }
}

/// <summary>The intermediates of the RFC 8291 derivation, in the order the RFC lists them.</summary>
internal readonly record struct WebPushKeyMaterial(
    byte[] SenderPublicKey,
    byte[] EcdhSecret,
    byte[] PrkKey,
    byte[] KeyInfo,
    byte[] Ikm,
    byte[] Prk,
    byte[] Cek,
    byte[] Nonce);
