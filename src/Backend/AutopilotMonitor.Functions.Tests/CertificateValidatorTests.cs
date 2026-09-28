using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for CertificateValidator chain-trust enforcement.
///
/// The validator pins trust to embedded Intune root certs via X509ChainTrustMode.CustomRootTrust.
/// Two layers of coverage:
///   - Happy path: a real Intune-issued device cert (public-key only, copied from a test VM)
///     must validate successfully. This guards against rollouts where the embedded root/intermediate
///     bundle drifts from the chain Microsoft is actually issuing.
///   - Negative path: self-signed leaf certs (with or without Intune-shaped DNs) must NOT validate.
///   - No network I/O: a leaf from an unknown CA must be rejected without fetching its AIA URL.
/// </summary>
public class CertificateValidatorTests
{
    private const string DeviceSamplePem = "device-cert-sample.pem";

    [Fact]
    public void ValidateCertificate_WithRealIntuneDeviceCert_ReturnsValid()
    {
        // Real device cert (public-key only) issued by "Microsoft Intune MDM Device CA".
        // This is the regression test that would have caught the previous outage where the
        // embedded root bundle didn't match the chain Microsoft uses for current devices.
        var pemPath = ResolveSamplePath();
        Assert.True(File.Exists(pemPath), $"Sample cert not found at {pemPath}");

        var pem = File.ReadAllText(pemPath);
        using var cert = X509Certificate2.CreateFromPem(pem);
        var b64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.True(result.IsValid, $"Real device cert rejected: {result.ErrorMessage}");
        Assert.False(string.IsNullOrEmpty(result.Thumbprint));
    }

    [Fact]
    public void ValidateCertificate_WithEmptyBase64_ReturnsInvalid()
    {
        var result = CertificateValidator.ValidateCertificate("");

        Assert.False(result.IsValid);
        Assert.Contains("No certificate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCertificate_WithNull_ReturnsInvalid()
    {
        var result = CertificateValidator.ValidateCertificate(null);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithMalformedBase64_ReturnsInvalid()
    {
        var result = CertificateValidator.ValidateCertificate("not-base64!!!");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithOversizedHeader_ReturnsInvalidWithoutAllocating()
    {
        // Guard against CPU-DoS from a flooder posting a near-host-limit header that would
        // otherwise traverse UnescapeDataString + FromBase64String + X509Certificate2.
        var oversized = new string('A', CertificateValidator.MaxCertHeaderLength + 1);

        var result = CertificateValidator.ValidateCertificate(oversized);

        Assert.False(result.IsValid);
        Assert.Contains("too large", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCertificate_WithSelfSignedCertImpersonatingIntune_ReturnsInvalid()
    {
        // Regression test: a self-signed leaf with Subject CN="Microsoft Intune MDM Device CA"
        // and Client-Auth EKU previously passed validation under
        // AllowUnknownCertificateAuthority + DN substring match. Under CustomRootTrust the
        // chain has no signature link to a pinned Intune root and must be rejected.
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "Microsoft Intune MDM Device CA",
            includeClientAuthEku: true);

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
        Assert.Contains("chain validation failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCertificate_WithSelfSignedCertImpersonatingMdmDeviceCa_ReturnsInvalid()
    {
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "MDM Device CA",
            includeClientAuthEku: true);

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithSelfSignedNonIntuneCert_ReturnsInvalid()
    {
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "test-device.contoso.example",
            includeClientAuthEku: true);

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithExpiredSelfSignedCert_ReturnsInvalid()
    {
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "Microsoft Intune MDM Device CA",
            includeClientAuthEku: true,
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddYears(-1));

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCertificate_WithUnknownIssuerAndAiaUrl_NeverFetchesTheIssuer()
    {
        // The caller mints the leaf, so the AIA caIssuers URL points wherever they like. A
        // self-signed leaf never reaches the issuer-download path; it takes a leaf signed by a
        // CA that is neither embedded nor sent.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var b64 = CreateLeafFromUnknownCaBase64($"http://127.0.0.1:{port}/{Guid.NewGuid():N}.cer");

            var result = CertificateValidator.ValidateCertificate(b64);

            Assert.False(result.IsValid);
            Assert.Contains("chain validation failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(listener.Pending(), "Chain building connected to the certificate's AIA URL");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string ResolveSamplePath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(CertificateValidatorTests).Assembly.Location)!;
        return Path.Combine(assemblyDir, DeviceSamplePem);
    }

    private static string CreateSelfSignedCertBase64(
        string subjectCn,
        bool includeClientAuthEku,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={subjectCn}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (includeClientAuthEku)
        {
            // 1.3.6.1.5.5.7.3.2 = Client Authentication
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
                    critical: false));
        }

        var nb = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var na = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);
        using var cert = req.CreateSelfSigned(nb, na);

        return Convert.ToBase64String(cert.Export(X509ContentType.Cert));
    }

    private static string CreateLeafFromUnknownCaBase64(string aiaCaIssuersUrl)
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Unknown Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=test-device.contoso.example", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
                critical: false));
        leafReq.CertificateExtensions.Add(
            new X509AuthorityInformationAccessExtension(ocspUris: null, caIssuersUris: new[] { aiaCaIssuersUrl }));
        using var leaf = leafReq.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(16));

        return Convert.ToBase64String(leaf.Export(X509ContentType.Cert));
    }
}
