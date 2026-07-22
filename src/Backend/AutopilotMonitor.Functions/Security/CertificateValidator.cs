using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates client certificates for device authentication with full chain validation
    /// </summary>
    /// <remarks>
    /// Security measures:
    /// - X.509 chain trust pinned to embedded Intune root certificate(s) via
    ///   X509ChainTrustMode.CustomRootTrust (OS trust store is ignored)
    /// - No revocation checking (Intune certificates have no CRL/OCSP endpoints)
    /// - Prevents self-signed certificate attacks
    /// - Validates Enhanced Key Usage for Client Authentication
    /// - In-memory cache for validated certificates (5 minute TTL)
    /// - Intune CA certificates embedded as resources (required on Linux/Azure Functions flex plan
    ///   where the OS trust store does not contain Microsoft Intune CAs)
    /// </remarks>
    public static class CertificateValidator
    {
        private static readonly ConcurrentDictionary<string, CachedValidationResult> _validationCache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        // Hard cap on the encoded cert header length. A real Intune device cert base64-encodes to
        // ~2 KB; 16 KB leaves ample headroom for URL-encoding while preventing CPU-DoS from a
        // flooder posting near-host-limit (~32-64 KB) garbage that would otherwise allocate
        // through UnescapeDataString + FromBase64String + X509Certificate2.
        internal const int MaxCertHeaderLength = 16 * 1024;

        // Lazily loaded Intune CA certs from embedded PEM resources, split by role:
        //   roots         -> pinned trust anchors  (chain.ChainPolicy.CustomTrustStore)
        //   intermediates -> bridge candidates    (chain.ChainPolicy.ExtraStore)
        // Files are picked up by name prefix from Security/Certificates/, so adding a
        // future Intune root or intermediate is a drop-in (no code change).
        // Duplicates (same thumbprint) are deduped at load time.
        private static readonly Lazy<X509Certificate2[]> _intuneRootCerts =
            new(() => LoadIntuneCerts("intune-root-"));
        private static readonly Lazy<X509Certificate2[]> _intuneIntermediateCerts =
            new(() => LoadIntuneCerts("intune-intermediate-"));

        /// <summary>
        /// Returns the embedded Intune root certificates loaded into the validator.
        /// Used by maintenance jobs (e.g. cert-expiry monitor) that need to inspect
        /// the bundle without re-loading PEM resources. Treat the returned array as
        /// read-only; the chain validator caches it for process lifetime.
        /// </summary>
        internal static IReadOnlyList<X509Certificate2> GetEmbeddedRoots() => _intuneRootCerts.Value;

        /// <summary>
        /// Returns the embedded Intune intermediate certificates loaded into the validator.
        /// See <see cref="GetEmbeddedRoots"/> for usage notes.
        /// </summary>
        internal static IReadOnlyList<X509Certificate2> GetEmbeddedIntermediates() => _intuneIntermediateCerts.Value;

        private class CachedValidationResult
        {
            public CertificateValidationResult Result { get; set; } = null!;
            public DateTime ExpiresAt { get; set; }
        }

        /// <summary>
        /// Stable rejection-reason codes emitted in the <c>AgentCertRejected</c> structured log.
        /// Keep these strings stable — operators query them by exact match in KQL
        /// (<c>customDimensions.Reason == "ChainFailed"</c>).
        /// </summary>
        internal static class RejectReason
        {
            public const string NoCertProvided = "NoCertProvided";
            public const string ParseError = "ParseError";
            public const string NotYetValid = "NotYetValid";
            public const string Expired = "Expired";
            public const string NoTrustAnchor = "NoTrustAnchor";
            public const string ChainFailed = "ChainFailed";
            public const string MissingClientAuthEku = "MissingClientAuthEku";
        }

        /// <summary>
        /// Validates a client certificate from HTTP request header
        /// </summary>
        /// <param name="certificateBase64">Base64-encoded certificate from X-Client-Certificate header</param>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>Validation result with certificate details</returns>
        public static CertificateValidationResult ValidateCertificate(string? certificateBase64, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(certificateBase64))
            {
                logger?.LogWarning(
                    "AgentCertRejected reason={Reason} thumbprint={Thumbprint} subject={Subject} issuer={Issuer} notBefore={NotBefore} notAfter={NotAfter} error={Error}",
                    RejectReason.NoCertProvided, "n/a", "n/a", "n/a", "n/a", "n/a", "header empty");
                return new CertificateValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "No certificate provided"
                };
            }

            try
            {
                // Reject oversized headers before the expensive decode + X509 parse path runs.
                // See MaxCertHeaderLength for rationale.
                if (certificateBase64.Length > MaxCertHeaderLength)
                {
                    logger?.LogWarning(
                        "AgentCertRejected reason={Reason} thumbprint={Thumbprint} subject={Subject} issuer={Issuer} notBefore={NotBefore} notAfter={NotAfter} error={Error}",
                        RejectReason.ParseError, "n/a", "n/a", "n/a", "n/a", "n/a", $"header too large ({certificateBase64.Length} > {MaxCertHeaderLength})");
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Certificate header too large"
                    };
                }

                // Azure App Service may URL-encode the X-ARR-ClientCert header value.
                // Decode before Base64 parsing.
                if (certificateBase64.Contains('%'))
                {
                    certificateBase64 = Uri.UnescapeDataString(certificateBase64);
                }

                // Parse certificate from Base64
                var certBytes = Convert.FromBase64String(certificateBase64);
                var certificate = X509CertificateLoader.LoadCertificate(certBytes);
                var thumbprint = certificate.Thumbprint;

                logger?.LogDebug($"Validating certificate: Subject={certificate.Subject}, Issuer={certificate.Issuer}, Thumbprint={thumbprint}");

                // Check cache first
                if (_validationCache.TryGetValue(thumbprint, out var cached))
                {
                    if (cached.ExpiresAt > DateTime.UtcNow)
                    {
                        logger?.LogDebug($"Certificate validation cache HIT for thumbprint {thumbprint}");
                        return cached.Result;
                    }
                    else
                    {
                        // Remove expired entry
                        _validationCache.TryRemove(thumbprint, out _);
                        logger?.LogDebug($"Certificate validation cache EXPIRED for thumbprint {thumbprint}");
                    }
                }

                logger?.LogDebug($"Certificate validation cache MISS for thumbprint {thumbprint}, performing full validation");

                // 1. Check expiry. X509Certificate2.NotBefore/NotAfter return Local time on .NET;
                // normalize to UTC before comparing to DateTime.UtcNow or the result is wrong on
                // hosts whose TZ != UTC (e.g. test runs on a CEST dev box).
                var now = DateTime.UtcNow;
                var notBeforeUtc = certificate.NotBefore.ToUniversalTime();
                var notAfterUtc = certificate.NotAfter.ToUniversalTime();
                if (now < notBeforeUtc || now > notAfterUtc)
                {
                    var lifecycleReason = now < notBeforeUtc ? RejectReason.NotYetValid : RejectReason.Expired;
                    logger?.LogWarning(
                        "AgentCertRejected reason={Reason} thumbprint={Thumbprint} subject={Subject} issuer={Issuer} notBefore={NotBefore:u} notAfter={NotAfter:u} error={Error}",
                        lifecycleReason, thumbprint, certificate.Subject, certificate.Issuer, notBeforeUtc, notAfterUtc, "cert lifecycle outside window");
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate expired or not yet valid (NotBefore={notBeforeUtc:u}, NotAfter={notAfterUtc:u})",
                        Thumbprint = thumbprint
                    };
                }

                // 2. Validate certificate chain against pinned Intune roots (no revocation - no CRL/OCSP).
                //
                // CustomTrustStore + CustomRootTrust pins trust to the embedded Intune root cert(s)
                // only and ignores the OS trust store entirely - works on Linux/Flex Consumption
                // (no machine store) and replaces a previous AllowUnknownCertificateAuthority
                // workaround which was bypassable by self-signed leaf certs whose Subject contained
                // "Microsoft Intune".
                var roots = _intuneRootCerts.Value;
                var intermediates = _intuneIntermediateCerts.Value;

                if (roots.Length == 0)
                {
                    logger?.LogError("No embedded Intune root certificates loaded - failing closed");
                    logger?.LogWarning(
                        "AgentCertRejected reason={Reason} thumbprint={Thumbprint} subject={Subject} issuer={Issuer} notBefore={NotBefore:u} notAfter={NotAfter:u} error={Error}",
                        RejectReason.NoTrustAnchor, thumbprint, certificate.Subject, certificate.Issuer, notBeforeUtc, notAfterUtc, "no embedded Intune roots");
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Server misconfiguration: no Intune trust anchors available",
                        Thumbprint = thumbprint
                    };
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;

                foreach (var root in roots)
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                foreach (var intermediate in intermediates)
                    chain.ChainPolicy.ExtraStore.Add(intermediate);

                logger?.LogDebug(
                    "Chain build with {RootCount} pinned Intune root(s) + {IntermediateCount} intermediate(s)",
                    roots.Length, intermediates.Length);

                var chainValid = chain.Build(certificate);

                if (!chainValid)
                {
                    var chainErrors = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation));
                    logger?.LogWarning(
                        "AgentCertRejected reason={Reason} thumbprint={Thumbprint} subject={Subject} issuer={Issuer} notBefore={NotBefore:u} notAfter={NotAfter:u} error={Error}",
                        RejectReason.ChainFailed, thumbprint, certificate.Subject, certificate.Issuer, notBeforeUtc, notAfterUtc, chainErrors);

                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate chain validation failed: {chainErrors}",
                        Thumbprint = thumbprint
                    };
                }

                // chainValid == true under CustomRootTrust proves the leaf is transitively signed
                // by one of the pinned Intune roots. The previous Subject/Issuer DN substring check
                // is intentionally removed - it added no security on top of root pinning and would
                // false-negative if Microsoft renames the CA.

                // 3. Check Enhanced Key Usage for Client Authentication (1.3.6.1.5.5.7.3.2)
                var hasClientAuth = certificate.Extensions
                    .OfType<X509EnhancedKeyUsageExtension>()
                    .Any(ext => ext.EnhancedKeyUsages
                        .OfType<Oid>()
                        .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));

                if (!hasClientAuth)
                {
                    logger?.LogWarning(
                        "AgentCertRejected reason={Reason} thumbprint={Thumbprint} subject={Subject} issuer={Issuer} notBefore={NotBefore:u} notAfter={NotAfter:u} error={Error}",
                        RejectReason.MissingClientAuthEku, thumbprint, certificate.Subject, certificate.Issuer, notBeforeUtc, notAfterUtc, "client-auth EKU missing");
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Certificate does not have Client Authentication EKU",
                        Thumbprint = thumbprint
                    };
                }

                // All checks passed - create and cache success result
                logger?.LogInformation($"Certificate validated successfully: Thumbprint={thumbprint}");
                var successResult = new CertificateValidationResult
                {
                    IsValid = true,
                    Thumbprint = thumbprint,
                    Subject = certificate.Subject,
                    Issuer = certificate.Issuer
                };

                // Cache successful validation
                _validationCache[thumbprint] = new CachedValidationResult
                {
                    Result = successResult,
                    ExpiresAt = DateTime.UtcNow.Add(CacheDuration)
                };

                logger?.LogDebug($"Certificate validation cached for thumbprint {thumbprint}, expires at {DateTime.UtcNow.Add(CacheDuration):u}");

                return successResult;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error validating certificate");
                logger?.LogWarning(
                    "AgentCertRejected reason={Reason} thumbprint={Thumbprint} subject={Subject} issuer={Issuer} notBefore={NotBefore} notAfter={NotAfter} error={Error}",
                    RejectReason.ParseError, "n/a", "n/a", "n/a", "n/a", "n/a", ex.GetType().Name + ": " + ex.Message);
                return new CertificateValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Certificate validation error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Loads all certs whose embedded resource filename starts with the given prefix.
        /// Multiple PEM blocks per file are supported (cross-signed bundles). Duplicates
        /// are deduped by thumbprint, so a file accidentally duplicated under another name
        /// is harmless. Roles are filename-segregated:
        ///   intune-root-*.pem         -> pinned trust anchors (CustomTrustStore)
        ///   intune-intermediate-*.pem -> chain bridge candidates (ExtraStore)
        /// </summary>
        private static X509Certificate2[] LoadIntuneCerts(string fileNamePrefix)
        {
            const string resourceNamespace = "AutopilotMonitor.Functions.Security.Certificates.";
            var assembly = Assembly.GetExecutingAssembly();
            var resourcePrefix = resourceNamespace + fileNamePrefix;

            var resourceNames = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(resourcePrefix, StringComparison.Ordinal)
                            && n.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal);

            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var seenThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var certs = new List<X509Certificate2>();

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;

                using var reader = new StreamReader(stream);
                var pem = reader.ReadToEnd();

                if (pem.Contains("PLACEHOLDER_REPLACE_WITH_BASE64"))
                    continue;

                int idx = 0;
                while ((idx = pem.IndexOf(begin, idx, StringComparison.Ordinal)) >= 0)
                {
                    var endIdx = pem.IndexOf(end, idx, StringComparison.Ordinal);
                    if (endIdx < 0) break;
                    var block = pem.Substring(idx, endIdx + end.Length - idx);
                    try
                    {
                        var cert = X509Certificate2.CreateFromPem(block);
                        if (seenThumbprints.Add(cert.Thumbprint))
                            certs.Add(cert);
                        else
                            cert.Dispose();
                    }
                    catch
                    {
                        // Malformed block - skip; chain.Build() will fail closed if no roots remain.
                    }
                    idx = endIdx + end.Length;
                }
            }

            return certs.ToArray();
        }
    }

    /// <summary>
    /// Result of certificate validation
    /// </summary>
    public class CertificateValidationResult
    {
        /// <summary>
        /// Whether the certificate is valid
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Certificate thumbprint (for logging and rate limiting)
        /// </summary>
        public string? Thumbprint { get; set; }

        /// <summary>
        /// Certificate subject
        /// </summary>
        public string? Subject { get; set; }

        /// <summary>
        /// Certificate issuer
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
