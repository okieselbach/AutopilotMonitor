using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// Covers the pure, store-independent portions of <see cref="CertificateHelper"/>:
    /// <list type="bullet">
    /// <item><see cref="CertificateHelper.IsCertificateValid"/> — the NotBefore/NotAfter window check.</item>
    /// <item><see cref="CertificateHelper.SelectMdmCertificate"/> — the security-critical MDM cert
    /// selection (issuer discrimination + ClientAuth EKU filter + NotAfter tiebreaker) that
    /// <see cref="CertificateHelper.FindMdmCertificate"/> drives over the LocalMachine\My /
    /// CurrentUser\My store contents. Exercised here against in-memory self-signed certs so the store
    /// is never touched.</item>
    /// </list>
    /// The remaining store-iteration/thumbprint-search shell of FindMdmCertificate (opening the two
    /// X509Store instances) is not unit-tested — it needs a real machine cert store and per the project
    /// rule we do not fake it — but the selection rules it depends on are now fully pinned below.
    /// </summary>
    public sealed class CertificateHelperFindMdmCertTests
    {
        // The Intune MDM device CA (the one the backend pins) vs. the MMP-C stack's CA, which is
        // co-installed on dual-enrolled devices and must NEVER be selected.
        private const string MdmIssuerCn  = "Microsoft Intune MDM Device CA";
        private const string MmpcIssuerCn = "Microsoft Intune Device Management Device CA";
        private const string ClientAuthEku = "1.3.6.1.5.5.7.3.2";

        /// <summary>
        /// In-memory self-signed cert (so a self-signed cert's Issuer == its Subject == "CN={cn}").
        /// Optionally advertises the Client Authentication EKU and an explicit validity window.
        /// </summary>
        private static X509Certificate2 CreateCert(
            string subjectCn, bool clientAuthEku = true, int notBeforeDays = -1, int notAfterDays = 365)
        {
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest(
                    $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                if (clientAuthEku)
                {
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid(ClientAuthEku) }, critical: false));
                }

                return req.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(notBeforeDays),
                    DateTimeOffset.UtcNow.AddDays(notAfterDays));
            }
        }

        // ── IsCertificateValid: NotBefore/NotAfter window ────────────────────

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
            using var cert = CreateCert("apmon-v2-test", notBeforeDays: notBeforeDays, notAfterDays: notAfterDays);

            Assert.Equal(expected, CertificateHelper.IsCertificateValid(cert));
        }

        [Fact]
        public void IsCertificateValid_returns_false_for_null()
        {
            Assert.False(CertificateHelper.IsCertificateValid(null));
        }

        // ── SelectMdmCertificate: issuer + EKU + NotAfter tiebreaker ─────────

        [Fact]
        public void SelectMdmCertificate_picks_the_mdm_cert_with_client_auth()
        {
            using var mdm = CreateCert(MdmIssuerCn, clientAuthEku: true);

            var selected = CertificateHelper.SelectMdmCertificate(new[] { mdm });

            Assert.Same(mdm, selected);
        }

        [Fact]
        public void SelectMdmCertificate_rejects_mmpc_issuer_even_with_client_auth()
        {
            // The MMP-C CA carries ClientAuth too, but chains to an intermediate the backend does not
            // pin — selecting it produces ChainFailed. The issuer discrimination must exclude it.
            using var mmpc = CreateCert(MmpcIssuerCn, clientAuthEku: true);

            Assert.Null(CertificateHelper.SelectMdmCertificate(new[] { mmpc }));
        }

        [Fact]
        public void SelectMdmCertificate_rejects_mdm_issuer_without_client_auth_eku()
        {
            // Right issuer, but no Client Authentication EKU → not an mTLS client cert.
            using var noEku = CreateCert(MdmIssuerCn, clientAuthEku: false);

            Assert.Null(CertificateHelper.SelectMdmCertificate(new[] { noEku }));
        }

        [Fact]
        public void SelectMdmCertificate_prefers_the_longest_lived_qualifying_cert()
        {
            using var shortLived = CreateCert(MdmIssuerCn, clientAuthEku: true, notAfterDays: 30);
            using var longLived  = CreateCert(MdmIssuerCn, clientAuthEku: true, notAfterDays: 365);

            // Order in the input must not matter — the NotAfter tiebreaker decides.
            Assert.Same(longLived, CertificateHelper.SelectMdmCertificate(new[] { shortLived, longLived }));
            Assert.Same(longLived, CertificateHelper.SelectMdmCertificate(new[] { longLived, shortLived }));
        }

        [Fact]
        public void SelectMdmCertificate_issuer_filter_beats_notafter_tiebreaker()
        {
            // The dual-stack regression: an MMP-C cert with a LATER NotAfter must not win over an
            // earlier-expiring genuine MDM cert. Issuer discrimination runs before the tiebreaker.
            using var mmpcLonger = CreateCert(MmpcIssuerCn, clientAuthEku: true, notAfterDays: 3650);
            using var mdmShorter = CreateCert(MdmIssuerCn,  clientAuthEku: true, notAfterDays: 90);

            Assert.Same(mdmShorter, CertificateHelper.SelectMdmCertificate(new[] { mmpcLonger, mdmShorter }));
        }

        [Fact]
        public void SelectMdmCertificate_matches_issuer_case_insensitively()
        {
            using var upper = CreateCert(MdmIssuerCn.ToUpperInvariant(), clientAuthEku: true);

            Assert.Same(upper, CertificateHelper.SelectMdmCertificate(new[] { upper }));
        }

        [Fact]
        public void SelectMdmCertificate_returns_null_for_empty_or_null_input()
        {
            Assert.Null(CertificateHelper.SelectMdmCertificate(new X509Certificate2[0]));
            Assert.Null(CertificateHelper.SelectMdmCertificate(null));
        }

        [Fact]
        public void SelectMdmCertificate_ignores_null_entries_in_the_candidate_set()
        {
            using var mdm = CreateCert(MdmIssuerCn, clientAuthEku: true);

            var selected = CertificateHelper.SelectMdmCertificate(new X509Certificate2?[] { null, mdm, null }!);

            Assert.Same(mdm, selected);
        }
    }
}
