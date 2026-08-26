#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Interop
{
    /// <summary>
    /// Tests for the tiered WiFi reader. The WLAN state of the test machine is unknown (CI
    /// runners have no WLAN service, dev boxes may be on WiFi with or without location consent),
    /// so the live path only pins the fail-soft contract. The diagnostics composer is pinned
    /// exactly — it is the only field evidence for a missing wifi_signal_info event, and the
    /// ERROR_ACCESS_DENIED hint is what tells an operator that Windows 11 24H2's location gate,
    /// not the hardware, swallowed the SSID.
    /// </summary>
    public sealed class WifiInfoProviderTests
    {
        [Fact]
        public void TryRead_never_throws_and_reports_a_reason_when_it_yields_nothing()
        {
            var result = WifiInfoProvider.TryRead(null, out var diagnostics);

            if (result == null)
            {
                Assert.False(string.IsNullOrWhiteSpace(diagnostics),
                    "a null result must always explain itself — that string is the only field evidence");
                Assert.Contains("native:", diagnostics);
                Assert.Contains("WinRT:", diagnostics);
                Assert.Contains("netsh:", diagnostics);
            }
            else
            {
                Assert.Null(diagnostics);
                // Every tier either carries an SSID or was not supposed to return at all.
                Assert.False(result.Ssid == null && result.SignalPercent == null
                             && result.RadioType == null && result.Channel == null,
                    "an all-null payload must surface as null, not as an empty connection");
            }
        }

        // ----- BuildDiagnostics ---------------------------------------------

        [Fact]
        public void BuildDiagnostics_names_every_tier()
        {
            var text = WifiInfoProvider.BuildDiagnostics(null, null);

            Assert.Contains("no WiFi info from any reader", text);
            Assert.Contains("native: no connected WLAN interface", text);
            Assert.Contains("WinRT: no SSID", text);
            Assert.Contains("netsh: no parsable output", text);
        }

        [Fact]
        public void BuildDiagnostics_passes_through_tier_reasons()
        {
            var text = WifiInfoProvider.BuildDiagnostics("WlanOpenHandle rc=1062", "WinRT NetworkInformation unavailable");

            Assert.Contains("native: WlanOpenHandle rc=1062", text);
            Assert.Contains("WinRT: WinRT NetworkInformation unavailable", text);
        }

        [Fact]
        public void BuildDiagnostics_explains_access_denied_as_the_location_gate()
        {
            var text = WifiInfoProvider.BuildDiagnostics(
                "[1cbc79c3-2e31-468a-b949-17fafa6e23ed] query(current_connection) rc=5; ", null);

            Assert.Contains("ERROR_ACCESS_DENIED", text);
            Assert.Contains("24H2", text);
            Assert.Contains("Location services", text);
        }

        [Fact]
        public void BuildDiagnostics_explains_access_denied_without_a_trailing_separator()
        {
            var text = WifiInfoProvider.BuildDiagnostics("WlanOpenHandle rc=5", null);

            Assert.Contains("ERROR_ACCESS_DENIED", text);
        }

        [Theory]
        [InlineData("query(current_connection) rc=50; ")]
        [InlineData("query(current_connection) rc=5023; ")]
        [InlineData("WlanEnumInterfaces rc=1062")]
        [InlineData("[guid] state=wlan_interface_state_disconnected; ")]
        public void BuildDiagnostics_does_not_blame_the_location_gate_for_other_codes(string nativeDiag)
        {
            var text = WifiInfoProvider.BuildDiagnostics(nativeDiag, null);

            Assert.DoesNotContain("ERROR_ACCESS_DENIED", text);
            Assert.Contains($"native: {nativeDiag}", text);
        }
    }
}
