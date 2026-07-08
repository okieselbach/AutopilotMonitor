using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Helper for finding and validating MDM client certificates
    /// </summary>
    public static class CertificateHelper
    {
        /// <summary>
        /// Finds the MDM device certificate for client authentication
        /// </summary>
        /// <param name="thumbprint">Optional specific thumbprint to search for</param>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>The MDM certificate, or null if not found</returns>
        public static X509Certificate2 FindMdmCertificate(string thumbprint = null, AgentLogger logger = null)
        {
            try
            {
                // Search locations for MDM certificate
                var stores = new[]
                {
                    new { Location = StoreLocation.LocalMachine, Name = StoreName.My },
                    new { Location = StoreLocation.CurrentUser, Name = StoreName.My }
                };

                foreach (var storeInfo in stores)
                {
                    using (var store = new X509Store(storeInfo.Name, storeInfo.Location))
                    {
                        store.Open(OpenFlags.ReadOnly);

                        logger?.Debug($"Searching for MDM certificate in {storeInfo.Location}\\{storeInfo.Name}");

                        // If thumbprint specified, search by thumbprint
                        if (!string.IsNullOrEmpty(thumbprint))
                        {
                            var cert = store.Certificates
                                .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                                .OfType<X509Certificate2>()
                                .FirstOrDefault();

                            if (cert != null)
                            {
                                logger?.Info($"Found certificate by thumbprint: {thumbprint}");
                                return cert;
                            }
                        }
                        else
                        {
                            // Auto-detect the MDM certificate by issuer + EKU + NotAfter tiebreaker.
                            // The selection rules live in the pure SelectMdmCertificate seam so they
                            // can be unit-tested without a real store.
                            var cert = SelectMdmCertificate(store.Certificates.OfType<X509Certificate2>());
                            if (cert != null)
                            {
                                logger?.Info($"Found MDM certificate: Issuer={cert.Issuer}, Subject={cert.Subject}, Thumbprint={cert.Thumbprint}");
                                return cert;
                            }
                        }
                    }
                }

                logger?.Warning("No MDM certificate found");
                return null;
            }
            catch (Exception ex)
            {
                logger?.Error("Error finding MDM certificate", ex);
                return null;
            }
        }

        // Client Authentication EKU (used by the MDM device cert for mTLS to the backend).
        private const string ClientAuthEku = "1.3.6.1.5.5.7.3.2";

        // The exact issuer of the Intune MDM device certificate. Deliberately NOT a substring match
        // on "Microsoft Intune": the MMP-C stack issues "CN=Microsoft Intune Device Management Device
        // CA", co-installed on dual-enrolled devices, which chains to a different intermediate the
        // backend does not pin. Selecting it would produce ChainFailed at the backend.
        private const string MdmIssuer = "CN=Microsoft Intune MDM Device CA";

        /// <summary>
        /// Pure selection of the MDM device certificate from a candidate set (typically the
        /// LocalMachine\My / CurrentUser\My store contents). Applies the issuer discrimination,
        /// ClientAuth-EKU filter and NotAfter tiebreaker that <see cref="FindMdmCertificate"/> relies
        /// on — extracted so the security-critical selection is unit-testable without a real store.
        /// Among qualifying certs the longest-lived (highest NotAfter) wins; returns null if none
        /// qualify (or the input is null).
        /// </summary>
        public static X509Certificate2 SelectMdmCertificate(IEnumerable<X509Certificate2> candidates)
        {
            if (candidates == null)
                return null;

            return candidates
                .Where(c => c != null && IsMdmCertificate(c))
                .OrderByDescending(c => c.NotAfter) // prefer the cert with the longest validity
                .FirstOrDefault();
        }

        private static bool IsMdmCertificate(X509Certificate2 c)
        {
            var issuerIsMdm = string.Equals(c.Issuer, MdmIssuer, StringComparison.OrdinalIgnoreCase);

            // Enhanced Key Usage must advertise Client Authentication (1.3.6.1.5.5.7.3.2).
            var hasClientAuth = c.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .Any(ext => ext.EnhancedKeyUsages
                    .OfType<Oid>()
                    .Any(oid => oid.Value == ClientAuthEku));

            return issuerIsMdm && hasClientAuth;
        }

        /// <summary>
        /// Validates that a certificate is still valid
        /// </summary>
        public static bool IsCertificateValid(X509Certificate2 certificate)
        {
            if (certificate == null)
                return false;

            var now = DateTime.UtcNow;
            return now >= certificate.NotBefore && now <= certificate.NotAfter;
        }
    }
}
