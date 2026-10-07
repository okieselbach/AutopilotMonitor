using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

/// <summary>
/// RFC 8291 Appendix A and section 5, values copied verbatim from the RFC text with the
/// presentation whitespace removed. Every intermediate of the derivation is pinned.
/// </summary>
public class Rfc8291VectorTests
{
    private const string Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";

    private const string EcdhSecret = "kyrL1jIIOHEzg3sM2ZWRHDRB62YACZhhSlknJ672kSs";
    private const string PrkKey = "Snr3JMxaHVDXHWJn5wdC52WjpCtd2EIEGBykDcZW32k";
    private const string KeyInfo = "V2ViUHVzaDogaW5mbwAEJXGyvs3942BVGq8e0PTNNmwRzr5VX4m8t7GGpTM5FzFo7OLr4BhZe9MEebhuPI-OztV3ylkYfpJGmQ22ggCLDgT-M_SrDepxkU21WCP3O1SUj0EwbZIHMtu5pZpTKGSCIA5Zent7wmC6HCJ5mFgJkuk5cwAvMBKiiujwa7t45ewP";
    private const string Ikm = "S4lYMb_L0FxCeq0WhDx813KgSYqU26kOyzWUdsXYyrg";
    private const string Prk = "09_eUZGrsvxChDCGRCdkLiDXrReGOEVeSCdCcPBSJSc";
    private const string Cek = "oIhVW04MRdy2XN9CiKLxTg";
    private const string Nonce = "4h_95klXJ5E_qnoN";

    private const string Header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string PaddedPlaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24C";
    private const string Ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";
    private const string Body = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    private static WebPushSubscription VectorSubscription() =>
        new(new Uri("https://push.example.net/push/JzLQ3raZJfFBR0aqvOMsLrt54w4rJUsV"), TestKeys.Decode(UaPublic), TestKeys.Decode(AuthSecret));

    private static ECDiffieHellman SenderKey()
    {
        var point = TestKeys.Decode(AsPublic);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = TestKeys.Decode(AsPrivate),
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
    }

    [Fact]
    public void Plaintext_is_the_watermelon_sentence()
    {
        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.ASCII.GetString(TestKeys.Decode(Plaintext)));
        Assert.Equal(41, TestKeys.Decode(Plaintext).Length);
    }

    [Fact]
    public void DeriveKeys_matches_every_intermediate()
    {
        using var sender = SenderKey();
        var keys = WebPushEncryptor.DeriveKeys(VectorSubscription(), TestKeys.Decode(Salt), sender);

        Assert.Equal(AsPublic, TestKeys.Base64Url(keys.SenderPublicKey));
        Assert.Equal(EcdhSecret, TestKeys.Base64Url(keys.EcdhSecret));
        Assert.Equal(PrkKey, TestKeys.Base64Url(keys.PrkKey));
        Assert.Equal(KeyInfo, TestKeys.Base64Url(keys.KeyInfo));
        Assert.Equal(144, keys.KeyInfo.Length);
        Assert.Equal(Ikm, TestKeys.Base64Url(keys.Ikm));
        Assert.Equal(Prk, TestKeys.Base64Url(keys.Prk));
        Assert.Equal(Cek, TestKeys.Base64Url(keys.Cek));
        Assert.Equal(16, keys.Cek.Length);
        Assert.Equal(Nonce, TestKeys.Base64Url(keys.Nonce));
        Assert.Equal(12, keys.Nonce.Length);
    }

    [Fact]
    public void Encrypt_produces_the_RFC_message_body()
    {
        using var sender = SenderKey();
        var body = WebPushEncryptor.Encrypt(TestKeys.Decode(Plaintext), VectorSubscription(), TestKeys.Decode(Salt), sender);

        Assert.Equal(144, body.Length);
        Assert.Equal(Header, TestKeys.Base64Url(body[..86]));
        Assert.Equal(Ciphertext, TestKeys.Base64Url(body[86..]));
        Assert.Equal(Body, TestKeys.Base64Url(body));
        Assert.Equal(192, TestKeys.Base64Url(body).Length);
    }

    [Fact]
    public void Padded_plaintext_is_plaintext_plus_delimiter_0x02()
    {
        var padded = TestKeys.Decode(PaddedPlaintext);
        Assert.Equal(TestKeys.Decode(Plaintext), padded[..^1]);
        Assert.Equal(0x02, padded[^1]);
    }

    [Fact]
    public void Header_layout_is_salt_rs_idlen_keyid()
    {
        var header = TestKeys.Decode(Header);
        Assert.Equal(TestKeys.Decode(Salt), header[..16]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x10, 0x00 }, header[16..20]);
        Assert.Equal(65, header[20]);
        Assert.Equal(TestKeys.Decode(AsPublic), header[21..]);
    }

    [Fact]
    public void Public_Encrypt_uses_fresh_salt_and_key_and_the_receiver_can_decrypt()
    {
        var subscription = VectorSubscription();
        var plaintext = TestKeys.Decode(Plaintext);
        var first = WebPushEncryptor.Encrypt(plaintext, subscription);
        var second = WebPushEncryptor.Encrypt(plaintext, subscription);

        Assert.Equal(144, first.Length);
        Assert.NotEqual(first[..16], second[..16]);
        Assert.NotEqual(first[21..86], second[21..86]);
        Assert.Equal(plaintext, ReceiverDecrypt(first));
        Assert.Equal(plaintext, ReceiverDecrypt(second));
    }

    [Fact]
    public void Encrypt_refuses_plaintext_above_the_RFC_bound_and_accepts_the_bound()
    {
        var subscription = VectorSubscription();
        Assert.Equal(3993, WebPushEncryptor.MaxPlaintextLength);
        var atBound = WebPushEncryptor.Encrypt(new byte[3993], subscription);
        Assert.Equal(4096, atBound.Length);
        Assert.Throws<ArgumentException>(() => WebPushEncryptor.Encrypt(new byte[3994], subscription));
    }

    /// <summary>The user agent's side of RFC 8291 section 3.4, written independently of the encryptor.</summary>
    private static byte[] ReceiverDecrypt(byte[] body)
    {
        var salt = body[..16];
        var senderPublic = body[21..86];
        var uaPublicPoint = TestKeys.Decode(UaPublic);
        using var uaKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = TestKeys.Decode(UaPrivate),
            Q = new ECPoint { X = uaPublicPoint[1..33], Y = uaPublicPoint[33..] },
        });
        using var senderKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..] },
        });

        var ecdhSecret = uaKey.DeriveRawSecretAgreement(senderKey.PublicKey);
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, TestKeys.Decode(AuthSecret));
        var keyInfo = "WebPush: info\0"u8.ToArray().Concat(uaPublicPoint).Concat(senderPublic).ToArray();
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        var record = body[86..];
        var padded = new byte[record.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, record[..^16], record[^16..], padded);
        Assert.Equal(0x02, padded[^1]);
        return padded[..^1];
    }
}
