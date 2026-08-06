using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-trip + tamper + replay tests for the pagination continuation token
/// codec (PR-1 of mcp-pagination-rollout).
/// </summary>
public class ContinuationTokenTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string AzureToken = "1!76!ZjQwYjBmYWMtMDcyNS00MzZjLWI2NTAtZmRl";

    private static IEnumerable<KeyValuePair<string, string?>> FilterArgsA() => new[]
    {
        new KeyValuePair<string, string?>("tenantId", TenantA),
        new KeyValuePair<string, string?>("eventType", "enrollment_complete"),
        new KeyValuePair<string, string?>("dateFrom", "2026-04-01T00:00:00Z"),
        new KeyValuePair<string, string?>("dateTo", "2026-05-01T00:00:00Z"),
    };

    private static IEnumerable<KeyValuePair<string, string?>> FilterArgsB() => new[]
    {
        new KeyValuePair<string, string?>("tenantId", TenantB),
        new KeyValuePair<string, string?>("eventType", "enrollment_complete"),
        new KeyValuePair<string, string?>("dateFrom", "2026-04-01T00:00:00Z"),
        new KeyValuePair<string, string?>("dateTo", "2026-05-01T00:00:00Z"),
    };

    [Fact]
    public void Encode_then_TryDecode_returns_original_azure_token()
    {
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fp);

        Assert.True(ContinuationToken.TryDecode(encoded, TenantA, fp, out var decoded, out var reason));
        Assert.Null(reason);
        Assert.Equal(AzureToken, decoded);
    }

    [Fact]
    public void TryDecode_rejects_token_for_a_different_tenant()
    {
        // Token issued for tenant A, replayed by tenant B — must fail before any
        // data is read regardless of fingerprint plausibility.
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fp);

        var fpForB = ContinuationToken.ComputeFingerprint(FilterArgsB());
        Assert.False(ContinuationToken.TryDecode(encoded, TenantB, fpForB, out var decoded, out var reason));
        Assert.Equal("cross_tenant", reason);
        Assert.Equal(string.Empty, decoded);
    }

    [Fact]
    public void TryDecode_rejects_token_when_filter_fingerprint_changes()
    {
        // Token issued for filter X, caller now sends filter Y → reject; token
        // would otherwise reseek into a different table layout.
        var fpOriginal = ContinuationToken.ComputeFingerprint(FilterArgsA());
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fpOriginal);

        var fpDifferent = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("tenantId", TenantA),
            new KeyValuePair<string, string?>("eventType", "enrollment_failed"), // changed
            new KeyValuePair<string, string?>("dateFrom", "2026-04-01T00:00:00Z"),
            new KeyValuePair<string, string?>("dateTo", "2026-05-01T00:00:00Z"),
        });

        Assert.False(ContinuationToken.TryDecode(encoded, TenantA, fpDifferent, out _, out var reason));
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryDecode_rejects_token_with_tampered_payload()
    {
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fp);

        // Flip a byte in the middle of the base64url payload — decoder either
        // fails parsing or rejects on tenant/fingerprint check; either way no
        // entries are exposed.
        var tampered = encoded.Substring(0, encoded.Length / 2) + "X" + encoded.Substring(encoded.Length / 2 + 1);

        Assert.False(ContinuationToken.TryDecode(tampered, TenantA, fp, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryDecode_rejects_expired_token()
    {
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        var issued = DateTimeOffset.UtcNow - TimeSpan.FromHours(48);
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fp, issued);

        Assert.False(ContinuationToken.TryDecode(encoded, TenantA, fp, out _, out var reason));
        Assert.Equal("expired", reason);
    }

    [Fact]
    public void TryDecode_accepts_token_within_default_ttl()
    {
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        var issued = DateTimeOffset.UtcNow - TimeSpan.FromHours(23);
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fp, issued);

        Assert.True(ContinuationToken.TryDecode(encoded, TenantA, fp, out var decoded, out _));
        Assert.Equal(AzureToken, decoded);
    }

    [Fact]
    public void TryDecode_rejects_garbage_input()
    {
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        Assert.False(ContinuationToken.TryDecode("not a token", TenantA, fp, out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryDecode_rejects_empty_input()
    {
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        Assert.False(ContinuationToken.TryDecode("", TenantA, fp, out _, out var reason));
        Assert.Equal("empty", reason);
    }

    [Fact]
    public void ComputeFingerprint_is_stable_regardless_of_arg_order()
    {
        var fp1 = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("tenantId", TenantA),
            new KeyValuePair<string, string?>("dateFrom", "2026-04-01T00:00:00Z"),
            new KeyValuePair<string, string?>("eventType", "x"),
        });
        var fp2 = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("eventType", "x"),
            new KeyValuePair<string, string?>("tenantId", TenantA),
            new KeyValuePair<string, string?>("dateFrom", "2026-04-01T00:00:00Z"),
        });
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_treats_keys_case_insensitively()
    {
        var fp1 = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("TenantId", TenantA),
            new KeyValuePair<string, string?>("EventType", "x"),
        });
        var fp2 = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("tenantid", TenantA),
            new KeyValuePair<string, string?>("eventtype", "x"),
        });
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_drops_null_and_empty_values()
    {
        // Optional filter args (null/empty) must not perturb the fingerprint —
        // otherwise endpoints that pass them through unconditionally would
        // produce non-resumable tokens.
        var withOptionals = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("tenantId", TenantA),
            new KeyValuePair<string, string?>("eventType", "x"),
            new KeyValuePair<string, string?>("dateFrom", null),
            new KeyValuePair<string, string?>("dateTo", ""),
        });
        var withoutOptionals = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("tenantId", TenantA),
            new KeyValuePair<string, string?>("eventType", "x"),
        });
        Assert.Equal(withOptionals, withoutOptionals);
    }

    [Fact]
    public void ComputeFingerprint_changes_when_value_differs()
    {
        var a = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("tenantId", TenantA),
        });
        var b = ContinuationToken.ComputeFingerprint(new[]
        {
            new KeyValuePair<string, string?>("tenantId", TenantB),
        });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Encode_throws_on_empty_tenant_or_fingerprint()
    {
        Assert.Throws<ArgumentException>(() => ContinuationToken.Encode(AzureToken, "", "fp"));
        Assert.Throws<ArgumentException>(() => ContinuationToken.Encode(AzureToken, TenantA, ""));
        Assert.Throws<ArgumentNullException>(() => ContinuationToken.Encode(null!, TenantA, "fp"));
    }

    // ────────── HMAC signature integrity ────────────────────────────────────

    [Fact]
    public void TryDecode_rejects_iat_bypass_attempt()
    {
        // The whole point of HMAC: a client that hand-crafts a token with a
        // fresh `iat` to bypass the 24h expiry must be rejected. The fingerprint
        // and tenantId checks alone don't catch this — only the signature does.
        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fp,
            issuedAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(48));

        // Decode the token's payload, push iat to "now", re-encode without
        // re-signing — exactly what an attacker would try.
        var jsonBytes = Base64UrlDecode(encoded);
        using var doc = System.Text.Json.JsonDocument.Parse(jsonBytes);
        var rebuilt = new System.Collections.Generic.Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            rebuilt[prop.Name] = prop.Name == "iat"
                ? (object)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                : prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.Value.GetString()!
                    : (object)prop.Value.GetInt64();
        }
        var tamperedJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rebuilt);
        var tampered = Base64UrlEncode(tamperedJson);

        Assert.False(ContinuationToken.TryDecode(tampered, TenantA, fp, out _, out var reason));
        Assert.Equal("bad_signature", reason);
    }

    [Fact]
    public void TryDecode_rejects_payload_with_missing_signature()
    {
        // Crafted token without the `sig` field — the codec must refuse to
        // proceed before any other field is even examined.
        var unsigned = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            t = AzureToken,
            tid = TenantA,
            fp = "deadbeef",
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        var raw = Base64UrlEncode(unsigned);

        Assert.False(ContinuationToken.TryDecode(raw, TenantA, "deadbeef", out _, out var reason));
        Assert.Equal("missing_signature", reason);
    }

    [Fact]
    public void TryDecode_rejects_token_signed_with_a_different_key()
    {
        // Simulate key rotation: token issued under key A, validated under key B.
        // The scoped (AsyncLocal) override keeps the swap confined to this test's
        // execution context — mutating the process-wide test key here raced the
        // concurrently running pagination test classes.
        var keyA = new byte[32]; for (int i = 0; i < keyA.Length; i++) keyA[i] = (byte)(i * 3 + 1);
        var keyB = new byte[32]; for (int i = 0; i < keyB.Length; i++) keyB[i] = (byte)(i * 5 + 7);

        var fp = ContinuationToken.ComputeFingerprint(FilterArgsA());

        ContinuationToken.SetScopedSigningKeyForTesting(keyA);
        var encoded = ContinuationToken.Encode(AzureToken, TenantA, fp);

        ContinuationToken.SetScopedSigningKeyForTesting(keyB);
        var ok = ContinuationToken.TryDecode(encoded, TenantA, fp, out _, out var reason);

        ContinuationToken.SetScopedSigningKeyForTesting(null);

        Assert.False(ok);
        Assert.Equal("bad_signature", reason);
    }

    // Test-only mirrors of the Base64Url helpers in ContinuationToken — they're
    // private there, but we need the same encoding to construct adversarial tokens.
    private static string Base64UrlEncode(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
