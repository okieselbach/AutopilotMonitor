using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// The TPM-PSS distress message: codes instead of localized error texts, so reports from
    /// German, Czech and English devices read the same and the 256-char budget holds the
    /// handshake code and the Schannel events.
    /// </summary>
    public sealed class TpmPssProbeResultTests
    {
        private const int SaltSizeMismatch = unchecked((int)0x80290423);
        private const int NotSupported = unchecked((int)0x80090029);

        private static TpmPssProbeResult BrokenPcp(string provider = "Microsoft Platform Crypto Provider") =>
            new TpmPssProbeResult(
                pkcs1Sha256Works: true,
                pssSha256Works: false,
                pssSha384Works: false,
                providerName: provider,
                keySizeBits: 2048,
                pssSha256Error: "Die angeforderte Salt-Größe für die Signierung mit RSAPSS entspricht nicht dem, was TPM verwendet.",
                pssSha384Error: "Der angeforderte Vorgang wird nicht unterstützt.",
                pssSha256ErrorCode: SaltSizeMismatch,
                pssSha384ErrorCode: NotSupported);

        [Fact]
        public void Message_carries_codes_handshake_and_schannel_events()
        {
            var message = BrokenPcp().ToDistressMessage("0x80090326", "36888:40/1205,36887:116", "tls12,-,cc403");

            Assert.Equal(
                "pkcs1=ok pss256=fail(0x80290423) pss384=fail(0x80090029) tls=0x80090326 tlsp=tls12,-,cc403 sch=36888:40/1205,36887:116 prov=PCP key=2048",
                message);
        }

        [Fact]
        public void Message_never_carries_the_localized_error_text()
        {
            var message = BrokenPcp().ToDistressMessage();

            Assert.DoesNotContain("Salt", message);
            Assert.Equal("pkcs1=ok pss256=fail(0x80290423) pss384=fail(0x80090029) prov=PCP key=2048", message);
        }

        [Fact]
        public void Working_signatures_and_failures_without_a_code()
        {
            var result = new TpmPssProbeResult(
                pkcs1Sha256Works: true, pssSha256Works: true, pssSha384Works: false,
                providerName: "Microsoft Software Key Storage Provider", keySizeBits: 0,
                pssSha256Error: null, pssSha384Error: "empty signature");

            Assert.Equal("pkcs1=ok pss256=ok pss384=fail prov=KSP", result.ToDistressMessage());
        }

        [Fact]
        public void Message_stays_within_the_distress_cap()
        {
            var longEvents = string.Join(",", System.Linq.Enumerable.Repeat("36888:4294967295/4294967295/4294967295", 10));

            var message = BrokenPcp(new string('P', 200)).ToDistressMessage("0x80090326", longEvents);

            Assert.Equal(TpmPssProbeResult.MaxDistressMessageLength, message.Length);
        }

        [Theory]
        [InlineData("Microsoft Platform Crypto Provider", "PCP")]
        [InlineData("microsoft platform crypto provider", "PCP")]
        [InlineData("Microsoft Software Key Storage Provider", "KSP")]
        [InlineData("Contoso Smart Card Provider", "Contoso Smart Card Provider")]
        [InlineData("", "(unknown)")]
        public void Provider_names_are_shortened(string provider, string expected)
        {
            Assert.Equal(expected, TpmPssProbeResult.ShortProviderName(provider));
        }

        [Fact]
        public void Long_provider_name_is_cut_to_32_chars()
        {
            Assert.Equal(32, TpmPssProbeResult.ShortProviderName(new string('x', 40)).Length);
        }

        [Fact]
        public void Tpm_backed_broken_key_is_the_reportable_footprint()
        {
            var result = BrokenPcp();

            Assert.True(result.IsTpmPssBroken);
            Assert.True(result.IsTpmBacked);
        }
    }
}
