#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Interop
{
    /// <summary>
    /// Tests for the WinRT SSID reader. Its whole value is that it keeps working where the
    /// native WLAN API is denied, so the contract that matters here is fail-soft resolution:
    /// the type is bound by name at runtime (no winmd), and every step must degrade to null
    /// with a reason instead of throwing — a throw would take the Task.Run collector down
    /// silently, which is precisely the failure mode this tier exists to end.
    /// </summary>
    public sealed class WinRtWifiSsidReaderTests
    {
        [Fact]
        public void TryGetConnectedSsid_never_throws()
        {
            var ssid = WinRtWifiSsidReader.TryGetConnectedSsid(out var diagnostics);

            if (ssid == null)
                Assert.False(string.IsNullOrWhiteSpace(diagnostics), "a null SSID must carry a reason");
            else
                Assert.Null(diagnostics);
        }

        [Fact]
        public void TryGetConnectedSsid_never_returns_an_empty_ssid()
        {
            // An AP broadcasting an empty SSID must surface as null, never as "" — the caller
            // maps a non-null SSID straight into the wifi_signal_info payload.
            var ssid = WinRtWifiSsidReader.TryGetConnectedSsid(out _);

            Assert.True(ssid == null || ssid.Length > 0);
        }

        [Fact]
        public void TryGetConnectedSsid_is_stable_across_calls()
        {
            // Reflection members are resolved once and cached; a second call must take the
            // cached path and behave identically.
            var first = WinRtWifiSsidReader.TryGetConnectedSsid(out _);
            var second = WinRtWifiSsidReader.TryGetConnectedSsid(out _);

            Assert.Equal(first, second);
        }
    }
}
