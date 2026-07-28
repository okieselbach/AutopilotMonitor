#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Interop
{
    /// <summary>
    /// Tests for the pure netsh-output parser of the WiFi fallback path. netsh labels follow
    /// the OS UI language — the parser must extract SSID/signal/radio/channel from the label
    /// variants of the languages commonly seen in enrollments, and degrade gracefully (fields
    /// absent, never throw) on unrecognized ones.
    /// </summary>
    public sealed class NetshWifiFallbackTests
    {
        [Fact]
        public void Parse_english_output_extracts_all_fields()
        {
            const string output = @"
There is 1 interface on the system:

    Name                   : Wi-Fi
    Description            : Intel(R) Wi-Fi 6 AX201 160MHz
    GUID                   : 754b6c07-5ae9-49d5-ba6c-4fbd6e38bbf6
    State                  : connected
    SSID                   : contoso-corp
    AP BSSID               : 3c:37:12:53:c8:44
    Band                   : 5 GHz
    Channel                : 116
    Radio type             : 802.11ax
    Signal                 : 94%
";
            var r = NetshWifiFallback.Parse(output);

            Assert.NotNull(r);
            Assert.Equal("contoso-corp", r!.Ssid);
            Assert.Equal(94, r.SignalPercent);
            Assert.Equal("802.11ax", r.RadioType);
            Assert.Equal(116, r.Channel);
        }

        [Fact]
        public void Parse_german_output_extracts_all_fields()
        {
            const string output = @"
Es ist 1 Schnittstelle auf dem System vorhanden:

    Name                   : WLAN
    Beschreibung           : Intel(R) Wi-Fi 6 AX201 160MHz
    Status                 : Verbunden
    SSID                   : fabrikam-wlan
    Kanal                  : 44
    Funktyp                : 802.11ac
    Signal                 : 87%
";
            var r = NetshWifiFallback.Parse(output);

            Assert.NotNull(r);
            Assert.Equal("fabrikam-wlan", r!.Ssid);
            Assert.Equal(87, r.SignalPercent);
            Assert.Equal("802.11ac", r.RadioType);
            Assert.Equal(44, r.Channel);
        }

        [Fact]
        public void Parse_spanish_output_extracts_signal_and_channel()
        {
            const string output = @"
    Nombre                 : Wi-Fi
    SSID                   : contoso-es
    Canal                  : 6
    Tipo de radio          : 802.11n
    Señal                  : 72%
";
            var r = NetshWifiFallback.Parse(output);

            Assert.NotNull(r);
            Assert.Equal("contoso-es", r!.Ssid);
            Assert.Equal(72, r.SignalPercent);
            Assert.Equal("802.11n", r.RadioType);
            Assert.Equal(6, r.Channel);
        }

        [Fact]
        public void Parse_chinese_simplified_output_extracts_all_fields()
        {
            const string output = @"
    名称                   : WLAN
    SSID                   : contoso-cn
    信道                   : 149
    无线电类型             : 802.11ac
    信号                   : 68%
";
            var r = NetshWifiFallback.Parse(output);

            Assert.NotNull(r);
            Assert.Equal("contoso-cn", r!.Ssid);
            Assert.Equal(68, r.SignalPercent);
            Assert.Equal("802.11ac", r.RadioType);
            Assert.Equal(149, r.Channel);
        }

        [Fact]
        public void Parse_korean_output_extracts_all_fields()
        {
            const string output = @"
    이름                   : Wi-Fi
    SSID                   : contoso-kr
    채널                   : 36
    라디오 종류            : 802.11ax
    신호                   : 91%
";
            var r = NetshWifiFallback.Parse(output);

            Assert.NotNull(r);
            Assert.Equal("contoso-kr", r!.Ssid);
            Assert.Equal(91, r.SignalPercent);
            Assert.Equal("802.11ax", r.RadioType);
            Assert.Equal(36, r.Channel);
        }

        [Fact]
        public void Parse_signal_with_space_before_percent_is_parsed()
        {
            var r = NetshWifiFallback.Parse("    Signal : 81 %\r\n    SSID : x\r\n");

            Assert.NotNull(r);
            Assert.Equal(81, r!.SignalPercent);
        }

        [Fact]
        public void Parse_ap_bssid_does_not_overwrite_ssid()
        {
            const string output = @"
    SSID                   : contoso-corp
    AP BSSID               : aa:bb:cc:dd:ee:ff
";
            var r = NetshWifiFallback.Parse(output);

            Assert.Equal("contoso-corp", r!.Ssid);
        }

        [Fact]
        public void Parse_finnish_output_extracts_all_fields()
        {
            const string output = @"
    Nimi                   : Wi-Fi
    SSID                   : contoso-fi
    Kanava                 : 100
    Radiotyyppi            : 802.11ax
    Signaali               : 79%
";
            var r = NetshWifiFallback.Parse(output);

            Assert.NotNull(r);
            Assert.Equal("contoso-fi", r!.Ssid);
            Assert.Equal(79, r.SignalPercent);
            Assert.Equal("802.11ax", r.RadioType);
            Assert.Equal(100, r.Channel);
        }

        [Fact]
        public void Parse_indonesian_output_extracts_all_fields()
        {
            const string output = @"
    Nama                   : Wi-Fi
    SSID                   : contoso-id
    Saluran                : 11
    Jenis radio            : 802.11n
    Sinyal                 : 64%
";
            var r = NetshWifiFallback.Parse(output);

            Assert.NotNull(r);
            Assert.Equal("contoso-id", r!.Ssid);
            Assert.Equal(64, r.SignalPercent);
            Assert.Equal("802.11n", r.RadioType);
            Assert.Equal(11, r.Channel);
        }

        [Fact]
        public void Parse_unknown_language_without_ssid_returns_null()
        {
            // Greek is not in the enrollment-geography label set -> nothing to report.
            var r = NetshWifiFallback.Parse("    Κατάσταση : συνδεδεμένο\r\n    Σήμα : 90%\r\n");

            Assert.Null(r);
        }

        [Fact]
        public void Parse_unknown_language_still_yields_ssid()
        {
            // SSID is untranslated in all locales — partial result beats none.
            var r = NetshWifiFallback.Parse("    SSID : contoso-gr\r\n    Σήμα : 90%\r\n");

            Assert.NotNull(r);
            Assert.Equal("contoso-gr", r!.Ssid);
            Assert.Null(r.SignalPercent);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Der WLAN-AutoKonfig-Dienst (wlansvc) wird nicht ausgeführt.")]
        public void Parse_empty_or_error_output_returns_null(string? output)
        {
            Assert.Null(NetshWifiFallback.Parse(output));
        }
    }
}
