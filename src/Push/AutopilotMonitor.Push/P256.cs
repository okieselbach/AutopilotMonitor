using System.Numerics;

namespace AutopilotMonitor.Push;

/// <summary>
/// The NIST P-256 curve equation, checked in arithmetic so an off-curve point from an anonymous
/// pairing body is refused on every platform: CNG imports such a point without complaint and
/// only OpenSSL rejects it, so the platform's import is not a usable gate.
/// </summary>
internal static class P256
{
    /// <summary>Field order p = 2^256 - 2^224 + 2^192 + 2^96 - 1 (FIPS 186-4, D.1.2.3).</summary>
    internal static readonly BigInteger Prime =
        (BigInteger.One << 256) - (BigInteger.One << 224) + (BigInteger.One << 192) + (BigInteger.One << 96) - BigInteger.One;

    /// <summary>Curve coefficient b (FIPS 186-4, D.1.2.3); a = -3.</summary>
    internal static readonly BigInteger B = new(
        Convert.FromHexString("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B"),
        isUnsigned: true,
        isBigEndian: true);

    /// <summary>True when (x, y), given as 32-byte big-endian coordinates, satisfies y^2 = x^3 - 3x + b (mod p).</summary>
    internal static bool IsOnCurve(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        if (x.Length != 32 || y.Length != 32)
        {
            return false;
        }

        var xi = new BigInteger(x, isUnsigned: true, isBigEndian: true);
        var yi = new BigInteger(y, isUnsigned: true, isBigEndian: true);
        if (xi >= Prime || yi >= Prime)
        {
            return false;
        }

        var left = BigInteger.Remainder(yi * yi, Prime);
        var right = BigInteger.Remainder(xi * xi * xi - 3 * xi + B, Prime);
        if (right.Sign < 0)
        {
            right += Prime;
        }

        return left == right;
    }
}
