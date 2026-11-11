#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.DeviceInfo
{
    /// <summary>
    /// Restart dedup of the device-info event surface: the gate decision seam
    /// (<c>ShouldEmitDeviceInfoEvent</c>) with a REAL StartupEventGate over a temp state
    /// directory. Collection side effects are out of scope here — the seam only ever wraps
    /// the event emission.
    /// </summary>
    public sealed class DeviceInfoCollectorGateTests
    {
        private static readonly System.DateTime At = new System.DateTime(2026, 6, 12, 12, 0, 0, System.DateTimeKind.Utc);

        private sealed class Rig : System.IDisposable
        {
            public DeviceInfoCollector Sut { get; }
            public StartupEventGate Gate { get; }
            private readonly TempDirectory _tmp;

            public Rig()
            {
                _tmp = new TempDirectory();
                var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
                Gate = new StartupEventGate(_tmp.Path, logger);
                var post = new InformationalEventPost(new FakeSignalIngressSink(), new VirtualClock(At));
                Sut = new DeviceInfoCollector("S1", "T1", post, logger, startupGate: Gate);
            }

            public void Dispose() => _tmp.Dispose();
        }

        [Fact]
        public void Identical_payload_is_suppressed_on_repeat_changed_payload_reemits()
        {
            using var rig = new Rig();
            var notJoined = new Dictionary<string, object> { { "joinType", "Not Joined" } };
            var joined = DeviceInfoCollector.BuildAadJoinedData("T1", "alice@contoso.com", "AABB", isPlaceholderUser: false);

            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.AadJoinStatus, notJoined));
            Assert.False(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.AadJoinStatus, notJoined));

            // The late-join path (Hybrid Join / Self-Deploying: status flips AFTER a reboot,
            // re-collected by the next agent run) MUST re-emit — only identical repeats dedup.
            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.AadJoinStatus, joined));
            Assert.False(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.AadJoinStatus, joined));
        }

        [Fact]
        public void Placeholder_replaced_by_a_user_of_the_same_domain_reemits()
        {
            // Pre-provisioning hands the device from the placeholder account to the real user
            // in the same domain — the two payloads must still differ for the gate.
            using var rig = new Rig();
            var placeholder = DeviceInfoCollector.BuildAadJoinedData("T1", "foouser@contoso.com", "AABB", isPlaceholderUser: true);
            var realUser = DeviceInfoCollector.BuildAadJoinedData("T1", "alice@contoso.com", "AABB", isPlaceholderUser: false);

            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.AadJoinStatus, placeholder));
            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.AadJoinStatus, realUser));
        }

        [Fact]
        public void Boot_time_and_wifi_signal_are_exempt_from_the_gate()
        {
            using var rig = new Rig();
            var boot = new Dictionary<string, object> { { "bootTime", "2026-06-12T11:58:00Z" } };
            var wifi = new Dictionary<string, object> { { "wifiSsid", "corp" }, { "wifiSignalPercent", 80 } };

            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.BootTime, boot));
            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.BootTime, boot));
            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.WifiSignalInfo, wifi));
            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.WifiSignalInfo, wifi));
        }

        [Fact]
        public void Network_interface_link_speed_variation_alone_does_not_reemit()
        {
            using var rig = new Rig();
            var first = new Dictionary<string, object>
            {
                { "adapterName", "WLAN" },
                { "macAddress", "AABBCCDDEEFF" },
                { "interfaceType", "Wireless80211" },
                { "linkSpeedMbps", 433L },
            };
            var sameAdapterOtherSpeed = new Dictionary<string, object>
            {
                { "adapterName", "WLAN" },
                { "macAddress", "AABBCCDDEEFF" },
                { "interfaceType", "Wireless80211" },
                { "linkSpeedMbps", 866L }, // negotiated WiFi rate churns per association
            };
            var otherAdapter = new Dictionary<string, object>
            {
                { "adapterName", "Ethernet" },
                { "macAddress", "112233445566" },
                { "interfaceType", "Ethernet" },
                { "linkSpeedMbps", 1000L },
            };

            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.NetworkInterfaceInfo, first));
            Assert.False(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.NetworkInterfaceInfo, sameAdapterOtherSpeed));
            Assert.True(rig.Sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.NetworkInterfaceInfo, otherAdapter));
        }

        [Fact]
        public void Without_a_gate_everything_emits()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var post = new InformationalEventPost(new FakeSignalIngressSink(), new VirtualClock(At));
            var sut = new DeviceInfoCollector("S1", "T1", post, logger);
            var data = new Dictionary<string, object> { { "x", 1 } };

            Assert.True(sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.OsInfo, data));
            Assert.True(sut.ShouldEmitDeviceInfoEvent(Constants.EventTypes.OsInfo, data));
        }
    }

    /// <summary>Payload of <c>aad_join_status</c> for a joined device.</summary>
    public sealed class AadJoinStatusPayloadTests
    {
        [Fact]
        public void Joined_payload_carries_the_domain_of_the_signed_in_address_only()
        {
            var data = DeviceInfoCollector.BuildAadJoinedData("T1", "alice@contoso.com", "AABB", isPlaceholderUser: false);

            Assert.Equal("Azure AD Joined", data["joinType"]);
            Assert.Equal("T1", data["tenantId"]);
            Assert.Equal("AABB", data["thumbprint"]);
            Assert.Equal("contoso.com", data["userDomain"]);
            Assert.False(data.ContainsKey("isFooUser"));
            Assert.DoesNotContain(data.Values, v => v is string s && s.Contains("alice"));
        }

        [Fact]
        public void Joined_payload_flags_the_placeholder_and_omits_the_domain_without_an_address()
        {
            var placeholder = DeviceInfoCollector.BuildAadJoinedData("T1", "foouser@contoso.com", "AABB", isPlaceholderUser: true);
            Assert.Equal(true, placeholder["isFooUser"]);
            Assert.Equal("contoso.com", placeholder["userDomain"]);

            var deviceOnly = DeviceInfoCollector.BuildAadJoinedData("T1", null!, "AABB", isPlaceholderUser: false);
            Assert.False(deviceOnly.ContainsKey("userDomain"));
            Assert.False(deviceOnly.ContainsKey("isFooUser"));
        }
    }

    /// <summary>Inventory fingerprint used by the SoftwareInventoryAnalyzer's restart dedup.</summary>
    public sealed class SoftwareInventoryFingerprintTests
    {
        private static SoftwareInventoryAnalyzer.SoftwareEntry Entry(string name, string version, string publisher = "Contoso") =>
            new SoftwareInventoryAnalyzer.SoftwareEntry { DisplayName = name, DisplayVersion = version, Publisher = publisher };

        [Fact]
        public void Fingerprint_is_stable_across_collection_order()
        {
            // Registry enumeration order is not guaranteed — the fingerprint must not depend on it.
            var a = new List<SoftwareInventoryAnalyzer.SoftwareEntry> { Entry("App A", "1.0"), Entry("App B", "2.0") };
            var b = new List<SoftwareInventoryAnalyzer.SoftwareEntry> { Entry("App B", "2.0"), Entry("App A", "1.0") };

            Assert.Equal(
                SoftwareInventoryAnalyzer.BuildInventoryFingerprint(a),
                SoftwareInventoryAnalyzer.BuildInventoryFingerprint(b));
        }

        [Fact]
        public void Fingerprint_changes_on_new_entry_or_version_change()
        {
            var baseline = new List<SoftwareInventoryAnalyzer.SoftwareEntry> { Entry("App A", "1.0") };
            var withNewApp = new List<SoftwareInventoryAnalyzer.SoftwareEntry> { Entry("App A", "1.0"), Entry("App B", "2.0") };
            var withUpgrade = new List<SoftwareInventoryAnalyzer.SoftwareEntry> { Entry("App A", "1.1") };

            var baseFp = SoftwareInventoryAnalyzer.BuildInventoryFingerprint(baseline);
            Assert.NotEqual(baseFp, SoftwareInventoryAnalyzer.BuildInventoryFingerprint(withNewApp));
            Assert.NotEqual(baseFp, SoftwareInventoryAnalyzer.BuildInventoryFingerprint(withUpgrade));
        }
    }
}
