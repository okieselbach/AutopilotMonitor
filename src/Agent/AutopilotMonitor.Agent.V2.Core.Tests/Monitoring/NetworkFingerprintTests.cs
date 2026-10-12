using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// The local network fingerprint a failed registration is stored with: SHA-256 over the
    /// connection type and gateway <see cref="NetworkChangeDetector"/> captures, cut to 16 hex
    /// characters; unknown (null) whenever either input is missing. Gateways are documentation
    /// addresses (RFC 5737).
    /// </summary>
    public sealed class NetworkFingerprintTests
    {
        private static NetworkChangeDetector.NetworkStateSnapshot State(string? connectionType, string? gateway, bool hasNetwork = true) =>
            new NetworkChangeDetector.NetworkStateSnapshot
            {
                HasNetwork = hasNetwork,
                ConnectionType = connectionType,
                Gateway = gateway,
            };

        [Fact]
        public void Same_connection_type_and_gateway_give_the_same_short_hex_fingerprint()
        {
            var first = NetworkFingerprint.Of(State("WiFi", "192.0.2.1"));
            var second = NetworkFingerprint.Of(State("WiFi", "192.0.2.1"));

            Assert.Equal(first, second);
            Assert.Equal(NetworkFingerprint.Length, first!.Length);
            Assert.True(first.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), first);
        }

        [Theory]
        [InlineData("WiFi", "198.51.100.1")] // another gateway
        [InlineData("Ethernet", "192.0.2.1")] // another connection type
        public void Another_gateway_or_connection_type_is_another_network(string connectionType, string gateway)
        {
            Assert.NotEqual(
                NetworkFingerprint.Of(State("WiFi", "192.0.2.1")),
                NetworkFingerprint.Of(State(connectionType, gateway)));
        }

        [Fact]
        public void Unknown_network_has_no_fingerprint()
        {
            Assert.Null(NetworkFingerprint.Of(null));
            Assert.Null(NetworkFingerprint.Of(State("WiFi", "192.0.2.1", hasNetwork: false)));
            Assert.Null(NetworkFingerprint.Of(State("WiFi", null)));
            Assert.Null(NetworkFingerprint.Of(State(null, "192.0.2.1")));
        }

        [Theory]
        [InlineData("aaaa", "aaaa", false)]
        [InlineData("aaaa", "bbbb", true)]
        public void Differs_compares_known_fingerprints(string before, string after, bool expected)
        {
            Assert.Equal(expected, NetworkFingerprint.Differs(before, after));
        }

        [Theory]
        [InlineData(null, "aaaa")]
        [InlineData("aaaa", null)]
        [InlineData(null, null)]
        public void Differs_is_unknown_when_either_side_is(string? before, string? after)
        {
            Assert.Null(NetworkFingerprint.Differs(before, after));
        }

        [Fact]
        public void TryCapture_reads_this_machine_without_throwing()
        {
            // Machine-dependent: either no active interface (null) or a well-formed fingerprint
            // that matches the detector's own capture.
            var captured = NetworkFingerprint.TryCapture();

            if (captured != null)
            {
                Assert.Equal(NetworkFingerprint.Length, captured.Length);
                Assert.Equal(NetworkFingerprint.Of(NetworkChangeDetector.CaptureInterfaceState()), captured);
            }
        }
    }
}
