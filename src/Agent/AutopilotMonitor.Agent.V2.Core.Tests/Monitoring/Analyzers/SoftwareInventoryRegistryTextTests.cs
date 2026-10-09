#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// Uninstall-key strings are read the way Windows shows them: up to the first NUL. Installers
    /// that pad REG_SZ data with NULs otherwise leak them into the normalized vendor/name, which
    /// the backend turns into a table key.
    /// </summary>
    public sealed class SoftwareInventoryRegistryTextTests
    {
        [Theory]
        [InlineData("Contoso Widget", "Contoso Widget")]
        [InlineData("Contoso Widget\0\0\0\0\0", "Contoso Widget")]
        [InlineData("Contoso\0stale buffer", "Contoso")]
        [InlineData("\0\0\0", "")]
        [InlineData("", "")]
        public void CutAtNul_KeepsTheTextBeforeTheFirstNul(string raw, string expected)
        {
            Assert.Equal(expected, SoftwareInventoryAnalyzer.CutAtNul(raw));
        }

        [Fact]
        public void CutAtNul_Null_StaysNull()
        {
            Assert.Null(SoftwareInventoryAnalyzer.CutAtNul(null!));
        }
    }
}
