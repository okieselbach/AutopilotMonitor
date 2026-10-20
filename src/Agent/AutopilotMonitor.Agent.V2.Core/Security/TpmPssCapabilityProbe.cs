using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Diagnostic probe that asks the local key-storage provider to produce three RSA signatures
    /// over a fixed payload — PKCS#1 v1.5 SHA-256 (legacy, what every TPM 2.0 supports),
    /// PSS SHA-256, and PSS SHA-384 (what Schannel prefers for TLS client authentication, and the
    /// only RSA signature TLS 1.3 allows).
    /// <para>
    /// The probe exists for one reason: to distinguish a generic <c>SecureChannelFailure</c>
    /// from the case where a TPM-backed cert can be filtered out of TLS client-auth because its
    /// firmware can't do PSS. On older TPM 2.0 firmware (Surface Book 1's Infineon SLB 9665,
    /// Infineon 7.63 and STMicro 73.4 in 2017/18 business devices) PKCS#1 succeeds but PSS
    /// reports <c>"The requested salt size for signing with RSAPSS does not match what the TPM
    /// uses"</c> or <c>"The requested operation is not supported"</c>. Such a device can still
    /// connect at another time (failed during setup, connected after it), so the result
    /// classifies the failed start, not the device.
    /// </para>
    /// <para>
    /// <b>Hot-path discipline:</b> this probe is intentionally NOT called at agent startup.
    /// It must only be invoked on the failure path after the session-registration retry loop
    /// has terminally failed with <c>SecureChannelFailure</c>, so healthy devices never pay
    /// the ~1 second of TPM operations this involves.
    /// </para>
    /// </summary>
    public static class TpmPssCapabilityProbe
    {
        /// <summary>
        /// Performs three signing attempts (PKCS#1 SHA-256, PSS SHA-256, PSS SHA-384) and returns
        /// a result describing which the local key provider can do. Never throws.
        /// </summary>
        public static TpmPssProbeResult Probe(X509Certificate2 cert, AgentLogger logger = null)
        {
            if (cert == null)
                return TpmPssProbeResult.Unprobable("(no certificate)");

            RSA rsa;
            try
            {
                rsa = RSACertificateExtensions.GetRSAPrivateKey(cert);
            }
            catch (Exception ex)
            {
                logger?.Debug($"TpmPssCapabilityProbe: cannot acquire RSA private key: {ex.Message}");
                return TpmPssProbeResult.Unprobable($"GetRSAPrivateKey failed: {ex.Message}");
            }

            if (rsa == null)
                return TpmPssProbeResult.Unprobable("(certificate has no RSA private key)");

            var providerName = TryGetProviderName(rsa);
            var keySizeBits = TryGetKeySize(rsa);
            var data = Encoding.UTF8.GetBytes("autopilot-monitor-tpm-probe");

            var pkcs1 = TrySignData(rsa, data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var pssSha256 = TrySignData(rsa, data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            var pssSha384 = TrySignData(rsa, data, HashAlgorithmName.SHA384, RSASignaturePadding.Pss);

            try { rsa.Dispose(); } catch { /* best-effort */ }

            return new TpmPssProbeResult(
                pkcs1Sha256Works: pkcs1.Success,
                pssSha256Works: pssSha256.Success,
                pssSha384Works: pssSha384.Success,
                providerName: providerName,
                keySizeBits: keySizeBits,
                pssSha256Error: pssSha256.ErrorMessage,
                pssSha384Error: pssSha384.ErrorMessage,
                pssSha256ErrorCode: pssSha256.ErrorCode,
                pssSha384ErrorCode: pssSha384.ErrorCode);
        }

        private static (bool Success, string ErrorMessage, int? ErrorCode) TrySignData(
            RSA rsa, byte[] data, HashAlgorithmName hash, RSASignaturePadding padding)
        {
            try
            {
                var sig = rsa.SignData(data, hash, padding);
                return (sig != null && sig.Length > 0, null, null);
            }
            catch (Exception ex)
            {
                // CryptographicException carries the NCrypt status as HResult
                // (0x80290423 = the TPM's PSS salt size does not match, 0x80090029 = not supported).
                return (false, ex.Message, ex.HResult);
            }
        }

        private static string TryGetProviderName(RSA rsa)
        {
            // RSACng is the .NET wrapper around CNG keys — TPM-backed keys (Microsoft Platform
            // Crypto Provider) and software CNG keys (Microsoft Software Key Storage Provider)
            // both surface as RSACng. RSACryptoServiceProvider is the legacy CryptoAPI path.
            try
            {
                if (rsa is RSACng rsaCng)
                    return rsaCng.Key?.Provider?.Provider ?? "(cng-no-provider)";
            }
            catch { /* fall through */ }

            try
            {
                if (rsa is RSACryptoServiceProvider rsaCsp)
                    return rsaCsp.CspKeyContainerInfo?.ProviderName ?? "(csp-no-provider)";
            }
            catch { /* fall through */ }

            return rsa.GetType().Name;
        }

        private static int TryGetKeySize(RSA rsa)
        {
            try { return rsa.KeySize; } catch { return 0; }
        }
    }

    /// <summary>
    /// Outcome of <see cref="TpmPssCapabilityProbe.Probe"/>. Treat as a snapshot — do not retain
    /// across cert refreshes.
    /// </summary>
    public sealed class TpmPssProbeResult
    {
        /// <summary>Longest <see cref="ToDistressMessage"/> — the distress message cap.</summary>
        internal const int MaxDistressMessageLength = 256;

        public bool Pkcs1Sha256Works { get; }
        public bool PssSha256Works { get; }
        public bool PssSha384Works { get; }
        public string ProviderName { get; }
        public int KeySizeBits { get; }
        public string PssSha256Error { get; }
        public string PssSha384Error { get; }
        public int? PssSha256ErrorCode { get; }
        public int? PssSha384ErrorCode { get; }
        public bool IsProbable { get; }

        /// <summary>
        /// True iff PKCS#1 succeeded but PSS did not — the canonical "TPM firmware is too old
        /// for PSS" footprint. Distinguishes the TPM-PSS case from generic crypto failures
        /// (which would also fail PKCS#1).
        /// </summary>
        public bool IsTpmPssBroken => IsProbable && Pkcs1Sha256Works && !PssSha256Works;

        /// <summary>
        /// True iff the provider name explicitly identifies a TPM-backed key. Lets the caller
        /// avoid emitting <c>TpmPssUnsupported</c> distress when PSS fails on a software key
        /// (which would mean a different bug class entirely).
        /// </summary>
        public bool IsTpmBacked =>
            !string.IsNullOrEmpty(ProviderName) &&
            ProviderName.IndexOf("Platform Crypto", StringComparison.OrdinalIgnoreCase) >= 0;

        public TpmPssProbeResult(
            bool pkcs1Sha256Works,
            bool pssSha256Works,
            bool pssSha384Works,
            string providerName,
            int keySizeBits,
            string pssSha256Error,
            string pssSha384Error,
            int? pssSha256ErrorCode = null,
            int? pssSha384ErrorCode = null)
        {
            Pkcs1Sha256Works = pkcs1Sha256Works;
            PssSha256Works = pssSha256Works;
            PssSha384Works = pssSha384Works;
            ProviderName = providerName ?? "(unknown)";
            KeySizeBits = keySizeBits;
            PssSha256Error = pssSha256Error;
            PssSha384Error = pssSha384Error;
            PssSha256ErrorCode = pssSha256ErrorCode;
            PssSha384ErrorCode = pssSha384ErrorCode;
            IsProbable = true;
        }

        private TpmPssProbeResult(string failureSummary)
        {
            ProviderName = "(unprobable)";
            PssSha256Error = failureSummary;
            IsProbable = false;
        }

        public static TpmPssProbeResult Unprobable(string reason) => new TpmPssProbeResult(reason);

        /// <summary>
        /// Language-neutral summary for the distress message, codes instead of the localized
        /// error texts: <c>pkcs1=ok pss256=fail(0x80290423) pss384=fail(0x80090029)
        /// tls=0x80090326 sch=36888:40/1205 prov=PCP key=2048</c>. <paramref name="tlsCode"/>
        /// is the native code of the failed handshake (<see cref="TransportErrorCodes"/>),
        /// <paramref name="schannelEvents"/> the System-log tokens (<see cref="SchannelEventReader"/>);
        /// both are left out when null. At most <see cref="MaxDistressMessageLength"/> chars.
        /// </summary>
        public string ToDistressMessage(string tlsCode = null, string schannelEvents = null)
        {
            var sb = new StringBuilder();
            sb.Append("pkcs1=").Append(Pkcs1Sha256Works ? "ok" : "fail");
            AppendSignOutcome(sb, " pss256=", PssSha256Works, PssSha256ErrorCode);
            AppendSignOutcome(sb, " pss384=", PssSha384Works, PssSha384ErrorCode);
            if (!string.IsNullOrEmpty(tlsCode)) sb.Append(" tls=").Append(tlsCode);
            if (!string.IsNullOrEmpty(schannelEvents)) sb.Append(" sch=").Append(schannelEvents);
            sb.Append(" prov=").Append(ShortProviderName(ProviderName));
            if (KeySizeBits > 0) sb.Append(" key=").Append(KeySizeBits);
            return sb.Length <= MaxDistressMessageLength ? sb.ToString() : sb.ToString(0, MaxDistressMessageLength);
        }

        private static void AppendSignOutcome(StringBuilder sb, string key, bool works, int? errorCode)
        {
            sb.Append(key);
            if (works) sb.Append("ok");
            else if (errorCode.HasValue) sb.Append("fail(").Append(TransportErrorCodes.Hex(errorCode.Value)).Append(')');
            else sb.Append("fail");
        }

        internal static string ShortProviderName(string providerName)
        {
            if (string.Equals(providerName, "Microsoft Platform Crypto Provider", StringComparison.OrdinalIgnoreCase)) return "PCP";
            if (string.Equals(providerName, "Microsoft Software Key Storage Provider", StringComparison.OrdinalIgnoreCase)) return "KSP";
            if (string.IsNullOrEmpty(providerName)) return "(unknown)";
            return providerName.Length <= 32 ? providerName : providerName.Substring(0, 32);
        }
    }
}
