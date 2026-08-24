using System;

namespace AutopilotMonitor.Shared.Security
{
    /// <summary>
    /// Microsoft device-certificate extension OIDs and the shared ASN.1 decoding for the
    /// GUID-bearing ones. Canonical for both the agent (local identity probing) and the
    /// backend (client-cert tenant binding) so the two cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Two certificate families carry these, and they are NOT interchangeable:
    /// <list type="bullet">
    /// <item><description>
    /// <b>MS-Organization-Access</b> (Entra/AAD-Join, <c>1.2.840.113556.1.5.284.*</c>) — installed
    /// on AAD-join. The agent reads it locally to learn its own TenantId; it is never used for
    /// backend authentication.
    /// </description></item>
    /// <item><description>
    /// <b>Microsoft Intune MDM Device CA</b> (<c>1.2.840.113556.5.*</c>) — this is the cert the
    /// agent presents as the mTLS client certificate, so it is the only one whose contents the
    /// backend can trust as proof of possession.
    /// </description></item>
    /// </list>
    /// Values verified against a real field certificate (issued 2026-05-08) — see
    /// <c>Functions.Tests/device-cert-sample.pem</c>. Note that the Intune <b>Account</b> ID
    /// (<c>5.6</c>) is a DIFFERENT GUID from the Entra tenant ID (<c>5.14</c>); they must never
    /// be compared against each other.
    /// </remarks>
    public static class MsDeviceCertificateOids
    {
        /// <summary>Issuer CN of the Intune MDM device certificate used for mTLS client auth.</summary>
        public const string IntuneMdmIssuer = "CN=Microsoft Intune MDM Device CA";

        /// <summary>
        /// Intune MDM device certificate → Entra (Azure AD) TenantId. Encoded as a nested
        /// ASN.1 OCTET STRING wrapping a 16-byte little-endian GUID.
        /// </summary>
        public const string IntuneCertEntraTenantIdOid = "1.2.840.113556.5.14";

        /// <summary>
        /// Intune MDM device certificate → Intune <b>Account</b> ID. Distinct from the Entra
        /// tenant ID — do not use it for tenant comparison.
        /// </summary>
        public const string IntuneCertAccountIdOid = "1.2.840.113556.5.6";

        /// <summary>
        /// Intune MDM device certificate → Intune device ID (matches the certificate's Subject CN).
        /// Unlike the other two this extension holds the 16 GUID bytes <b>raw</b>, without the
        /// nested OCTET STRING wrapper — decode it with <see cref="TryParseGuid"/>, which accepts
        /// both shapes.
        /// </summary>
        public const string IntuneCertDeviceIdOid = "1.2.840.113556.5.4";

        /// <summary>MS-Organization-Access (Entra join) certificate → TenantId.</summary>
        public const string EntraJoinCertTenantIdOid = "1.2.840.113556.1.5.284.5";

        /// <summary>MS-Organization-Access (Entra join) certificate → DeviceId.</summary>
        public const string EntraJoinCertDeviceIdOid = "1.2.840.113556.1.5.284.2";

        /// <summary>
        /// Decodes a 16-byte Microsoft binary GUID out of an X.509 extension value
        /// (<c>X509Extension.RawData</c>).
        /// <para>
        /// Accepts both encodings seen in the wild: the GUID wrapped in a nested ASN.1 OCTET
        /// STRING (short form <c>04 10 …</c> and long form <c>04 81 10 …</c>), and the 16 raw
        /// bytes with no wrapper at all (Intune OID <c>5.4</c>).
        /// </para>
        /// The byte order is Microsoft's — little-endian for the first three groups, i.e. exactly
        /// what <see cref="Guid(byte[])"/> expects.
        /// </summary>
        /// <returns><c>true</c> and the decoded GUID, or <c>false</c> on any structural mismatch. Never throws.</returns>
        public static bool TryParseGuid(byte[] raw, out Guid value)
        {
            value = Guid.Empty;
            if (raw == null) return false;

            // Unwrapped form: exactly the 16 GUID bytes (Intune OID 5.4).
            if (raw.Length == 16)
                return TryFromBytes(raw, 0, out value);

            if (raw.Length < 2) return false;

            // ASN.1 OCTET STRING tag.
            if (raw[0] != 0x04) return false;

            int contentStart;
            int contentLen;

            if (raw[1] < 0x80)
            {
                // short-form length
                contentLen = raw[1];
                contentStart = 2;
            }
            else
            {
                // long-form length: lower 7 bits = number of following length bytes.
                int numLenBytes = raw[1] & 0x7F;
                if (numLenBytes < 1 || numLenBytes > 4 || raw.Length < 2 + numLenBytes) return false;

                contentLen = 0;
                for (int i = 0; i < numLenBytes; i++)
                    contentLen = (contentLen << 8) | raw[2 + i];

                contentStart = 2 + numLenBytes;
            }

            if (contentLen != 16 || contentStart + 16 > raw.Length) return false;

            return TryFromBytes(raw, contentStart, out value);
        }

        private static bool TryFromBytes(byte[] source, int offset, out Guid value)
        {
            value = Guid.Empty;
            var guidBytes = new byte[16];
            Buffer.BlockCopy(source, offset, guidBytes, 0, 16);

            try
            {
                value = new Guid(guidBytes);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
