using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Waits for an Intune MDM certificate to appear in the certificate store.
    /// Used in await-enrollment mode when the agent is deployed before MDM enrollment completes.
    /// Polls CertificateHelper.FindMdmCertificate() at a fixed interval (Timer-based, no busy-wait).
    /// </summary>
    public static class EnrollmentAwaiter
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Polls the certificate store until a valid MDM certificate is found or the timeout expires.
        /// </summary>
        public static async Task<X509Certificate2> WaitForMdmCertificateAsync(
            string thumbprint = null,
            int timeoutMinutes = 480,
            AgentLogger logger = null,
            CancellationToken cancellationToken = default)
        {
            var timeoutDisplay = timeoutMinutes > 0 ? $"{timeoutMinutes}min" : "unlimited";
            logger?.Info($"Await-enrollment: Waiting for MDM certificate (timeout: {timeoutDisplay}, poll interval: {PollInterval.TotalSeconds}s)");

            var startTime = DateTime.UtcNow;
            var timeout = timeoutMinutes > 0 ? TimeSpan.FromMinutes(timeoutMinutes) : TimeSpan.MaxValue;
            var lastLogTime = DateTime.MinValue;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Not finding the certificate is the expected answer of most polls; the start, the
                // minute status and the result below tell the story.
                var cert = CertificateHelper.FindMdmCertificate(thumbprint, logger, logNotFound: false);

                if (cert != null)
                {
                    // No IsCertificateValid check here: freshly provisioned SCEP certs can have
                    // a NotBefore slightly in the future due to clock skew between device and CA.
                    // The cert existing in the store with correct issuer+EKU is sufficient.
                    var elapsed = DateTime.UtcNow - startTime;
                    logger?.Info($"Await-enrollment: MDM certificate found after {FormatElapsed(elapsed)} — " +
                                $"Issuer={cert.Issuer}, Thumbprint={cert.Thumbprint}, " +
                                $"Valid={cert.NotBefore:yyyy-MM-dd HH:mm:ss}..{cert.NotAfter:yyyy-MM-dd HH:mm:ss}");
                    return cert;
                }

                var totalElapsed = DateTime.UtcNow - startTime;

                if (totalElapsed >= timeout)
                {
                    logger?.Warning($"Await-enrollment: Timeout after {FormatElapsed(totalElapsed)} — no MDM certificate found");
                    LogCertificateStoreDetails(logger);
                    return null;
                }

                // Periodic status with cert counts (every 1 minute)
                if (DateTime.UtcNow - lastLogTime >= LogInterval)
                {
                    var remaining = timeout - totalElapsed;
                    var storeSummary = GetCertificateStoreSummary();
                    logger?.Debug($"Await-enrollment: Waiting for MDM certificate... " +
                                $"(elapsed: {FormatElapsed(totalElapsed)}, remaining: {FormatElapsed(remaining)}, {storeSummary})");
                    lastLogTime = DateTime.UtcNow;
                }

                try
                {
                    await Task.Delay(PollInterval, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            logger?.Info("Await-enrollment: Cancelled");
            return null;
        }

        /// <summary>
        /// Returns a short summary of cert counts per store, e.g. "LocalMachine=2, CurrentUser=0".
        /// </summary>
        private static string GetCertificateStoreSummary()
        {
            try
            {
                int lmCount = 0, cuCount = 0;

                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    lmCount = store.Certificates.Count;
                }

                using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadOnly);
                    cuCount = store.Certificates.Count;
                }

                return $"certs in store: LM={lmCount}, CU={cuCount}";
            }
            catch
            {
                return "certs in store: unknown";
            }
        }

        /// <summary>
        /// Full cert store dump — only on timeout to help diagnose why no MDM cert was matched.
        /// </summary>
        private static void LogCertificateStoreDetails(AgentLogger logger)
        {
            if (logger == null) return;

            try
            {
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
                        var certs = store.Certificates.OfType<X509Certificate2>().ToList();

                        logger.Info($"Await-enrollment: {storeInfo.Location}\\{storeInfo.Name} — {certs.Count} certificate(s):");

                        foreach (var cert in certs)
                        {
                            var hasClientAuth = cert.Extensions
                                .OfType<X509EnhancedKeyUsageExtension>()
                                .Any(ext => ext.EnhancedKeyUsages
                                    .OfType<Oid>()
                                    .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));

                            logger.Info($"  - Issuer={cert.Issuer}, Thumbprint={cert.Thumbprint}, " +
                                        $"ClientAuth={hasClientAuth}, Valid={cert.NotBefore:yyyy-MM-dd}..{cert.NotAfter:yyyy-MM-dd}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"Await-enrollment: Could not read certificate store: {ex.Message}");
            }
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
                return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m";
            return $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s";
        }
    }
}
