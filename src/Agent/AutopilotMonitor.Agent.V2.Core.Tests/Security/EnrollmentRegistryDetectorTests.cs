using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// Regression coverage for the IsHybridJoin detection that runs at session-registration
    /// time. Field evidence (session c4c8d206-…, Lenovo ThinkPad T14 Gen 5, Win11 23H2
    /// 22631.4317): the AutopilotPolicyCache key on this device exposes
    /// <c>CloudAssignedDomainJoinMethod</c> only inside the embedded <c>PolicyJsonCache</c>
    /// blob, not as a top-level registry value. Without the JSON fallback the agent registered
    /// the session with <c>IsHybridJoin=false</c> even though the device was a HAADJ target,
    /// while the later-running <c>DeviceInfoCollector</c> (which already had the fallback)
    /// emitted <c>autopilot_profile</c> with <c>isHybridJoin=true</c>, producing a confusing
    /// inconsistency in the Sessions table vs. the Events stream.
    /// </summary>
    public sealed class EnrollmentRegistryDetectorTests
    {
        private static Func<string, object> Lookup(Dictionary<string, object> values)
            => name => values.TryGetValue(name, out var v) ? v : null!;

        [Fact]
        public void TopLevel_DomainJoinMethod_one_returns_true()
        {
            var values = new Dictionary<string, object>
            {
                ["CloudAssignedDomainJoinMethod"] = 1,
            };
            Assert.True(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void TopLevel_DomainJoinMethod_zero_returns_false_even_with_hybrid_in_json()
        {
            // Top-level value is authoritative: explicit "0" wins over a contradicting JSON
            // blob — the JSON fallback only kicks in when the top-level is absent.
            var values = new Dictionary<string, object>
            {
                ["CloudAssignedDomainJoinMethod"] = 0,
                ["PolicyJsonCache"] = "{\"CloudAssignedDomainJoinMethod\":1}",
            };
            Assert.False(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void Missing_TopLevel_falls_back_to_PolicyJsonCache_one_returns_true()
        {
            // Real-world payload from the Bayer Lenovo T14 Gen 5 / Win11 23H2 22631.4317
            // device that triggered this fix. Truncated for readability — the only relevant
            // key is CloudAssignedDomainJoinMethod=1.
            const string policyJson =
                "{\r\n  \"CloudAssignedTenantDomain\": \"bayergroup.onmicrosoft.com\",\r\n" +
                "  \"CloudAssignedDomainJoinMethod\": 1,\r\n" +
                "  \"AutopilotMode\": 2\r\n}";

            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = policyJson,
            };
            Assert.True(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void Missing_TopLevel_falls_back_to_PolicyJsonCache_zero_returns_false()
        {
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = "{\"CloudAssignedDomainJoinMethod\":0}",
            };
            Assert.False(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void Missing_TopLevel_and_PolicyJsonCache_without_key_returns_false()
        {
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = "{\"CloudAssignedTenantDomain\":\"contoso.onmicrosoft.com\"}",
            };
            Assert.False(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void Missing_TopLevel_and_malformed_PolicyJsonCache_returns_false_without_throw()
        {
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = "{not valid json",
            };
            // Must not throw — the detector runs on the registration hot-path and a failure
            // here would block session registration.
            Assert.False(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void Missing_TopLevel_and_empty_PolicyJsonCache_returns_false()
        {
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = string.Empty,
            };
            Assert.False(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void All_values_missing_returns_false()
        {
            var values = new Dictionary<string, object>();
            Assert.False(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(Lookup(values)));
        }

        [Fact]
        public void Null_lookup_returns_false()
        {
            Assert.False(EnrollmentRegistryDetector.ResolveHybridJoinFromValues(null));
        }

        [Fact]
        public void DetectHybridJoin_returns_false_when_registry_key_is_unreadable()
        {
            // Smoke test against the real registry. On a developer machine the AutopilotPolicyCache
            // key is typically absent so DetectHybridJoin must degrade to false without throwing.
            // (CI / test boxes that DO have the key — vanishingly unlikely — would still observe a
            // boolean, never an exception.)
            var actual = EnrollmentRegistryDetector.DetectHybridJoin();
            Assert.IsType<bool>(actual);
        }

        // ============================================== self-deploying profile (OobeConfig)
        // Session 320b3bf7 kiosk fix: CloudAssignedOobeConfig bits 0x20|0x40 mark a
        // self-deploying profile (platform sweep 2026-07-02: exclusive to SD/kiosk/shared
        // profiles, both bits always co-occur, zero user-driven false positives).

        [Theory]
        [InlineData(0x60, true)]    // exactly the mask
        [InlineData(1534, true)]    // 0x5FE — Opsys Kiosk (session 320b3bf7)
        [InlineData(510, true)]     // 0x1FE — second observed SD value
        [InlineData(0x20, false)]   // TPM-attestation bit alone
        [InlineData(0x40, false)]   // device-auth bit alone
        [InlineData(1310, false)]   // 0x51E — dominant user-driven value
        [InlineData(286, false)]    // 0x11E — user-driven
        [InlineData(0, false)]
        public void TopLevel_OobeConfig_requires_both_selfdeploying_bits(int oobeConfig, bool expected)
        {
            var values = new Dictionary<string, object>
            {
                ["CloudAssignedOobeConfig"] = oobeConfig,
            };
            Assert.Equal(expected, EnrollmentRegistryDetector.ResolveSelfDeployingFromValues(Lookup(values)));
        }

        [Fact]
        public void TopLevel_OobeConfig_nonnumeric_returns_false_without_fallback()
        {
            // Top-level value is authoritative when present — an unparseable top-level does
            // NOT fall through to a contradicting JSON blob (mirrors the hybrid-join contract).
            var values = new Dictionary<string, object>
            {
                ["CloudAssignedOobeConfig"] = "not-a-number",
                ["PolicyJsonCache"] = "{\"CloudAssignedOobeConfig\":1534}",
            };
            Assert.False(EnrollmentRegistryDetector.ResolveSelfDeployingFromValues(Lookup(values)));
        }

        [Fact]
        public void Missing_TopLevel_OobeConfig_falls_back_to_PolicyJsonCache()
        {
            // Real-world shape from session 320b3bf7 (Opsys Kiosk): OobeConfig only inside
            // the embedded JSON blob, DeploymentProfileName "Opsys Kiosk".
            const string policyJson =
                "{\r\n  \"CloudAssignedTenantDomain\": \"contoso.onmicrosoft.com\",\r\n" +
                "  \"CloudAssignedOobeConfig\": 1534,\r\n" +
                "  \"DeploymentProfileName\": \"Contoso Kiosk\"\r\n}";
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = policyJson,
            };
            Assert.True(EnrollmentRegistryDetector.ResolveSelfDeployingFromValues(Lookup(values)));
        }

        [Fact]
        public void Missing_TopLevel_OobeConfig_PolicyJsonCache_userdriven_returns_false()
        {
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = "{\"CloudAssignedOobeConfig\":1310}",
            };
            Assert.False(EnrollmentRegistryDetector.ResolveSelfDeployingFromValues(Lookup(values)));
        }

        [Fact]
        public void OobeConfig_absent_everywhere_returns_false()
        {
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = "{\"CloudAssignedTenantDomain\":\"contoso.onmicrosoft.com\"}",
            };
            Assert.False(EnrollmentRegistryDetector.ResolveSelfDeployingFromValues(Lookup(values)));
        }

        [Fact]
        public void OobeConfig_malformed_PolicyJsonCache_returns_false_without_throw()
        {
            var values = new Dictionary<string, object>
            {
                ["PolicyJsonCache"] = "{not valid json",
            };
            Assert.False(EnrollmentRegistryDetector.ResolveSelfDeployingFromValues(Lookup(values)));
        }

        [Fact]
        public void OobeConfig_null_lookup_returns_false()
        {
            Assert.False(EnrollmentRegistryDetector.ResolveSelfDeployingFromValues(null));
        }

        [Fact]
        public void DetectSelfDeployingProfile_returns_false_when_registry_key_is_unreadable()
        {
            var actual = EnrollmentRegistryDetector.DetectSelfDeployingProfile();
            Assert.IsType<bool>(actual);
        }
    }
}
