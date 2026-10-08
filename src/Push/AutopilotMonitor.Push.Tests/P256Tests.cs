using System.Numerics;
using System.Security.Cryptography;
using AutopilotMonitor.Push;

namespace AutopilotMonitor.Push.Tests;

public class P256Tests
{
    [Fact]
    public void Constants_match_the_runtime_explicit_curve_parameters()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var curve = key.ExportExplicitParameters(false).Curve;

        Assert.Equal(new BigInteger(curve.Prime!, isUnsigned: true, isBigEndian: true), P256.Prime);
        Assert.Equal(new BigInteger(curve.B!, isUnsigned: true, isBigEndian: true), P256.B);
        Assert.Equal(P256.Prime - 3, new BigInteger(curve.A!, isUnsigned: true, isBigEndian: true));
    }

    [Fact]
    public void Generated_points_are_on_the_curve_and_a_flipped_bit_is_not()
    {
        for (var i = 0; i < 8; i++)
        {
            var (point, key) = TestKeys.UserAgentKey();
            key.Dispose();
            Assert.True(P256.IsOnCurve(point.AsSpan(1, 32), point.AsSpan(33, 32)));

            point[1 + (i * 7) % 64] ^= 0x01;
            Assert.False(P256.IsOnCurve(point.AsSpan(1, 32), point.AsSpan(33, 32)));
        }
    }

    [Fact]
    public void Coordinates_outside_the_field_and_wrong_lengths_are_rejected()
    {
        var p = P256.Prime.ToByteArray(isUnsigned: true, isBigEndian: true);
        Assert.False(P256.IsOnCurve(p, new byte[32]));
        Assert.False(P256.IsOnCurve(new byte[32], p));
        Assert.False(P256.IsOnCurve(new byte[31], new byte[32]));
        Assert.False(P256.IsOnCurve(new byte[32], new byte[33]));
        Assert.False(P256.IsOnCurve(new byte[32], new byte[32]));
    }
}
