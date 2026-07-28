#nullable enable
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using Xunit;
using static AutopilotMonitor.Agent.V2.Core.Monitoring.Interop.WlanNativeMethods;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Interop
{
    /// <summary>
    /// Tests for the native-WLAN-API WiFi reader. The WLAN state of the test machine is
    /// unknown (CI runners/VMs have no WLAN service), so the live path only pins the
    /// fail-soft contract; the pure helpers (SSID decode, PHY-type mapping) are pinned
    /// exactly — their output feeds wifi_signal_info event payloads and must stay
    /// consistent with the strings netsh historically reported.
    /// </summary>
    public sealed class WifiInfoReaderTests
    {
        [Fact]
        public void TryGetCurrentConnection_never_throws_and_returns_sane_values()
        {
            var result = WifiInfoReader.TryGetCurrentConnection();

            if (result != null)
            {
                Assert.InRange(result.SignalPercent, 0, 100);
                if (result.Channel.HasValue)
                    Assert.True(result.Channel.Value > 0, "channel must be positive when present");
            }
        }

        // ----- DecodeSsid ---------------------------------------------------

        [Fact]
        public void DecodeSsid_decodes_utf8_bytes()
        {
            var raw = new byte[32];
            var ssid = Encoding.UTF8.GetBytes("Contoso-Büro");
            ssid.CopyTo(raw, 0);

            Assert.Equal("Contoso-Büro", WifiInfoReader.DecodeSsid(raw, (uint)ssid.Length));
        }

        [Fact]
        public void DecodeSsid_returns_null_for_empty_or_missing()
        {
            Assert.Null(WifiInfoReader.DecodeSsid(null, 5));
            Assert.Null(WifiInfoReader.DecodeSsid(new byte[32], 0));
        }

        [Fact]
        public void DecodeSsid_clamps_length_to_buffer_size()
        {
            var raw = Encoding.UTF8.GetBytes("abc");

            // A corrupt native length beyond the buffer must not throw.
            Assert.Equal("abc", WifiInfoReader.DecodeSsid(raw, 200));
        }

        [Fact]
        public void DecodeSsid_uses_only_declared_length()
        {
            var raw = new byte[32];
            Encoding.UTF8.GetBytes("HomeNet-Rest-Ignored").CopyTo(raw, 0);

            Assert.Equal("HomeNet", WifiInfoReader.DecodeSsid(raw, 7));
        }

        // ----- PhyTypeToRadioType -------------------------------------------

        // int parameters (cast inside): DOT11_PHY_TYPE is internal and may not appear in a
        // public test method signature (CS0051).
        [Theory]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_ofdm, "802.11a")]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_hrdsss, "802.11b")]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_erp, "802.11g")]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_ht, "802.11n")]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_vht, "802.11ac")]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_dmg, "802.11ad")]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_he, "802.11ax")]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_eht, "802.11be")]
        public void PhyTypeToRadioType_maps_known_generations(int phyType, string expected)
        {
            Assert.Equal(expected, WifiInfoReader.PhyTypeToRadioType((DOT11_PHY_TYPE)phyType));
        }

        [Theory]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_unknown)]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_fhss)]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_dsss)]
        [InlineData((int)DOT11_PHY_TYPE.dot11_phy_type_irbaseband)]
        [InlineData(9999)]
        public void PhyTypeToRadioType_returns_null_for_legacy_or_unknown(int phyType)
        {
            Assert.Null(WifiInfoReader.PhyTypeToRadioType((DOT11_PHY_TYPE)phyType));
        }
    }
}
