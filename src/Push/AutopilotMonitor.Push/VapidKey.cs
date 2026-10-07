using System.Buffers.Text;
using System.Security.Cryptography;

namespace AutopilotMonitor.Push;

/// <summary>
/// A VAPID (RFC 8292) signing key: a P-256 private scalar together with its public point.
/// The stored form is base64url(d || X || Y), 96 bytes. The public point travels with the
/// scalar because .NET offers no scalar multiplication to recompute it, and a key whose
/// point does not belong to its scalar is rejected on parse (the push service would reject
/// every signature made with it).
/// </summary>
public sealed class VapidKey : IDisposable
{
    /// <summary>Length in bytes of the stored form: 32-byte scalar plus the two 32-byte coordinates.</summary>
    public const int ExportLength = 96;

    /// <summary>Length in bytes of the uncompressed public point (0x04 || X || Y).</summary>
    public const int PublicKeyLength = 65;

    /// <summary>Number of base64url characters of the public-key hash that form the key identifier.</summary>
    public const int KidLength = 8;

    private const int CoordinateLength = 32;
    private static readonly byte[] ProbeMessage = "vapid-key-probe"u8.ToArray();

    private readonly ECDsa _ecdsa;
    private readonly byte[] _export;
    private readonly byte[] _publicKey;
    private readonly object _signLock = new();
    private bool _disposed;

    private VapidKey(ECDsa ecdsa, byte[] export, byte[] publicKey)
    {
        _ecdsa = ecdsa;
        _export = export;
        _publicKey = publicKey;
        PublicKeyBase64Url = Base64Url.EncodeToString(publicKey);
        Kid = Base64Url.EncodeToString(SHA256.HashData(publicKey))[..KidLength];
    }

    /// <summary>The uncompressed public point, 65 bytes starting with 0x04.</summary>
    public ReadOnlyMemory<byte> PublicKeyBytes => _publicKey;

    /// <summary>The public point in base64url, the value of the <c>k</c> parameter of the Authorization header.</summary>
    public string PublicKeyBase64Url { get; }

    /// <summary>Key identifier: the first eight base64url characters of SHA-256 over the public point.</summary>
    public string Kid { get; }

    /// <summary>Creates a fresh random P-256 key.</summary>
    public static VapidKey Generate()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = generated.ExportParameters(true);
        var export = new byte[ExportLength];
        LeftPad(parameters.D!, export.AsSpan(0, CoordinateLength));
        LeftPad(parameters.Q.X!, export.AsSpan(CoordinateLength, CoordinateLength));
        LeftPad(parameters.Q.Y!, export.AsSpan(2 * CoordinateLength, CoordinateLength));
        return FromExport(export);
    }

    /// <summary>
    /// Parses the stored form produced by <see cref="ExportBase64Url"/>.
    /// Throws <see cref="ArgumentException"/> on malformed input; the message never contains the input.
    /// </summary>
    public static VapidKey Parse(string base64Url)
    {
        ArgumentNullException.ThrowIfNull(base64Url);
        byte[] raw;
        try
        {
            raw = Base64Url.DecodeFromChars(base64Url.AsSpan());
        }
        catch (FormatException)
        {
            throw new ArgumentException("The VAPID key is not valid base64url.", nameof(base64Url));
        }

        if (raw.Length != ExportLength)
        {
            CryptographicOperations.ZeroMemory(raw);
            throw new ArgumentException(
                $"The VAPID key must decode to {ExportLength} bytes (private scalar followed by the uncompressed public point).",
                nameof(base64Url));
        }

        return FromExport(raw);
    }

    /// <summary>The stored form: base64url(d || X || Y). Contains the private key.</summary>
    public string ExportBase64Url()
    {
        ThrowIfDisposed();
        return Base64Url.EncodeToString(_export);
    }

    /// <summary>Signs <paramref name="data"/> with ES256 and returns the 64-byte r || s signature (JWS form).</summary>
    public byte[] SignEs256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ThrowIfDisposed();
        lock (_signLock)
        {
            return _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_export);
        _ecdsa.Dispose();
    }

    private static VapidKey FromExport(byte[] export)
    {
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = export[..CoordinateLength],
            Q = new ECPoint
            {
                X = export[CoordinateLength..(2 * CoordinateLength)],
                Y = export[(2 * CoordinateLength)..],
            },
        };

        ECDsa ecdsa;
        try
        {
            parameters.Validate();
            ecdsa = ECDsa.Create(parameters);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(export);
            throw new ArgumentException("The VAPID key is not a valid P-256 key pair.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }

        // A point that does not belong to the scalar imports cleanly on some platforms and only
        // fails at the push service (VapidPkHashMismatch); the probe surfaces it at parse time.
        bool consistent;
        try
        {
            var probe = ecdsa.SignData(ProbeMessage, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            consistent = ecdsa.VerifyData(ProbeMessage, probe, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            consistent = false;
        }

        if (!consistent)
        {
            ecdsa.Dispose();
            CryptographicOperations.ZeroMemory(export);
            throw new ArgumentException("The VAPID public point does not belong to the private scalar.");
        }

        var publicKey = new byte[PublicKeyLength];
        publicKey[0] = 0x04;
        export.AsSpan(CoordinateLength).CopyTo(publicKey.AsSpan(1));
        return new VapidKey(ecdsa, export, publicKey);
    }

    /// <summary>
    /// Writes a big-endian field element into a fixed 32-byte slot. Exports may drop leading
    /// zero bytes on some platforms; the wire forms (JWK-style point, JWS signature) never do.
    /// </summary>
    internal static void LeftPad(ReadOnlySpan<byte> value, Span<byte> destination)
    {
        if (value.Length > destination.Length)
        {
            throw new ArgumentException("The field element is longer than the curve's field size.");
        }

        destination[..(destination.Length - value.Length)].Clear();
        value.CopyTo(destination[(destination.Length - value.Length)..]);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
