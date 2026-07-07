using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

// NOTE: FindMdmCertificate (CertificateHelper.cs:20-101) is NOT unit-testable as written — it
// opens LocalMachine\My and CurrentUser\My X509Store instances directly and performs the
// issuer-match ("CN=Microsoft Intune MDM Device CA", explicitly NOT the MMP-C
// "CN=Microsoft Intune Device Management Device CA"), the ClientAuth EKU (1.3.6.1.5.5.7.3.2)
// filter, and the NotAfter tiebreaker inline inside that store loop. There is no pure seam
// (e.g. an internal selection method over an IEnumerable<X509Certificate2> or an issuer
// predicate) to drive those rules without a real machine cert store. Per the project rule we do
// NOT fake the store. Refactor needed to make the security-critical selection testable: extract a
// `SelectMdmCertificate(IEnumerable<X509Certificate2>)` pure method and unit-test the issuer
// discrimination + NotAfter tiebreaker against it. Until then this file covers only the pure
// helper actually exposed: IsCertificateValid.
namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// Covers the pure, store-independent portion of <see cref="CertificateHelper"/> —
    /// <see cref="CertificateHelper.IsCertificateValid"/> (CertificateHelper.cs:106-113), the
    /// NotBefore/NotAfter validity-window check. See the file-top NOTE for why FindMdmCertificate's
    /// selection logic is out of unit-test reach.
    /// </summary>
    public sealed class CertificateHelperFindMdmCertTests
    {
        /// <summary>
        /// In-memory self-signed cert with an explicit validity window (days relative to now),
        /// so the store is never touched. Mirrors the CreateSelfSignedCert pattern used by
        /// MtlsHttpClientFactoryTests. Windows are kept &gt;= 1 day away from "now" so the
        /// helper's local-vs-UTC comparison skew cannot flip the expected result.
        /// </summary>
        private static X509Certificate2 CreateCert(int notBeforeDays, int notAfterDays)
        {
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest(
                    "CN=apmon-v2-test",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                return req.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(notBeforeDays),
                    DateTimeOffset.UtcNow.AddDays(notAfterDays));
            }
        }

        [Theory]
        // Currently within the window → valid.
        [InlineData(-1, 1, true)]
        [InlineData(-30, 30, true)]
        // Expired (NotAfter in the past) → invalid.
        [InlineData(-10, -1, false)]
        // Not yet valid (NotBefore in the future) → invalid.
        [InlineData(1, 10, false)]
        public void IsCertificateValid_reflects_validity_window(int notBeforeDays, int notAfterDays, bool expected)
        {
            using var cert = CreateCert(notBeforeDays, notAfterDays);

            Assert.Equal(expected, CertificateHelper.IsCertificateValid(cert));
        }

        [Fact]
        public void IsCertificateValid_returns_false_for_null()
        {
            Assert.False(CertificateHelper.IsCertificateValid(null));
        }
    }
}
