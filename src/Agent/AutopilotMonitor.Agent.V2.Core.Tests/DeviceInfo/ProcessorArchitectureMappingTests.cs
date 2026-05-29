using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.DeviceInfo
{
    /// <summary>
    /// Win32_Processor.Architecture (== PROCESSOR_ARCHITECTURE) maps to the
    /// human-readable cpuArchitecture value carried on the hardware_spec event.
    /// </summary>
    public sealed class ProcessorArchitectureMappingTests
    {
        [Theory]
        [InlineData(0, "x86")]
        [InlineData(5, "ARM")]
        [InlineData(6, "ia64")]
        [InlineData(9, "x64")]
        [InlineData(12, "ARM64")]
        public void Maps_known_architecture_codes(int code, string expected)
        {
            Assert.Equal(expected, DeviceInfoCollector.MapProcessorArchitecture(code));
        }

        [Theory]
        [InlineData(1)]   // MIPS
        [InlineData(3)]   // PowerPC
        [InlineData(99)]  // anything unexpected
        [InlineData(-1)]
        public void Unknown_codes_map_to_Unknown(int code)
        {
            Assert.Equal("Unknown", DeviceInfoCollector.MapProcessorArchitecture(code));
        }
    }
}
