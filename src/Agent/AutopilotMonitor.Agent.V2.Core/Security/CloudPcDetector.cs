using System;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Detects whether the agent runs on a Windows 365 Cloud PC, using the same marker
    /// evidence the bootstrap script's <c>Test-IsCloudPc</c> settled on after the first
    /// W365 field test (2026-08-06) disproved WMI-model matching (a Cloud PC reports only
    /// Manufacturer='Microsoft Corporation' + Model='Virtual Machine' locally — identical
    /// to any Hyper-V VM):
    /// <list type="bullet">
    ///   <item><c>HKLM\SOFTWARE\Microsoft\Windows365</c> exists, AND</item>
    ///   <item>the <c>CloudManagedDesktopExtension</c> service (Microsoft Cloud Managed
    ///         Desktop Extension, the W365 management agent that runs ON Cloud PCs) is
    ///         installed — probed via its <c>HKLM\SYSTEM\CurrentControlSet\Services</c> key,
    ///         so the agent needs no System.ServiceProcess dependency.</item>
    /// </list>
    /// BOTH markers are required (AND): each alone has plausible look-alikes (W365-Boot
    /// physical clients may carry Windows365 policy state; the MCMD agent family also
    /// served other managed-desktop offerings).
    /// <para>
    /// Display/context metadata only — the flag rides on <c>SessionRegistration</c> and the
    /// <c>EnrollmentFactsObserved</c> signal. It is NEVER an auth input: backend device
    /// validation trusts exclusively the cert-CN-bound Graph lookup
    /// (<c>CloudPcDeviceValidator</c>), not this locally-derived value.
    /// Exception-swallowing, false on any error — mirrors the SKIP-safe bootstrap contract
    /// and the <see cref="EnrollmentRegistryDetector"/> degradation rules.
    /// </para>
    /// </summary>
    public static class CloudPcDetector
    {
        internal const string Windows365MarkerKey = @"SOFTWARE\Microsoft\Windows365";
        internal const string CmdeServiceKey = @"SYSTEM\CurrentControlSet\Services\CloudManagedDesktopExtension";

        public static bool DetectIsCloudPc()
        {
            try
            {
                return ResolveIsCloudPc(SubKeyExists);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pure decision logic for <see cref="DetectIsCloudPc"/>. Exposed internally so the
        /// marker-AND semantics can be exercised without a real registry (same seam contract
        /// as <see cref="EnrollmentRegistryDetector.ResolveHybridJoinFromValues"/>).
        /// <paramref name="subKeyExists"/> receives an HKLM-relative subkey path and reports
        /// whether it exists; a throwing probe degrades to <c>false</c>.
        /// </summary>
        internal static bool ResolveIsCloudPc(Func<string, bool> subKeyExists)
        {
            if (subKeyExists == null) return false;

            try
            {
                return subKeyExists(Windows365MarkerKey) && subKeyExists(CmdeServiceKey);
            }
            catch
            {
                return false;
            }
        }

        private static bool SubKeyExists(string subKeyPath)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(subKeyPath))
            {
                return key != null;
            }
        }
    }
}
