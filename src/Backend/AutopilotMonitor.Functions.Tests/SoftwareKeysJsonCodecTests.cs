using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure-logic tests for the SoftwareKeysJson codec used by the inventory contributions side-row
/// (Plan §17). Encoding determinism + delta computation are the load-bearing properties — the
/// cascade decrement step reads back the side-row's keys and is byte-identical to what the
/// increment path wrote.
/// </summary>
public class SoftwareKeysJsonCodecTests
{
    [Fact]
    public void Encode_uses_raw_json_below_compression_threshold()
    {
        var keys = new List<DeletionDecrementKey>
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
            new DeletionDecrementKey { Vendor = "Fabrikam", Name = "Gadget", Version = "2.0" },
        };

        var encoded = SoftwareKeysJsonCodec.Encode(keys);

        Assert.False(encoded.IsCompressed);
        Assert.Equal(2, encoded.KeyCount);
        Assert.Contains("Contoso", encoded.Encoded);
        Assert.Contains("Fabrikam", encoded.Encoded);
    }

    [Fact]
    public void Encode_uses_gzip_base64_above_compression_threshold()
    {
        // Generate >30 KB of raw JSON: 1000 distinct keys with ~30-char vendor each.
        var keys = new List<DeletionDecrementKey>();
        for (var i = 0; i < 1000; i++)
        {
            keys.Add(new DeletionDecrementKey
            {
                Vendor = $"VendorWithALongerNameForBulk-{i:D6}",
                Name = $"Product-{i:D6}",
                Version = $"1.{i}.0",
            });
        }

        var encoded = SoftwareKeysJsonCodec.Encode(keys);

        Assert.True(encoded.IsCompressed);
        Assert.Equal(1000, encoded.KeyCount);
        // Compressed payload should be much smaller than raw (heavy redundancy).
        var rawSize = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(keys, DeletionManifestJson.SerializerOptions).Length;
        Assert.True(encoded.Encoded.Length * 3 / 4 < rawSize / 3,
            $"Compression should shrink payload by >3x. Got base64 {encoded.Encoded.Length} chars vs raw {rawSize} bytes.");
    }

    [Fact]
    public void Encode_decode_round_trips_keys_in_deterministic_order()
    {
        var keys = new List<DeletionDecrementKey>
        {
            new DeletionDecrementKey { Vendor = "Tailspin", Name = "Service", Version = "3.0" },
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
            new DeletionDecrementKey { Vendor = "Fabrikam", Name = "Gadget", Version = "2.0" },
        };

        var encoded = SoftwareKeysJsonCodec.Encode(keys);
        var decoded = SoftwareKeysJsonCodec.Decode(encoded.Encoded, encoded.IsCompressed);

        Assert.Equal(3, decoded.Count);
        // Builder sorts by (Vendor, Name, Version) so the side-row content is byte-stable
        // across builds — critical for manifest-determinism (Plan §18.6).
        Assert.Equal("Contoso", decoded[0].Vendor);
        Assert.Equal("Fabrikam", decoded[1].Vendor);
        Assert.Equal("Tailspin", decoded[2].Vendor);
    }

    [Fact]
    public void Encode_with_compression_round_trips_through_gzip_base64()
    {
        // Force the compressed path with a key set that just barely exceeds the threshold.
        var keys = new List<DeletionDecrementKey>();
        for (var i = 0; i < 500; i++)
        {
            keys.Add(new DeletionDecrementKey
            {
                Vendor = "VendorWithALongerNameForBulk-" + i.ToString("D6"),
                Name = "Product-" + i.ToString("D6"),
                Version = "1." + i + ".0",
            });
        }

        var encoded = SoftwareKeysJsonCodec.Encode(keys);
        var decoded = SoftwareKeysJsonCodec.Decode(encoded.Encoded, encoded.IsCompressed);

        Assert.True(encoded.IsCompressed);
        Assert.Equal(500, decoded.Count);
        Assert.Equal(keys[0].Vendor, decoded.First(k => k.Vendor == keys[0].Vendor).Vendor);
    }

    [Fact]
    public void Decode_empty_string_returns_empty_list()
    {
        Assert.Empty(SoftwareKeysJsonCodec.Decode("", false));
        Assert.Empty(SoftwareKeysJsonCodec.Decode("", true));
    }

    [Fact]
    public void Decode_invalid_base64_throws_invalid_data()
    {
        var ex = Assert.Throws<System.IO.InvalidDataException>(
            () => SoftwareKeysJsonCodec.Decode("!!! not base64 !!!", isCompressed: true));
        Assert.Contains("Base64", ex.Message);
    }

    [Fact]
    public void Decode_malformed_json_throws_invalid_data()
    {
        Assert.Throws<System.IO.InvalidDataException>(
            () => SoftwareKeysJsonCodec.Decode("{not-an-array}", isCompressed: false));
    }

    [Fact]
    public void ComputeDelta_returns_all_added_when_oldKeys_is_empty()
    {
        var newKeys = new[]
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
            new DeletionDecrementKey { Vendor = "Fabrikam", Name = "Gadget", Version = "2.0" },
        };

        var (added, removed) = SoftwareKeysJsonCodec.ComputeDelta(System.Array.Empty<DeletionDecrementKey>(), newKeys);

        Assert.Equal(2, added.Count);
        Assert.Empty(removed);
    }

    [Fact]
    public void ComputeDelta_returns_one_added_when_newKeys_adds_a_phase2_app()
    {
        var oldKeys = new[]
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
        };
        var newKeys = new[]
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
            new DeletionDecrementKey { Vendor = "Fabrikam", Name = "NewApp", Version = "2.0" },
        };

        var (added, removed) = SoftwareKeysJsonCodec.ComputeDelta(oldKeys, newKeys);

        Assert.Single(added);
        Assert.Contains(SoftwareKeysJsonCodec.CompositeKey("Fabrikam", "NewApp", "2.0"), added);
        Assert.Empty(removed);
    }

    [Fact]
    public void ComputeDelta_returns_one_removed_when_uninstall_happens_between_phases()
    {
        var oldKeys = new[]
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
            new DeletionDecrementKey { Vendor = "Fabrikam", Name = "OldApp", Version = "0.9" },
        };
        var newKeys = new[]
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
        };

        var (added, removed) = SoftwareKeysJsonCodec.ComputeDelta(oldKeys, newKeys);

        Assert.Empty(added);
        Assert.Single(removed);
        Assert.Contains(SoftwareKeysJsonCodec.CompositeKey("Fabrikam", "OldApp", "0.9"), removed);
    }

    [Fact]
    public void ComputeDelta_is_empty_when_keys_are_identical()
    {
        var keys = new[]
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
            new DeletionDecrementKey { Vendor = "Fabrikam", Name = "Gadget", Version = "2.0" },
        };

        var (added, removed) = SoftwareKeysJsonCodec.ComputeDelta(keys, keys);

        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void ComputeDelta_is_case_insensitive()
    {
        // Upstream normalization is best-effort; "Contoso" and "contoso" must collapse so a case-
        // flip between two correlation events on the same session doesn't trigger spurious deltas.
        var oldKeys = new[]
        {
            new DeletionDecrementKey { Vendor = "Contoso", Name = "Widget", Version = "1.0" },
        };
        var newKeys = new[]
        {
            new DeletionDecrementKey { Vendor = "contoso", Name = "widget", Version = "1.0" },
        };

        var (added, removed) = SoftwareKeysJsonCodec.ComputeDelta(oldKeys, newKeys);

        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void CompositeKey_builds_three_part_separated_value()
    {
        var ck = SoftwareKeysJsonCodec.CompositeKey("Contoso", "Widget", "1.0");
        Assert.NotEqual("ContosoWidget1.0", ck); // separator IS present
        // Different orderings of the same string components produce different composites.
        var ck2 = SoftwareKeysJsonCodec.CompositeKey("Widget", "Contoso", "1.0");
        Assert.NotEqual(ck, ck2);
    }
}
