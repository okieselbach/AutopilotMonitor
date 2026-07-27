using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// Audit Q5 — WMI hardware values are trimmed at the source. BIOS serials become device
    /// identity keys downstream (F2 device history joins on TenantId + serial), so padding
    /// must never leave the agent; whitespace-only values collapse to the "Unknown" sentinel
    /// the retry logic already treats as unresolved.
    /// </summary>
    public sealed class HardwareInfoNormalizeTests
    {
        [Theory]
        [InlineData(" PF60H941 ", "PF60H941")]
        [InlineData("PF60H941", "PF60H941")]
        [InlineData("\tLENOVO \r\n", "LENOVO")]
        [InlineData("Unknown", "Unknown")]
        public void Trims_whitespace_at_the_source(string raw, string expected)
            => Assert.Equal(expected, HardwareInfo.NormalizeWmiValue(raw));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void Empty_and_whitespace_only_collapse_to_the_Unknown_sentinel(string raw)
            => Assert.Equal("Unknown", HardwareInfo.NormalizeWmiValue(raw));

        [Fact]
        public void Null_collapses_to_the_Unknown_sentinel()
            => Assert.Equal("Unknown", HardwareInfo.NormalizeWmiValue(null));
    }
}
