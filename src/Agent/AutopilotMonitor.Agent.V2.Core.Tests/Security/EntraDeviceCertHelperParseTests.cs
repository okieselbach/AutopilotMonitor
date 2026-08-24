using System;
using AutopilotMonitor.Agent.V2.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// Pins <see cref="EntraDeviceCertHelper.ParseGuidFromExtension"/>, the pure byte-level part of the
    /// cert probe. Both encodings Microsoft actually ships are covered: the ASN.1 OCTET STRING wrapper
    /// used by the MS-Organization-Access TenantId/DeviceId extensions (and Intune's
    /// <c>1.2.840.113556.5.6</c> / <c>5.14</c>), and the BARE 16-byte payload used by Intune's DeviceId
    /// extension <c>1.2.840.113556.5.4</c> — the latter was silently returning <c>null</c> before.
    /// The store-iteration shell around it needs a real machine cert store and is not unit-tested.
    /// </summary>
    public sealed class EntraDeviceCertHelperParseTests
    {
        // Synthetic — never a real tenant/device id. ToByteArray() gives the MS little-endian layout
        // that both cert encodings carry, so every expectation below is derived, not hand-transcribed.
        private static readonly Guid Sample = new Guid("11223344-5566-7788-99aa-bbccddeeff00");

        private static byte[] Wrap(byte[] payload, params byte[] header)
        {
            var buf = new byte[header.Length + payload.Length];
            Buffer.BlockCopy(header, 0, buf, 0, header.Length);
            Buffer.BlockCopy(payload, 0, buf, header.Length, payload.Length);
            return buf;
        }

        [Fact]
        public void OctetString_ShortForm_Parses()
        {
            var raw = Wrap(Sample.ToByteArray(), 0x04, 0x10);

            Assert.Equal(Sample, EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        [Fact]
        public void OctetString_LongForm_Parses()
        {
            var raw = Wrap(Sample.ToByteArray(), 0x04, 0x81, 0x10);

            Assert.Equal(Sample, EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        /// <summary>
        /// The regression this class exists for: Intune's <c>1.2.840.113556.5.4</c> extension value is
        /// exactly 16 bytes with no <c>04 10</c> prefix, so the strict OCTET-STRING parser bailed on the
        /// tag check and the DeviceId was unreadable.
        /// </summary>
        [Fact]
        public void BareSixteenBytes_Parses()
        {
            var raw = Sample.ToByteArray();

            Assert.Equal(Sample, EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        /// <summary>
        /// A bare GUID may legitimately start with 0x04 (the OCTET STRING tag). That must not be
        /// mistaken for a wrapper: a wrapped 16-byte GUID is always >= 18 bytes, so the two forms are
        /// disjoint by length and the bare path has to win here.
        /// </summary>
        [Fact]
        public void BareSixteenBytes_StartingWithOctetStringTag_StillParses()
        {
            var guid = new Guid("aabbcc04-0110-4020-8030-405060708090");
            var raw = guid.ToByteArray();

            Assert.Equal(0x04, raw[0]); // guard: the byte layout really does start with the ASN.1 tag
            Assert.Equal(16, raw.Length);
            Assert.Equal(guid, EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        [Fact]
        public void OctetString_IsLittleEndianMsLayout()
        {
            // 04 10 + the canonical MS binary layout of {11223344-...} => first group byte-reversed.
            var raw = new byte[]
            {
                0x04, 0x10,
                0x44, 0x33, 0x22, 0x11,
                0x66, 0x55,
                0x88, 0x77,
                0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
            };

            Assert.Equal(Sample, EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        [Fact]
        public void Null_ReturnsNull()
        {
            Assert.Null(EntraDeviceCertHelper.ParseGuidFromExtension(null));
        }

        [Fact]
        public void Empty_ReturnsNull()
        {
            Assert.Null(EntraDeviceCertHelper.ParseGuidFromExtension(new byte[0]));
        }

        [Theory]
        [InlineData(15)]
        [InlineData(17)]
        public void BarePayload_OfWrongLength_ReturnsNull(int length)
        {
            var raw = new byte[length];
            for (var i = 0; i < length; i++) raw[i] = (byte)(0x40 + i); // no 0x04 tag, no valid wrapper

            Assert.Null(EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        [Fact]
        public void NonOctetStringTag_ReturnsNull()
        {
            // e.g. Intune's 1.2.840.113556.5.15 => ASN.1 INTEGER 2.
            Assert.Null(EntraDeviceCertHelper.ParseGuidFromExtension(new byte[] { 0x02, 0x01, 0x02 }));
        }

        [Fact]
        public void OctetString_WithNonGuidContentLength_ReturnsNull()
        {
            var raw = Wrap(new byte[8], 0x04, 0x08);

            Assert.Null(EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        [Fact]
        public void OctetString_TruncatedContent_ReturnsNull()
        {
            // Declares 16 content bytes but only carries 10.
            var raw = Wrap(new byte[10], 0x04, 0x10);

            Assert.Null(EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }

        [Fact]
        public void OctetString_LongFormWithOversizedLengthHeader_ReturnsNull()
        {
            // 0x85 => 5 length bytes, above the 4 the parser accepts.
            var raw = Wrap(Sample.ToByteArray(), 0x04, 0x85, 0x00, 0x00, 0x00, 0x00, 0x10);

            Assert.Null(EntraDeviceCertHelper.ParseGuidFromExtension(raw));
        }
    }
}
