using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Cert-expiry monitoring leg of MaintenanceService. Runs on the existing 2h
    /// timer and emits tiered OpsEvents for embedded Intune CA certs nearing
    /// NotAfter, so the operator gets an alert months before agent mTLS
    /// would break - bundle rotation is the only fix and needs lead time to
    /// source new PEMs from a corporate-enrolled device.
    /// </summary>
    public partial class MaintenanceService
    {
        // Tier thresholds, expressed as days remaining until NotAfter.
        // Order matters: most-severe first.
        internal const int CertExpiryCriticalThresholdDays = 7;
        internal const int CertExpiryUrgentThresholdDays = 30;
        internal const int CertExpirySoonThresholdDays = 90;

        // Emit-once-per-UTC-day dedup. Runtime caches by (eventType, thumbprint)
        // tuple from today's Security ops events. A cold start re-builds from the
        // table, so Flex Consumption scale-to-zero does not multiply pings.
        internal const string CertExpiryEventTypePrefix = "EmbeddedCert";

        /// <summary>
        /// Pure tier classifier - returns the OpsEvent EventType for a given
        /// daysUntilExpiry, or null if the cert is comfortably far from expiry.
        /// Extracted so the tier boundaries are unit-testable without
        /// constructing a full MaintenanceService.
        /// </summary>
        internal static string? ClassifyCertExpiryTier(int daysUntilExpiry)
        {
            if (daysUntilExpiry <= CertExpiryCriticalThresholdDays) return "EmbeddedCertExpired";
            if (daysUntilExpiry <= CertExpiryUrgentThresholdDays) return "EmbeddedCertExpiringUrgent";
            if (daysUntilExpiry <= CertExpirySoonThresholdDays) return "EmbeddedCertExpiringSoon";
            return null;
        }

        /// <summary>
        /// Bucket-level evaluation for a role group (e.g. all "Root" certs).
        /// Picks the cert with the MAXIMUM NotAfter and classifies its tier;
        /// older certs in the same bucket are chain-helpers for in-flight
        /// device certs (e.g. a soon-to-expire intermediate that already has
        /// a successor in the bundle) and do NOT trigger their own alarm.
        ///
        /// This is the correctness invariant: if a freshness-successor is
        /// already embedded for this role, the bundle is healthy regardless
        /// of how many older expiring/expired siblings ride along for chain
        /// continuity. The operator only gets paged when the freshest itself
        /// nears expiry, i.e. when there is genuinely no successor yet.
        /// </summary>
        internal static CertBucketEvaluation EvaluateBucket(
            IReadOnlyList<X509Certificate2> certs, DateTime nowUtc)
        {
            if (certs.Count == 0)
                return new CertBucketEvaluation(null, null, 0);

            var freshest = certs
                .OrderByDescending(c => c.NotAfter.ToUniversalTime())
                .First();
            var daysLeft = (int)Math.Floor(
                (freshest.NotAfter.ToUniversalTime() - nowUtc).TotalDays);
            var tier = ClassifyCertExpiryTier(daysLeft);

            return new CertBucketEvaluation(tier, freshest, daysLeft);
        }

        internal sealed record CertBucketEvaluation(
            string? EventType,
            X509Certificate2? Freshest,
            int DaysUntilExpiry);

        /// <summary>
        /// Inspects the embedded Intune CA bundle (roots + intermediates) and
        /// emits Warning/Error/Critical OpsEvents when the FRESHEST cert in
        /// each role bucket nears expiry. Older expiring siblings stay silent
        /// as long as a successor is present (they're chain-continuity helpers).
        /// Dedup: one event per role-bucket per tier per UTC day, by querying
        /// today's Security ops events.
        /// </summary>
        public async Task CheckEmbeddedCertExpiryAsync()
        {
            var roots = CertificateValidator.GetEmbeddedRoots();
            var intermediates = CertificateValidator.GetEmbeddedIntermediates();
            var seenToday = await BuildCertExpirySeenIndexAsync();

            if (roots.Count == 0)
            {
                if (seenToday.Add("EmbeddedCertBundleEmpty|"))
                    await _opsEventService.RecordEmbeddedCertBundleEmptyAsync();
                _logger.LogError("Embedded Intune root bundle is empty - agent mTLS will fail closed");
                return;
            }

            await EmitBucketAlertIfNeededAsync(roots, "Root", seenToday);
            await EmitBucketAlertIfNeededAsync(intermediates, "Intermediate", seenToday);
        }

        private async Task EmitBucketAlertIfNeededAsync(
            IReadOnlyList<X509Certificate2> certs, string role, HashSet<string> seenToday)
        {
            var eval = EvaluateBucket(certs, DateTime.UtcNow);
            if (eval.EventType is null || eval.Freshest is null) return;

            var freshest = eval.Freshest;
            var dedupKey = $"{eval.EventType}|{freshest.Thumbprint}";
            if (!seenToday.Add(dedupKey))
            {
                _logger.LogDebug("Cert bucket alert already emitted today: {Key}", dedupKey);
                return;
            }

            var notAfterUtc = freshest.NotAfter.ToUniversalTime();
            switch (eval.EventType)
            {
                case "EmbeddedCertExpired":
                    await _opsEventService.RecordEmbeddedCertExpiredAsync(
                        role, freshest.Subject, freshest.Thumbprint, notAfterUtc, eval.DaysUntilExpiry);
                    break;
                case "EmbeddedCertExpiringUrgent":
                    await _opsEventService.RecordEmbeddedCertExpiringUrgentAsync(
                        role, freshest.Subject, freshest.Thumbprint, notAfterUtc, eval.DaysUntilExpiry);
                    break;
                case "EmbeddedCertExpiringSoon":
                    await _opsEventService.RecordEmbeddedCertExpiringSoonAsync(
                        role, freshest.Subject, freshest.Thumbprint, notAfterUtc, eval.DaysUntilExpiry);
                    break;
            }
        }

        private async Task<HashSet<string>> BuildCertExpirySeenIndexAsync()
        {
            var todayUtc = DateTime.UtcNow.Date;
            var todaysSecurityEvents = await _opsEventRepo.GetOpsEventsAsync(
                category: OpsEventCategory.Security,
                dateFrom: todayUtc,
                dateTo: todayUtc.AddDays(1));

            return todaysSecurityEvents
                .Where(e => e.EventType.StartsWith(CertExpiryEventTypePrefix, StringComparison.Ordinal))
                .Select(e => $"{e.EventType}|{ExtractThumbprint(e.Details)}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal static string ExtractThumbprint(string? detailsJson)
        {
            if (string.IsNullOrEmpty(detailsJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(detailsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("thumbprint", out var t) &&
                    t.ValueKind == JsonValueKind.String)
                {
                    return t.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // Malformed details JSON - dedup key falls back to empty thumbprint
            }
            return string.Empty;
        }
    }
}
