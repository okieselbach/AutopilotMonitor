#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    public class DoGroupIdSetResult
    {
        /// <summary>The DOGroupId value is the computed one after this run (written or already there).</summary>
        public bool Success { get; set; }

        /// <summary>True when this run actually wrote the registry value (false for the idempotent no-op).</summary>
        public bool Written { get; set; }

        /// <summary>Deliberate non-action (existing foreign policy/value, no usable network) — not an error.</summary>
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }

        public string? GroupId { get; set; }
        public string? PreviousValue { get; set; }
        public string? GatewayIp { get; set; }
        public string? GatewayMac { get; set; }
        public string? Error { get; set; }
    }

    internal enum DoGroupIdAction
    {
        Write,
        NoOp,
        SkipForeign,
    }

    /// <summary>
    /// Sets the Delivery Optimization group ID policy value (<c>DOGroupId</c> under the GPO
    /// DeliveryOptimization key) from a network fingerprint: a deterministic GUID derived from the
    /// default gateway's IPv4 address and MAC address, so all devices behind the same gateway land
    /// in the same DO peering group. The byte layout is RealmJoin-compatible — devices managed by
    /// RealmJoin on the same network compute the identical GUID and peer with ours.
    /// <para>
    /// Deliberately conservative where RealmJoin is not: no random-GUID fallback (a per-device
    /// random ID would isolate the device in a one-member group — worse than the OS default
    /// grouping), and existing foreign configuration always wins: a DOGroupIdSource policy, an
    /// MDM-managed DOGroupId, or a GPO value we did not write ourselves all result in a skip.
    /// Never throws.
    /// </para>
    /// </summary>
    public static class DoGroupIdService
    {
        internal const string GroupIdValueName = "DOGroupId";
        internal const string GroupIdSourceValueName = "DOGroupIdSource";

        // RealmJoin's fingerprint-GUID prefix — part of the compatible byte layout, do not change.
        private static readonly byte[] GuidPrefix = { 101, 48, 6 };

        /// <summary>
        /// Computes and applies the fingerprint GroupId. <paramref name="lastWrittenGroupId"/> is
        /// the GUID a previous agent run wrote (null when none is recorded) — it lets us update our
        /// own value after a gateway change while never touching a foreign one.
        /// </summary>
        public static DoGroupIdSetResult TrySetGroupId(string? lastWrittenGroupId, AgentLogger logger)
        {
            var result = new DoGroupIdSetResult();

            try
            {
                // ---- Network fingerprint -------------------------------------------------
                var nic = NetworkInterfaceLocator.FindActiveNetworkInterface();
                var gateway = nic == null ? null : NetworkInterfaceLocator.GetIpv4Gateway(nic);
                if (gateway == null)
                {
                    result.Skipped = true;
                    result.SkipReason = "no_ipv4_gateway";
                    logger.Info("DO GroupId auto-set: no active interface with an IPv4 default gateway — skipping.");
                    return result;
                }
                result.GatewayIp = gateway.ToString();

                if (!ArpNativeMethods.TryResolveMac(gateway, out var mac, out var arpError))
                {
                    result.Skipped = true;
                    result.SkipReason = "arp_failed";
                    result.Error = arpError;
                    logger.Warning($"DO GroupId auto-set: could not resolve gateway MAC ({arpError}) — skipping.");
                    return result;
                }
                result.GatewayMac = BitConverter.ToString(mac);

                var computed = ComputeGroupId(gateway.GetAddressBytes(), mac).ToString();
                result.GroupId = computed;

                // ---- Foreign-configuration checks ----------------------------------------
                // DOGroupIdSource (either store) selects a dynamic group source — a fingerprint
                // GroupId would fight it. An MDM-managed DOGroupId means Intune owns the setting.
                if (ReadPolicyValue(DoThrottlePolicyReader.GpoKeyPath, GroupIdSourceValueName) != null
                    || ReadPolicyValue(DoThrottlePolicyReader.MdmKeyPath, GroupIdSourceValueName) != null)
                {
                    result.Skipped = true;
                    result.SkipReason = "group_id_source_policy";
                    logger.Info("DO GroupId auto-set: DOGroupIdSource policy is configured — skipping.");
                    return result;
                }
                if (ReadPolicyValue(DoThrottlePolicyReader.MdmKeyPath, GroupIdValueName) != null)
                {
                    result.Skipped = true;
                    result.SkipReason = "mdm_policy_present";
                    logger.Info("DO GroupId auto-set: DOGroupId is managed via MDM policy — skipping.");
                    return result;
                }

                // ---- Ownership decision ---------------------------------------------------
                var existing = ReadPolicyValue(DoThrottlePolicyReader.GpoKeyPath, GroupIdValueName) as string;
                result.PreviousValue = existing;

                switch (DecideAction(existing, computed, lastWrittenGroupId))
                {
                    case DoGroupIdAction.NoOp:
                        result.Success = true;
                        logger.Info($"DO GroupId auto-set: already set to {computed} — nothing to do.");
                        return result;

                    case DoGroupIdAction.SkipForeign:
                        result.Skipped = true;
                        result.SkipReason = "foreign_value_present";
                        logger.Info($"DO GroupId auto-set: existing DOGroupId '{existing}' was not written by us — skipping.");
                        return result;
                }

                // ---- Write + readback -----------------------------------------------------
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.CreateSubKey(DoThrottlePolicyReader.GpoKeyPath))
                {
                    key.SetValue(GroupIdValueName, computed, RegistryValueKind.String);
                }

                var readback = ReadPolicyValue(DoThrottlePolicyReader.GpoKeyPath, GroupIdValueName) as string;
                if (!string.Equals(readback, computed, StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = $"readback mismatch: wrote '{computed}', read '{readback ?? "<null>"}'";
                    logger.Warning($"DO GroupId auto-set failed: {result.Error}");
                    return result;
                }

                result.Success = true;
                result.Written = true;
                logger.Info($"DO GroupId auto-set: DOGroupId set to {computed} (gateway {result.GatewayIp}, previous: {existing ?? "<none>"})");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                logger.Warning($"DO GroupId auto-set failed: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// RealmJoin-compatible fingerprint GUID: 16 bytes laid out as
        /// prefix {101,48,6} + {0,0,0} + {ip[1],0,ip[2],ip[3]} + mac[6], passed to
        /// <c>new Guid(byte[])</c>. The IP byte shuffle mirrors RealmJoin's "swap for
        /// new Guid()" step — the layout must stay bit-identical for cross-product peering.
        /// </summary>
        internal static Guid ComputeGroupId(byte[] ipv4Bytes, byte[] macBytes)
        {
            if (ipv4Bytes == null || ipv4Bytes.Length != 4)
                throw new ArgumentException("IPv4 address bytes required", nameof(ipv4Bytes));
            if (macBytes == null || macBytes.Length != 6)
                throw new ArgumentException("6-byte MAC required", nameof(macBytes));

            var bytes = new byte[16];
            Array.Copy(GuidPrefix, 0, bytes, 0, GuidPrefix.Length);
            // bytes[3..5] stay zero (RealmJoin's zero padding)
            bytes[6] = ipv4Bytes[1];
            // bytes[7] stays zero
            bytes[8] = ipv4Bytes[2];
            bytes[9] = ipv4Bytes[3];
            Array.Copy(macBytes, 0, bytes, 10, macBytes.Length);
            return new Guid(bytes);
        }

        /// <summary>
        /// Pure ownership decision. GUID comparison tolerates case and braces so a value written
        /// by another tool in "{...}" form still matches semantically.
        /// </summary>
        internal static DoGroupIdAction DecideAction(string? existing, string computed, string? lastWrittenByUs)
        {
            if (string.IsNullOrEmpty(existing))
                return DoGroupIdAction.Write;
            if (GuidEquals(existing, computed))
                return DoGroupIdAction.NoOp;
            if (GuidEquals(existing, lastWrittenByUs))
                return DoGroupIdAction.Write; // our own stale value — gateway changed
            return DoGroupIdAction.SkipForeign;
        }

        private static bool GuidEquals(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb) && ga == gb;
        }

        private static object? ReadPolicyValue(string keyPath, string valueName)
        {
            // Registry64 explicitly — SOFTWARE\Policies is WOW64-redirected, and the policy
            // stores live in the 64-bit view regardless of the agent's bitness.
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(keyPath))
            {
                return key?.GetValue(valueName);
            }
        }
    }
}
