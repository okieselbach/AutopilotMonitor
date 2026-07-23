#nullable enable
using System.Collections.Generic;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Pins the pure parts of the UEFI Secure Boot certificate probe: the ASCII substring
    /// search over raw signature-list bytes and the <c>secureboot_status</c> payload shaping
    /// (in particular the one-sided <c>uefiCa2023FirmwareConfirmed</c> marker whose ABSENCE
    /// is load-bearing for ANALYZE-SEC-001's not_exists precondition). The actual firmware
    /// read is only smoke-tested: test machines may be VMs, non-elevated, or legacy BIOS,
    /// so only the fail-soft contract is asserted.
    /// </summary>
    public sealed class UefiSecureBootCertReaderTests
    {
        // --- ContainsAscii ---

        [Fact]
        public void ContainsAscii_finds_needle_embedded_in_binary_noise()
        {
            var bytes = Concat(new byte[] { 0x00, 0xA1, 0x30, 0x82 },
                Encoding.ASCII.GetBytes("Windows UEFI CA 2023"),
                new byte[] { 0x00, 0xFF });

            Assert.True(UefiSecureBootCertReader.ContainsAscii(bytes, "Windows UEFI CA 2023"));
        }

        [Fact]
        public void ContainsAscii_finds_needle_at_the_very_end()
        {
            var bytes = Concat(new byte[] { 0x01, 0x02 }, Encoding.ASCII.GetBytes("KEK 2K CA 2023"));

            Assert.True(UefiSecureBootCertReader.ContainsAscii(bytes, "KEK 2K CA 2023"));
        }

        [Fact]
        public void ContainsAscii_rejects_partial_overlap_and_absent_needle()
        {
            var bytes = Encoding.ASCII.GetBytes("Windows UEFI CA 2011 something else");

            Assert.False(UefiSecureBootCertReader.ContainsAscii(bytes, "Windows UEFI CA 2023"));
            // Repeated prefix bytes must not confuse the scan.
            var tricky = Encoding.ASCII.GetBytes("ababac");
            Assert.True(UefiSecureBootCertReader.ContainsAscii(tricky, "abac"));
        }

        [Fact]
        public void ContainsAscii_handles_null_empty_and_oversized_needle()
        {
            Assert.False(UefiSecureBootCertReader.ContainsAscii(null, "x"));
            Assert.False(UefiSecureBootCertReader.ContainsAscii(new byte[0], "x"));
            Assert.False(UefiSecureBootCertReader.ContainsAscii(new byte[] { 1, 2 }, ""));
            Assert.False(UefiSecureBootCertReader.ContainsAscii(new byte[] { 1, 2 }, "abc"));
        }

        // --- Payload shaping ---

        [Fact]
        public void Marker_emitted_only_when_db_ok_and_ca2023_present()
        {
            var data = new Dictionary<string, object>();
            DeviceInfoCollector.AppendFirmwareCertFields(data, new UefiSecureBootCertSnapshot
            {
                DbStatus = UefiSecureBootCertReader.StatusOk,
                KekStatus = UefiSecureBootCertReader.StatusOk,
                DbHasWindowsUefiCa2023 = true,
                KekHasMicrosoftKek2kCa2023 = true
            });

            Assert.Equal(true, data["uefiCa2023FirmwareConfirmed"]);
            Assert.Equal("ok", data["uefiFirmwareReadStatus"]);
            Assert.Equal(true, data["uefiDbHasWindowsUefiCa2023"]);
            Assert.Equal(true, data["uefiKekHasMicrosoftKek2kCa2023"]);
        }

        [Fact]
        public void Marker_absent_when_ca2023_missing_even_though_read_succeeded()
        {
            var data = new Dictionary<string, object>();
            DeviceInfoCollector.AppendFirmwareCertFields(data, new UefiSecureBootCertSnapshot
            {
                DbStatus = UefiSecureBootCertReader.StatusOk,
                KekStatus = UefiSecureBootCertReader.StatusOk,
                DbHasWindowsUefiCa2023 = false,
                DbHasWindowsProductionPca2011 = true
            });

            // The marker must be OMITTED, not false — not_exists fails on any present value.
            Assert.False(data.ContainsKey("uefiCa2023FirmwareConfirmed"));
            Assert.Equal(false, data["uefiDbHasWindowsUefiCa2023"]);
            Assert.Equal(true, data["uefiDbHasWindowsProductionPca2011"]);
        }

        [Fact]
        public void Failed_db_read_emits_status_but_no_cert_booleans_and_no_marker()
        {
            var data = new Dictionary<string, object>();
            DeviceInfoCollector.AppendFirmwareCertFields(data, new UefiSecureBootCertSnapshot
            {
                DbStatus = UefiSecureBootCertReader.StatusPrivilegeDenied,
                KekStatus = UefiSecureBootCertReader.StatusPrivilegeDenied
            });

            Assert.Equal(UefiSecureBootCertReader.StatusPrivilegeDenied, data["uefiFirmwareReadStatus"]);
            Assert.False(data.ContainsKey("uefiDbHasWindowsUefiCa2023"));
            Assert.False(data.ContainsKey("uefiKekHasMicrosoftKek2kCa2023"));
            Assert.False(data.ContainsKey("uefiCa2023FirmwareConfirmed"));
        }

        [Fact]
        public void Db_ok_but_kek_failed_keeps_db_booleans_and_reports_kek_status()
        {
            var data = new Dictionary<string, object>();
            DeviceInfoCollector.AppendFirmwareCertFields(data, new UefiSecureBootCertSnapshot
            {
                DbStatus = UefiSecureBootCertReader.StatusOk,
                KekStatus = UefiSecureBootCertReader.StatusVariableNotFound,
                DbHasWindowsUefiCa2023 = true
            });

            Assert.Equal(UefiSecureBootCertReader.StatusVariableNotFound, data["uefiFirmwareReadStatus"]);
            Assert.Equal(true, data["uefiDbHasWindowsUefiCa2023"]);
            Assert.Equal(true, data["uefiCa2023FirmwareConfirmed"]);
            Assert.False(data.ContainsKey("uefiKekHasMicrosoftKek2kCa2023"));
        }

        [Fact]
        public void Null_snapshot_leaves_payload_untouched()
        {
            var data = new Dictionary<string, object> { { "uefiSecureBootEnabled", true } };
            DeviceInfoCollector.AppendFirmwareCertFields(data, null);

            Assert.Single(data);
        }

        [Fact]
        public void DescribeFirmwareVerdict_covers_all_shapes()
        {
            Assert.Equal("unavailable", DeviceInfoCollector.DescribeFirmwareVerdict(null));
            Assert.Equal(UefiSecureBootCertReader.StatusNotUefi, DeviceInfoCollector.DescribeFirmwareVerdict(
                new UefiSecureBootCertSnapshot { DbStatus = UefiSecureBootCertReader.StatusNotUefi }));
            Assert.Equal("CA2023 confirmed", DeviceInfoCollector.DescribeFirmwareVerdict(
                new UefiSecureBootCertSnapshot { DbStatus = UefiSecureBootCertReader.StatusOk, DbHasWindowsUefiCa2023 = true }));
            Assert.Equal("CA2023 missing", DeviceInfoCollector.DescribeFirmwareVerdict(
                new UefiSecureBootCertSnapshot { DbStatus = UefiSecureBootCertReader.StatusOk, DbHasWindowsUefiCa2023 = false }));
        }

        // --- Reader smoke (real firmware; machine state unknown) ---

        [Fact]
        public void Read_never_throws_and_reports_a_status_for_both_variables()
        {
            var snapshot = UefiSecureBootCertReader.Read();

            Assert.NotNull(snapshot);
            Assert.False(string.IsNullOrEmpty(snapshot.DbStatus));
            Assert.False(string.IsNullOrEmpty(snapshot.KekStatus));
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var total = 0;
            foreach (var part in parts) total += part.Length;
            var result = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                System.Array.Copy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }
    }
}
