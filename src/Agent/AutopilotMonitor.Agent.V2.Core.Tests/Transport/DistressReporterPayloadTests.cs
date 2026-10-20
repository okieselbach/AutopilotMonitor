using System;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// What a distress report carries and that it fits the backend's body cap: the endpoint drops
    /// a larger body with 200, so an oversized report would be lost without a trace.
    /// </summary>
    public sealed class DistressReporterPayloadTests
    {
        private static DistressReporter NewReporter(string field = "x", string agentVersion = "2.0.1500")
            => new DistressReporter(
                baseUrl: "https://example.test",
                tenantId: "11111111-2222-3333-4444-555555555555",
                manufacturer: field,
                model: field,
                serialNumber: field,
                agentVersion: agentVersion,
                logger: null!);

        [Fact]
        public void Report_carries_os_build_and_setup_state_at_send_time()
        {
            using (var reporter = NewReporter())
            {
                var report = reporter.BuildReport(DistressErrorType.AuthCertificateRejected, "m", 401, tpmVersion: null);

                var osBuild = DeviceInfoProvider.GetOsBuild();
                Assert.Equal(string.IsNullOrEmpty(osBuild) ? null : osBuild, report.OsBuild);
                Assert.Equal(OobeStateReader.Read(), report.OobeState);
                Assert.Null(report.TpmVersion);
            }
        }

        [Fact]
        public void Tpm_version_passes_through_capped_at_48()
        {
            using (var reporter = NewReporter())
            {
                Assert.Equal("IFX 7.63.3353.0",
                    reporter.BuildReport(DistressErrorType.TpmPssUnsupported, "m", null, "IFX 7.63.3353.0").TpmVersion);
                Assert.Equal(48,
                    reporter.BuildReport(DistressErrorType.TpmPssUnsupported, "m", null, new string('v', 60)).TpmVersion!.Length);
            }
        }

        [Fact]
        public void Largest_ascii_report_fits_the_cap_untouched()
        {
            // Every field at the agent's own caps; the agent version as built (not cut to 32).
            using (var reporter = NewReporter(new string('A', 64), "2.0.1486+d95640c597cb90849803dd65afda19e2d7a39444"))
            {
                var report = reporter.BuildReport(DistressErrorType.ClientCertificateNotReceived, new string('m', 256), 403, new string('t', 48));
                report.CertThumbprint = new string('F', 40);
                report.CertSubject = new string('S', 96);
                report.CertIssuer = new string('I', 96);
                report.CertNotBefore = DateTime.UtcNow;
                report.CertNotAfter = DateTime.UtcNow;
                report.OsBuild = "26300.97111";
                report.OobeState = "unknown_2147483647".Substring(0, 16);

                var json = DistressReporter.SerializeWithinCap(report);

                Assert.Equal(256, report.Message!.Length);
                Assert.True(Encoding.UTF8.GetByteCount(json) <= Constants.DistressReportMaxBytes,
                    $"{Encoding.UTF8.GetByteCount(json)} bytes");
            }
        }

        [Fact]
        public void Oversized_report_gives_up_message_characters_to_fit()
        {
            // Three-byte characters everywhere — far past the cap before trimming.
            using (var reporter = NewReporter(new string('€', 64)))
            {
                var report = reporter.BuildReport(DistressErrorType.ClientCertificateNotReceived, new string('€', 256), 403, null);
                report.CertSubject = new string('€', 96);
                report.CertIssuer = new string('€', 96);
                Assert.True(Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(report)) > Constants.DistressReportMaxBytes);

                var json = DistressReporter.SerializeWithinCap(report);

                Assert.True(Encoding.UTF8.GetByteCount(json) <= Constants.DistressReportMaxBytes,
                    $"{Encoding.UTF8.GetByteCount(json)} bytes");
                Assert.True(report.Message!.Length < 256);
                Assert.Equal(report.Message, JsonConvert.DeserializeObject<DistressReport>(json)!.Message);
            }
        }
    }
}
