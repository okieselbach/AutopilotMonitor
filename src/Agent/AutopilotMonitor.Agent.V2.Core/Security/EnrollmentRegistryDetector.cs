using System;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Read-only registry probes for enrollment metadata the agent has to pin down at session
    /// registration time. V1 parity — these mirror <c>EnrollmentTracker.DetectHybridJoinStatic</c>
    /// and <c>EnrollmentTracker.DetectEnrollmentTypeStatic</c> from the legacy agent.
    /// <para>
    /// Kept stateless and exception-swallowing so the registration pipeline can call them
    /// before any other component is up. A missing / inaccessible registry key degrades to
    /// the default value (v1 / non-hybrid) rather than throwing — mirrors V1 behaviour.
    /// </para>
    /// </summary>
    public static class EnrollmentRegistryDetector
    {
        private const string AutopilotPolicyCacheKey = @"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache";
        private const string AutopilotSettingsKey = @"SOFTWARE\Microsoft\Provisioning\AutopilotSettings";

        /// <summary>
        /// <c>CloudAssignedOobeConfig</c> bits 0x20 (TPM attestation) + 0x40 (AAD device-ticket
        /// auth) — set together exclusively on self-deploying Autopilot profiles. Validated
        /// across the full platform (2026-07-02 sweep, 8983 sessions / 63 tenants): all 1197
        /// sessions carrying both bits belong to self-deploying/kiosk/shared-device profiles,
        /// zero of 6785 user-driven sessions carry them, and the two bits never occur
        /// individually. Session 320b3bf7 is the reference kiosk case this detection unblocks.
        /// </summary>
        internal const int SelfDeployingOobeConfigMask = 0x60;

        /// <summary>
        /// Reads <c>CloudAssignedDomainJoinMethod</c> from the Autopilot policy cache. Returns
        /// <c>true</c> when the profile was deployed with Hybrid Azure AD Join
        /// (<c>CloudAssignedDomainJoinMethod == 1</c>), <c>false</c> otherwise or on any error.
        /// <para>
        /// On some Windows builds (observed on Win11 23H2 Lenovo / 22631.4317) the
        /// <c>CloudAssignedDomainJoinMethod</c> value is not persisted as a top-level registry
        /// value — it lives only inside the JSON blob stored under <c>PolicyJsonCache</c>. The
        /// <c>DeviceInfoCollector</c> already handles this when emitting the
        /// <c>autopilot_profile</c> event; this detector mirrors the same fallback so the
        /// session is registered with the correct <c>IsHybridJoin</c> flag instead of a stale
        /// <c>false</c>.
        /// </para>
        /// </summary>
        public static bool DetectHybridJoin()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AutopilotPolicyCacheKey))
                {
                    if (key == null) return false;
                    return ResolveHybridJoinFromValues(key.GetValue);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pure decision logic for <see cref="DetectHybridJoin"/>. Exposed internally so the
        /// fallback can be exercised without a real registry. <paramref name="getValue"/> is
        /// expected to behave like <see cref="RegistryKey.GetValue(string)"/> — returns
        /// <c>null</c> when the named value is absent.
        /// </summary>
        internal static bool ResolveHybridJoinFromValues(Func<string, object> getValue)
        {
            if (getValue == null) return false;

            // Top-level value is authoritative when present (covers both "0" and "1").
            var topLevel = getValue("CloudAssignedDomainJoinMethod")?.ToString();
            if (topLevel != null)
            {
                return topLevel == "1";
            }

            // Fallback: on devices where the policy cache only carries the embedded JSON
            // blob, look up the same key inside PolicyJsonCache.
            var policyJson = getValue("PolicyJsonCache")?.ToString();
            if (string.IsNullOrWhiteSpace(policyJson)) return false;

            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(policyJson))
                {
                    if (doc.RootElement.TryGetProperty("CloudAssignedDomainJoinMethod", out var prop))
                    {
                        return prop.ToString() == "1";
                    }
                }
            }
            catch
            {
                // Malformed PolicyJsonCache → conservative non-HAADJ default. Detection is
                // best-effort and must never throw at session-registration time.
            }
            return false;
        }

        /// <summary>
        /// Reads <c>CloudAssignedOobeConfig</c> from the Autopilot policy cache and returns
        /// <c>true</c> when both self-deploying marker bits (<see cref="SelfDeployingOobeConfigMask"/>)
        /// are set. Mirrors the <see cref="DetectHybridJoin"/> top-level-value-then-
        /// <c>PolicyJsonCache</c>-JSON fallback, because the same Windows builds that omit the
        /// flat <c>CloudAssignedDomainJoinMethod</c> value also omit <c>CloudAssignedOobeConfig</c>.
        /// Returns <c>false</c> on any error — a missing/unreadable profile must degrade to
        /// today's classic behaviour, never to a false self-deploying classification.
        /// </summary>
        public static bool DetectSelfDeployingProfile()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AutopilotPolicyCacheKey))
                {
                    if (key == null) return false;
                    return ResolveSelfDeployingFromValues(key.GetValue);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pure decision logic for <see cref="DetectSelfDeployingProfile"/>. Exposed internally
        /// so the fallback can be exercised without a real registry (same seam contract as
        /// <see cref="ResolveHybridJoinFromValues"/>).
        /// </summary>
        internal static bool ResolveSelfDeployingFromValues(Func<string, object> getValue)
        {
            if (getValue == null) return false;

            // Top-level value is authoritative when present.
            var topLevel = getValue("CloudAssignedOobeConfig")?.ToString();
            if (topLevel != null)
            {
                return TryParseOobeConfig(topLevel, out var cfg) && HasSelfDeployingBits(cfg);
            }

            // Fallback: embedded JSON blob (mirrors the hybrid-join fallback).
            var policyJson = getValue("PolicyJsonCache")?.ToString();
            if (string.IsNullOrWhiteSpace(policyJson)) return false;

            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(policyJson))
                {
                    if (doc.RootElement.TryGetProperty("CloudAssignedOobeConfig", out var prop))
                    {
                        return TryParseOobeConfig(prop.ToString(), out var cfg) && HasSelfDeployingBits(cfg);
                    }
                }
            }
            catch
            {
                // Malformed PolicyJsonCache → conservative non-self-deploying default.
            }
            return false;
        }

        private static bool TryParseOobeConfig(string raw, out int cfg) =>
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out cfg);

        private static bool HasSelfDeployingBits(int cfg) =>
            (cfg & SelfDeployingOobeConfigMask) == SelfDeployingOobeConfigMask;

        /// <summary>
        /// Classifies the Autopilot flow based on the <c>AutopilotSettings</c> registry:
        /// <list type="bullet">
        ///   <item><c>CloudAssignedDeviceRegistration == 2</c> → <c>"v2"</c> (Windows Device Preparation).</item>
        ///   <item><c>CloudAssignedEspEnabled == 0</c> → <c>"v2"</c> (no ESP, WDP indicator).</item>
        ///   <item>Anything else → <c>"v1"</c> (Classic Autopilot / ESP).</item>
        /// </list>
        /// Defaults to <c>"v1"</c> on any error so a flaky registry does not misclassify a
        /// classic session as WDP.
        /// </summary>
        public static string DetectEnrollmentType()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AutopilotSettingsKey))
                {
                    if (key == null) return "v1";

                    var deviceReg = key.GetValue("CloudAssignedDeviceRegistration")?.ToString();
                    if (deviceReg == "2") return "v2";

                    var espEnabled = key.GetValue("CloudAssignedEspEnabled")?.ToString();
                    if (espEnabled == "0") return "v2";
                }
            }
            catch
            {
                // Fall through to v1 default.
            }
            return "v1";
        }
    }
}
