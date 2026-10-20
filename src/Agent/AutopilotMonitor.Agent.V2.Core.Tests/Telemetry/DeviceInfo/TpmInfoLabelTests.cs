using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.DeviceInfo
{
    public sealed class TpmInfoLabelTests
    {
        [Theory]
        [InlineData("IFX", "7.63.3353.0", "IFX 7.63.3353.0")]
        [InlineData("STM ", "73.4.17568.4452", "STM 73.4.17568.4452")] // ManufacturerIdTxt is space-padded
        [InlineData("INTC", null, "INTC")]
        [InlineData(null, "403.1.0.0", "403.1.0.0")]
        [InlineData(" ", " ", null)]
        [InlineData(null, null, null)]
        public void Label_is_trimmed_manufacturer_and_firmware(string? name, string? version, string? expected)
        {
            Assert.Equal(expected, TpmInfo.FormatLabel(name!, version!));
        }
    }
}
