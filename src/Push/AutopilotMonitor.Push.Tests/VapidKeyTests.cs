using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

public class VapidKeyTests
{
    [Fact]
    public void Generate_round_trips_through_export_and_parse()
    {
        using var generated = VapidKey.Generate();
        var export = generated.ExportBase64Url();
        using var parsed = VapidKey.Parse(export);

        Assert.Equal(96, TestKeys.Decode(export).Length);
        Assert.Equal(generated.PublicKeyBase64Url, parsed.PublicKeyBase64Url);
        Assert.Equal(generated.Kid, parsed.Kid);
        Assert.Equal(export, parsed.ExportBase64Url());
        Assert.Equal(65, generated.PublicKeyBytes.Length);
        Assert.Equal(0x04, generated.PublicKeyBytes.Span[0]);
    }

    [Fact]
    public void Kid_is_eight_chars_of_the_public_key_hash_and_stable()
    {
        using var key = VapidKey.Generate();
        var expected = TestKeys.Base64Url(SHA256.HashData(key.PublicKeyBytes.Span))[..8];

        Assert.Equal(8, key.Kid.Length);
        Assert.Equal(expected, key.Kid);
        Assert.Equal(key.Kid, VapidKey.Parse(key.ExportBase64Url()).Kid);
    }

    [Fact]
    public void SignEs256_is_a_64_byte_P1363_signature_that_verifies_with_the_public_point()
    {
        using var key = VapidKey.Generate();
        var data = Encoding.ASCII.GetBytes("header.payload");
        var signature = key.SignEs256(data);

        Assert.Equal(64, signature.Length);
        var point = key.PublicKeyBytes.ToArray();
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
        Assert.True(verifier.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData("not base64url!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void Parse_rejects_malformed_input_without_echoing_it(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => VapidKey.Parse(input));
        if (input.Length > 0)
        {
            Assert.DoesNotContain(input, ex.Message);
        }
    }

    [Fact]
    public void Parse_rejects_a_public_point_that_does_not_belong_to_the_scalar()
    {
        using var a = VapidKey.Generate();
        using var b = VapidKey.Generate();
        var mixed = TestKeys.Decode(a.ExportBase64Url())[..32].Concat(TestKeys.Decode(b.ExportBase64Url())[32..]).ToArray();

        var ex = Assert.Throws<ArgumentException>(() => VapidKey.Parse(TestKeys.Base64Url(mixed)));
        Assert.DoesNotContain(TestKeys.Base64Url(mixed), ex.Message);
    }

    [Fact]
    public void Disposed_key_refuses_to_sign()
    {
        var key = VapidKey.Generate();
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.SignEs256(new byte[1]));
    }
}

public class VapidKeyRingTests
{
    [Fact]
    public void FromSettings_resolves_active_first_then_retired_and_null_for_unknown()
    {
        using var active = VapidKey.Generate();
        using var retired1 = VapidKey.Generate();
        using var retired2 = VapidKey.Generate();

        using var ring = VapidKeyRing.FromSettings(
            active.ExportBase64Url(),
            $" {retired1.ExportBase64Url()} ; {retired2.ExportBase64Url()};");

        Assert.Equal(active.Kid, ring.Active.Kid);
        Assert.Equal(2, ring.Retired.Count);
        Assert.Same(ring.Active, ring.Resolve(active.Kid));
        Assert.Equal(retired1.Kid, ring.Resolve(retired1.Kid)!.Kid);
        Assert.Equal(retired2.Kid, ring.Resolve(retired2.Kid)!.Kid);
        Assert.Null(ring.Resolve("unknown1"));
        Assert.Null(ring.Resolve(null));
        Assert.Null(ring.Resolve(string.Empty));
    }

    [Fact]
    public void FromSettings_without_retired_keys_has_an_empty_retired_list()
    {
        using var active = VapidKey.Generate();
        using var ring = VapidKeyRing.FromSettings(active.ExportBase64Url(), null);
        Assert.Empty(ring.Retired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromSettings_rejects_a_missing_active_key(string? active)
    {
        Assert.Throws<ArgumentException>(() => VapidKeyRing.FromSettings(active, null));
    }

    [Fact]
    public void FromSettings_rejects_an_invalid_active_key_without_echoing_it()
    {
        const string bogus = "bm90LWEta2V5";
        var ex = Assert.Throws<ArgumentException>(() => VapidKeyRing.FromSettings(bogus, null));
        Assert.StartsWith("The active VAPID key is invalid", ex.Message);
        Assert.DoesNotContain(bogus, ex.Message);
    }

    [Fact]
    public void FromSettings_names_the_invalid_retired_entry_by_position_without_echoing_it()
    {
        using var active = VapidKey.Generate();
        using var retired = VapidKey.Generate();
        const string bogus = "bm90LWEta2V5";

        var ex = Assert.Throws<ArgumentException>(() => VapidKeyRing.FromSettings(active.ExportBase64Url(), $"{retired.ExportBase64Url()};{bogus}"));
        Assert.StartsWith("Retired VAPID key #2 is invalid", ex.Message);
        Assert.DoesNotContain(bogus, ex.Message);
        Assert.DoesNotContain(active.ExportBase64Url(), ex.Message);
    }
}
