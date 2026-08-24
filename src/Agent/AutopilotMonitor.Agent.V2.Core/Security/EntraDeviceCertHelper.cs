using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Reads the Entra TenantId / DeviceId out of the <c>MS-Organization-Access</c> device
    /// certificate that Windows installs into <c>LocalMachine\My</c> when AAD-Join completes.
    /// <para>
    /// Useful as a fallback for <see cref="TenantIdResolver"/>: in the Autopilot
    /// pre-provisioning / hybrid window the cert can land BEFORE the
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\TenantInfo</c> and
    /// <c>JoinInfo</c> registry writes finish, so the cert carries the TenantId while
    /// every registry probe still misses.
    /// </para>
    /// <para>
    /// Both GUIDs live as ASN.1 OCTET STRING extensions on the cert:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>1.2.840.113556.1.5.284.5</c> → TenantId (16 bytes, MS little-endian binary GUID)</description></item>
    /// <item><description><c>1.2.840.113556.1.5.284.2</c> → DeviceId (same encoding)</description></item>
    /// </list>
    /// <para>
    /// The sibling <c>Microsoft Intune MDM Device CA</c> client cert (the one the agent authenticates
    /// with, see <see cref="CertificateHelper"/>) carries a DIFFERENT OID arc that identifies the same
    /// device from Intune's side. Observed on a live Intune-enrolled Windows 11 device:
    /// </para>
    /// <list type="table">
    /// <listheader><term>OID</term><description>Meaning / encoding</description></listheader>
    /// <item><term><c>1.2.840.113556.5.4</c></term><description>Intune DeviceId — BARE 16-byte GUID, no ASN.1 wrapper. Equals the cert's Subject CN.</description></item>
    /// <item><term><c>1.2.840.113556.5.6</c></term><description>Intune AccountId (the Intune tenant account, NOT the Entra TenantId) — OCTET STRING GUID.</description></item>
    /// <item><term><c>1.2.840.113556.5.14</c></term><description>Entra TenantId — OCTET STRING GUID. This is the one to compare against an Entra tenant id.</description></item>
    /// <item><term><c>1.2.840.113556.5.11</c></term><description>Further GUID, OCTET STRING; semantics not documented by Microsoft and not verified here.</description></item>
    /// <item><term><c>1.2.840.113556.5.15/.16/.17/.18/.19/.23</c></term><description>ASN.1 INTEGER enums (platform / enrollment flavour); semantics undocumented.</description></item>
    /// </list>
    /// <para>
    /// The bare-GUID encoding of <c>1.2.840.113556.5.4</c> is why
    /// <see cref="ParseGuidFromExtension"/> accepts an unwrapped 16-byte payload as well —
    /// a strict OCTET-STRING parser silently returns <c>null</c> for that extension.
    /// </para>
    /// <para>
    /// All probes are best-effort: never throw, return <c>null</c> on miss, log a Warning
    /// with the specific failure reason (no matching cert / extension missing / ASN.1
    /// parse failed) so the agent log explains why the fallback didn't help.
    /// </para>
    /// </summary>
    public static class EntraDeviceCertHelper
    {
        public const string MsOrganizationAccessIssuer = "MS-Organization-Access";
        public const string TenantIdOid = "1.2.840.113556.1.5.284.5";
        public const string DeviceIdOid = "1.2.840.113556.1.5.284.2";

        /// <summary>
        /// Reads the Entra TenantId from the MS-Organization-Access device cert.
        /// Returns <c>null</c> if no currently-valid cert is present, the OID extension
        /// is missing, or the ASN.1 parse failed.
        /// </summary>
        public static Guid? TryGetTenantIdFromCert(AgentLogger logger = null) =>
            TryReadGuidExtension(TenantIdOid, "TenantId", logger);

        /// <summary>
        /// Reads the Entra DeviceId from the MS-Organization-Access device cert.
        /// </summary>
        public static Guid? TryGetDeviceIdFromCert(AgentLogger logger = null) =>
            TryReadGuidExtension(DeviceIdOid, "DeviceId", logger);

        private static Guid? TryReadGuidExtension(string oid, string label, AgentLogger logger)
        {
            if (string.IsNullOrEmpty(oid)) return null;

            try
            {
                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);

                    var now = DateTime.UtcNow;
                    var candidates = store.Certificates
                        .OfType<X509Certificate2>()
                        .Where(c => c.Issuer != null
                                 && c.Issuer.IndexOf(MsOrganizationAccessIssuer, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Where(c => c.NotBefore <= now && c.NotAfter >= now)
                        // Latest-expiring cert first — defends against an expired sibling from a prior enrollment lingering in the store.
                        .OrderByDescending(c => c.NotAfter)
                        .ToList();

                    if (candidates.Count == 0)
                    {
                        logger?.Warning(
                            $"EntraDeviceCertHelper: no currently-valid '{MsOrganizationAccessIssuer}' cert found in LocalMachine\\My — device likely not yet AAD-joined.");
                        return null;
                    }

                    foreach (var cert in candidates)
                    {
                        var ext = cert.Extensions
                            .OfType<X509Extension>()
                            .FirstOrDefault(e => e.Oid != null && e.Oid.Value == oid);

                        if (ext == null || ext.RawData == null)
                        {
                            logger?.Debug(
                                $"EntraDeviceCertHelper: cert thumbprint={cert.Thumbprint} carries no {label} extension (OID={oid}); trying next candidate.");
                            continue;
                        }

                        var guid = ParseGuidFromExtension(ext.RawData);
                        if (guid != null)
                        {
                            logger?.Info(
                                $"EntraDeviceCertHelper: read {label}={guid.Value} from {MsOrganizationAccessIssuer} cert " +
                                $"(thumbprint={cert.Thumbprint}, NotAfter={cert.NotAfter:yyyy-MM-ddTHH:mm:ssZ}).");
                            return guid;
                        }

                        logger?.Warning(
                            $"EntraDeviceCertHelper: failed to parse {label} GUID extension in cert thumbprint={cert.Thumbprint} " +
                            $"(raw {ext.RawData.Length} bytes); trying next candidate.");
                    }

                    logger?.Warning(
                        $"EntraDeviceCertHelper: {candidates.Count} matching cert(s) present but none yielded a parseable {label} extension.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger?.Warning($"EntraDeviceCertHelper: probe for {label} failed — {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parses a 16-byte MS-format binary GUID (little-endian for the first three groups,
        /// per <see cref="Guid(byte[])"/>) out of a certificate extension's raw value.
        /// Two encodings are accepted, because Microsoft uses both:
        /// <list type="number">
        /// <item><description>Wrapped in an ASN.1 OCTET STRING — short-form (<c>04 10 ...</c>) or
        /// long-form (<c>04 81 10 ...</c>) length. Used by the MS-Organization-Access TenantId/DeviceId
        /// extensions and by the Intune <c>…5.6</c> / <c>…5.14</c> extensions.</description></item>
        /// <item><description>A bare, unwrapped 16-byte payload. Used by the Intune DeviceId extension
        /// <c>1.2.840.113556.5.4</c> — verified against a live Intune MDM device cert, whose raw value is
        /// exactly 16 bytes with no <c>04 10</c> prefix.</description></item>
        /// </list>
        /// The two forms cannot collide: a valid OCTET-STRING wrapper around 16 bytes is at least 18
        /// bytes long, so a 16-byte payload is never a truncated wrapper even when it happens to start
        /// with <c>0x04</c>.
        /// Returns <c>null</c> on any structural mismatch — never throws.
        /// </summary>
        public static Guid? ParseGuidFromExtension(byte[] raw)
        {
            if (raw == null) return null;

            // Bare 16-byte GUID (Intune 1.2.840.113556.5.4) — no ASN.1 wrapper to strip.
            if (raw.Length == 16) return ToGuid(raw, 0);

            if (raw.Length < 2) return null;

            // ASN.1 OCTET STRING tag.
            if (raw[0] != 0x04) return null;

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
                if (numLenBytes < 1 || numLenBytes > 4 || raw.Length < 2 + numLenBytes) return null;

                contentLen = 0;
                for (int i = 0; i < numLenBytes; i++)
                    contentLen = (contentLen << 8) | raw[2 + i];

                contentStart = 2 + numLenBytes;
            }

            if (contentLen != 16 || contentStart + 16 > raw.Length) return null;

            return ToGuid(raw, contentStart);
        }

        private static Guid? ToGuid(byte[] raw, int offset)
        {
            var guidBytes = new byte[16];
            Buffer.BlockCopy(raw, offset, guidBytes, 0, 16);

            try { return new Guid(guidBytes); }
            catch { return null; }
        }
    }
}
