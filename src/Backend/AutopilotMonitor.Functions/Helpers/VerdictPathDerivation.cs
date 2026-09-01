using System;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Read-side attribution for sessions written BEFORE <see cref="SessionSummary.VerdictPath"/>
    /// was stamped. No mass backfill: the calibration aggregate calls <see cref="Derive"/> and
    /// counts the result with <c>Derived=true</c>, so pre-instrumentation history still shows up
    /// in the matrix and is visibly weaker. The derivation reads the fixed reason literals the
    /// writers emit (classifier rule prefixes, the max-lifetime / retro suffixes, the manual and
    /// rule attributions) — when the origin is ambiguous but the rule is known the path is
    /// <c>legacy:{rule}</c>; when nothing is attributable it is <see cref="VerdictPaths.LegacyUnknown"/>.
    /// A stamped row is returned verbatim with <c>Derived=false</c>.
    /// </summary>
    public static class VerdictPathDerivation
    {
        private const string OriginLegacy = "legacy";

        // Literals are owned by EnrollmentTimeoutClassifier / the writers; pinned by
        // VerdictPathDerivationTests so a wording change there fails here, not silently.
        private const string R1Prefix = "Enrollment reported an explicit failure before timeout";
        private const string R1bAwaitingPrefix = "Pre-provisioning (WhiteGlove) completed; no user sign-in since Part 2 resumed";
        private const string R1bSucceededPrefix = "Reconciled at timeout: pre-provisioning (WhiteGlove Part 1) completed";
        private const string R1cPrefix = "Reconciled: self-deploying profile";
        private const string R2Prefix = "Reconciled at timeout: Account Setup completed";
        private const string R3Prefix = "Agent emergency break fired";
        private const string R4Prefix = "Reconciled at timeout: user completed setup";
        private const string R5AwaitingPrefix = "Device Setup completed; awaiting user / Account Setup phase";
        private const string R5IncompletePrefix = "No completion signal within";
        private const string R6Prefix = "No Device Setup completion or explicit failure signal observed before timeout";
        private const string SupersededPrefix = "Superseded by session";
        private const string SweepStalledPrefix = "Agent silent for";
        private const string AgentStallProbePrefix = "Agent reported stall after";
        private const string EspFallbackLiteral = "ESP failure (backend fallback)";
        /// <summary>
        /// Agent esp_failure event message (ShellCoreTracker) that the ingest fallback copies verbatim
        /// into FailureReason — the stamped path for that write is <see cref="VerdictPaths.AgentEspFailureFallback"/>,
        /// so pre-instrumentation rows must derive the same way (without this they read as
        /// agent:failed and the cutover showed a fake agent:failed → esp_failure_fallback shift, 2026-09-02).
        /// </summary>
        private const string EspFailureMessagePrefix = "ESP (Enrollment Status Page) reported a failure";
        /// <summary>Pre-classifier blanket timeout verdict (LegacyReclassificationService) — never agent-reported.</summary>
        private const string LegacyBlanketTimeoutPrefix = "Session timed out";
        private const string MaxLifetimeSuffix = "max-lifetime watchdog";
        private const string RetroSuffix = "Retro-reclassified";

        public static (string Path, bool Derived) Derive(SessionSummary s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!string.IsNullOrWhiteSpace(s.VerdictPath))
                return (s.VerdictPath!, false);

            return (DeriveLegacy(s), true);
        }

        private static string DeriveLegacy(SessionSummary s)
        {
            var failureReason = s.FailureReason ?? string.Empty;
            var reconcileReason = s.ReconcileReason ?? string.Empty;
            var failureSource = s.FailureSource ?? string.Empty;

            // Unambiguous attributions first — independent of status text.
            if (string.Equals(s.AdminMarkedAction, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return VerdictPaths.ManualSucceeded;
            if (string.Equals(s.AdminMarkedAction, "Failed", StringComparison.OrdinalIgnoreCase))
                return VerdictPaths.ManualFailed;
            if (failureSource.StartsWith("rule:", StringComparison.OrdinalIgnoreCase))
                return VerdictPaths.Compose(VerdictPaths.OriginRule, failureSource.Substring(5));

            switch (s.Status)
            {
                case SessionStatus.Succeeded:
                    if (reconcileReason.Length == 0
                        || reconcileReason.StartsWith("Late completion report received", StringComparison.Ordinal))
                        return s.EspSoftFailure ? VerdictPaths.AgentCompleteSoft : VerdictPaths.AgentComplete;
                    if (Starts(reconcileReason, R1bSucceededPrefix)) return Classifier(reconcileReason, ClassifierRules.R1bWhiteGloveSucceeded);
                    if (Starts(reconcileReason, R1cPrefix)) return Classifier(reconcileReason, ClassifierRules.R1cSelfDeploying);
                    if (Starts(reconcileReason, R2Prefix)) return Classifier(reconcileReason, ClassifierRules.R2AccountSetupComplete);
                    if (Starts(reconcileReason, R4Prefix)) return Classifier(reconcileReason, ClassifierRules.R4DesktopHello);
                    return VerdictPaths.LegacyUnknown;

                case SessionStatus.Failed:
                    if (Starts(failureReason, R1Prefix)) return Classifier(failureReason, ClassifierRules.R1ExplicitFailure);
                    if (string.Equals(failureSource, "max_lifetime_watchdog", StringComparison.OrdinalIgnoreCase))
                        return VerdictPaths.Compose(VerdictPaths.OriginMaxLifetime, ClassifierRules.R1ExplicitFailure);
                    if (failureReason.Contains(EspFallbackLiteral, StringComparison.Ordinal)
                        || Starts(failureReason, EspFailureMessagePrefix)) return VerdictPaths.AgentEspFailureFallback;
                    if (Starts(failureReason, LegacyBlanketTimeoutPrefix)) return VerdictPaths.LegacyUnknown;
                    if (failureReason.Length > 0 && failureSource.Length == 0) return VerdictPaths.AgentFailed;
                    return VerdictPaths.LegacyUnknown;

                case SessionStatus.Incomplete:
                    if (Starts(failureReason, R3Prefix)) return Classifier(failureReason, ClassifierRules.R3EmergencyBreak);
                    if (Starts(failureReason, R5IncompletePrefix)) return Classifier(failureReason, ClassifierRules.R5DeviceSetupIncomplete);
                    if (Starts(failureReason, R6Prefix)) return Classifier(failureReason, ClassifierRules.R6Fallthrough);
                    if (Starts(failureReason, SupersededPrefix)) return VerdictPaths.Compose(OriginLegacy, "superseded");
                    return VerdictPaths.LegacyUnknown;

                case SessionStatus.AwaitingUser:
                    if (Starts(failureReason, R1bAwaitingPrefix)) return VerdictPaths.Compose(OriginLegacy, "wg_awaiting");
                    if (Starts(failureReason, R5AwaitingPrefix)) return Classifier(failureReason, ClassifierRules.R5DeviceSetupAwaiting);
                    return VerdictPaths.LegacyUnknown;

                case SessionStatus.Stalled:
                    if (Starts(failureReason, SweepStalledPrefix)) return VerdictPaths.SweepStalled;
                    if (Starts(failureReason, AgentStallProbePrefix)) return VerdictPaths.AgentStallProbe;
                    return VerdictPaths.LegacyUnknown;

                case SessionStatus.Pending:
                    return VerdictPaths.AgentWhiteGlovePending;

                default:
                    return VerdictPaths.LegacyUnknown;
            }
        }

        /// <summary>
        /// Classifier verdict with a known rule: the origin is recoverable only from the suffixes
        /// the max-lifetime and retro writers append; sweep / late-reconcile share the bare text
        /// and stay <c>legacy:{rule}</c>.
        /// </summary>
        private static string Classifier(string reason, string rule)
        {
            if (reason.Contains(MaxLifetimeSuffix, StringComparison.Ordinal))
                return VerdictPaths.Compose(VerdictPaths.OriginMaxLifetime, rule);
            if (reason.Contains(RetroSuffix, StringComparison.Ordinal))
                return VerdictPaths.Compose(VerdictPaths.OriginRetro, rule);
            return VerdictPaths.Compose(OriginLegacy, rule);
        }

        private static bool Starts(string text, string prefix)
            => text.StartsWith(prefix, StringComparison.Ordinal);
    }
}
