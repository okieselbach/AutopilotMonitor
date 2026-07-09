#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Snapshot of the configured Delivery Optimization bandwidth-throttle policies from BOTH
    /// policy stores. GPO and Intune/MDM do NOT land in the same registry path (verified against
    /// the Policy CSP / DO reference docs, 2026-07):
    /// <list type="bullet">
    ///   <item>GPO (ADMX): <c>HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization</c>,
    ///     DO-prefixed value names (<c>DOMaxForegroundDownloadBandwidth</c>, …).</item>
    ///   <item>Intune / Policy CSP: <c>HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\DeliveryOptimization</c>,
    ///     same value names.</item>
    /// </list>
    /// Absolute caps are in KB/s; percentage caps 0-100. A value of 0 means "dynamic, no limit"
    /// (the OS default) — treated as not configured for the caps. DownloadMode 0 however is a
    /// real setting (HTTP only) and is reported whenever the value exists.
    /// </summary>
    public sealed class DoThrottlePolicySnapshot
    {
        // GPO store (SOFTWARE\Policies\...)
        public int? GpoMaxForegroundKBps { get; set; }
        public int? GpoMaxBackgroundKBps { get; set; }
        public int? GpoPctMaxForeground { get; set; }
        public int? GpoPctMaxBackground { get; set; }
        public int? GpoDownloadMode { get; set; }

        // MDM / Policy CSP store (SOFTWARE\Microsoft\PolicyManager\current\device\...)
        public int? MdmMaxForegroundKBps { get; set; }
        public int? MdmMaxBackgroundKBps { get; set; }
        public int? MdmPctMaxForeground { get; set; }
        public int? MdmPctMaxBackground { get; set; }
        public int? MdmDownloadMode { get; set; }

        /// <summary>True when any download-bandwidth cap is configured in either store.</summary>
        public bool ThrottleConfigured =>
            GpoMaxForegroundKBps.HasValue || GpoMaxBackgroundKBps.HasValue ||
            GpoPctMaxForeground.HasValue || GpoPctMaxBackground.HasValue ||
            MdmMaxForegroundKBps.HasValue || MdmMaxBackgroundKBps.HasValue ||
            MdmPctMaxForeground.HasValue || MdmPctMaxBackground.HasValue;

        /// <summary>"gpo" | "mdm" | "gpo+mdm" — which store(s) carry throttle caps. Null when none.</summary>
        public string? ThrottleSources
        {
            get
            {
                var gpo = GpoMaxForegroundKBps.HasValue || GpoMaxBackgroundKBps.HasValue ||
                          GpoPctMaxForeground.HasValue || GpoPctMaxBackground.HasValue;
                var mdm = MdmMaxForegroundKBps.HasValue || MdmMaxBackgroundKBps.HasValue ||
                          MdmPctMaxForeground.HasValue || MdmPctMaxBackground.HasValue;
                if (gpo && mdm) return "gpo+mdm";
                if (gpo) return "gpo";
                if (mdm) return "mdm";
                return null;
            }
        }
    }

    /// <summary>
    /// Reads the DO throttle policies fresh at each emission (Intune policies land DURING the
    /// enrollment — a constructor-time read would miss them). Fail-soft: any registry error
    /// yields an empty snapshot, never throws.
    /// </summary>
    public static class DoThrottlePolicyReader
    {
        public const string GpoKeyPath = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
        public const string MdmKeyPath = @"SOFTWARE\Microsoft\PolicyManager\current\device\DeliveryOptimization";

        private const string MaxForegroundValue = "DOMaxForegroundDownloadBandwidth";
        private const string MaxBackgroundValue = "DOMaxBackgroundDownloadBandwidth";
        private const string PctMaxForegroundValue = "DOPercentageMaxForegroundBandwidth";
        private const string PctMaxBackgroundValue = "DOPercentageMaxBackgroundBandwidth";
        private const string DownloadModeValue = "DODownloadMode";

        /// <summary>Reads both policy stores from the 64-bit registry view.</summary>
        public static DoThrottlePolicySnapshot Read()
        {
            return ReadCore(RegistryValueGetter);
        }

        /// <summary>
        /// Testable core: <paramref name="getValue"/> maps (keyPath, valueName) to the raw
        /// registry value (or null when absent).
        /// </summary>
        internal static DoThrottlePolicySnapshot ReadCore(Func<string, string, object?> getValue)
        {
            var snapshot = new DoThrottlePolicySnapshot();
            try
            {
                snapshot.GpoMaxForegroundKBps = ReadCap(getValue, GpoKeyPath, MaxForegroundValue);
                snapshot.GpoMaxBackgroundKBps = ReadCap(getValue, GpoKeyPath, MaxBackgroundValue);
                snapshot.GpoPctMaxForeground = ReadCap(getValue, GpoKeyPath, PctMaxForegroundValue);
                snapshot.GpoPctMaxBackground = ReadCap(getValue, GpoKeyPath, PctMaxBackgroundValue);
                snapshot.GpoDownloadMode = ReadInt(getValue, GpoKeyPath, DownloadModeValue);

                snapshot.MdmMaxForegroundKBps = ReadCap(getValue, MdmKeyPath, MaxForegroundValue);
                snapshot.MdmMaxBackgroundKBps = ReadCap(getValue, MdmKeyPath, MaxBackgroundValue);
                snapshot.MdmPctMaxForeground = ReadCap(getValue, MdmKeyPath, PctMaxForegroundValue);
                snapshot.MdmPctMaxBackground = ReadCap(getValue, MdmKeyPath, PctMaxBackgroundValue);
                snapshot.MdmDownloadMode = ReadInt(getValue, MdmKeyPath, DownloadModeValue);
            }
            catch
            {
                // Fail-soft: a partially filled snapshot is fine; context fields are best-effort.
            }
            return snapshot;
        }

        /// <summary>Cap semantics: 0 = "dynamic / no limit" (OS default) → treated as absent.</summary>
        private static int? ReadCap(Func<string, string, object?> getValue, string keyPath, string valueName)
        {
            var value = ReadInt(getValue, keyPath, valueName);
            return value.HasValue && value.Value > 0 ? value : null;
        }

        private static int? ReadInt(Func<string, string, object?> getValue, string keyPath, string valueName)
        {
            try
            {
                var raw = getValue(keyPath, valueName);
                if (raw == null) return null;
                // DWORDs arrive as int; PolicyManager occasionally stores numbers as strings.
                return Convert.ToInt32(raw);
            }
            catch
            {
                return null;
            }
        }

        private static object? RegistryValueGetter(string keyPath, string valueName)
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
