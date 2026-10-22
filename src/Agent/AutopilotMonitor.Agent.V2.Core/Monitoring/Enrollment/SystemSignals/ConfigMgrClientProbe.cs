#nullable enable
using System;
using System.IO;
using System.ServiceProcess;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Reads the Configuration Manager client's footprint the way the V1 agent did: the
    /// <c>CCM</c> key, the version from <c>CCMSetup</c>, the assigned site code, the
    /// <c>%windir%\CCM</c> folder and the <c>CcmExec</c> service. Fail-soft: any failure yields
    /// <see cref="ConfigMgrClientSnapshot.Empty"/>. The service is queried only when its key
    /// exists, so devices without the client never throw on every poll.
    /// </summary>
    internal static class ConfigMgrClientProbe
    {
        internal const string CcmKeyPath = @"SOFTWARE\Microsoft\CCM";
        internal const string CcmSetupKeyPath = @"SOFTWARE\Microsoft\CCMSetup";
        internal const string MobileClientKeyPath = @"SOFTWARE\Microsoft\SMS\Mobile Client";
        internal const string CcmExecServiceKeyPath = @"SYSTEM\CurrentControlSet\Services\CcmExec";
        internal const string CcmExecServiceName = "CcmExec";

        /// <summary>Service state when <c>CcmExec</c> is not installed (the V1 payload value).</summary>
        internal const string ServiceNotFound = "not_found";

        /// <summary>Test seam; use <see cref="ScopedOverride"/> to guarantee cleanup.</summary>
        internal static Func<ConfigMgrClientSnapshot>? TestOverride;

        public static ConfigMgrClientSnapshot Read(AgentLogger? logger = null)
        {
            var probe = TestOverride;
            if (probe != null) return probe();

            try
            {
                // The agent is AnyCPU; the client writes the 64-bit view.
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

                bool registryFound;
                using (var ccm = hklm.OpenSubKey(CcmKeyPath))
                    registryFound = ccm != null;

                string? version;
                using (var setup = hklm.OpenSubKey(CcmSetupKeyPath))
                    version = NonEmpty(setup?.GetValue("LastValidVersion")?.ToString());

                string? siteCode;
                using (var mobileClient = hklm.OpenSubKey(MobileClientKeyPath))
                    siteCode = NonEmpty(mobileClient?.GetValue("AssignedSiteCode")?.ToString());

                bool serviceInstalled;
                using (var service = hklm.OpenSubKey(CcmExecServiceKeyPath))
                    serviceInstalled = service != null;

                var directoryFound = Directory.Exists(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "CCM"));

                return new ConfigMgrClientSnapshot(
                    registryFound, directoryFound, serviceInstalled ? ReadServiceState() : ServiceNotFound, version, siteCode);
            }
            catch (Exception ex)
            {
                logger?.Debug($"ConfigMgrClientProbe: read failed: {ex.Message}");
                return ConfigMgrClientSnapshot.Empty;
            }
        }

        private static string ReadServiceState()
        {
            try
            {
                using (var controller = new ServiceController(CcmExecServiceName))
                    return controller.Status.ToString();
            }
            catch (InvalidOperationException)
            {
                return ServiceNotFound; // removed between the key read and the query
            }
        }

        private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        /// <summary>Sets <see cref="TestOverride"/> for the scope and restores the previous value on Dispose.</summary>
        internal sealed class ScopedOverride : IDisposable
        {
            private readonly Func<ConfigMgrClientSnapshot>? _previous;
            private int _disposed;

            public ScopedOverride(Func<ConfigMgrClientSnapshot> probe)
            {
                _previous = TestOverride;
                TestOverride = probe;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
                TestOverride = _previous;
            }
        }
    }

    /// <summary>One read of the client's footprint, with the V1 confidence score.</summary>
    internal sealed class ConfigMgrClientSnapshot
    {
        public static readonly ConfigMgrClientSnapshot Empty =
            new ConfigMgrClientSnapshot(false, false, ConfigMgrClientProbe.ServiceNotFound, null, null);

        public ConfigMgrClientSnapshot(bool registryFound, bool directoryFound, string serviceState, string? version, string? siteCode)
        {
            RegistryFound = registryFound;
            DirectoryFound = directoryFound;
            ServiceState = serviceState;
            Version = version;
            SiteCode = siteCode;
        }

        public bool RegistryFound { get; }
        public bool DirectoryFound { get; }

        /// <summary><see cref="System.ServiceProcess.ServiceControllerStatus"/> name, or <see cref="ConfigMgrClientProbe.ServiceNotFound"/>.</summary>
        public string ServiceState { get; }

        public string? Version { get; }
        public string? SiteCode { get; }

        /// <summary>
        /// The V1 weights: the folder alone is too weak (it can be a remnant); the portal shows the
        /// detection from 50 on.
        /// </summary>
        public int ConfidenceScore =>
            (DirectoryFound ? 10 : 0)
            + (RegistryFound ? 20 : 0)
            + (Version != null ? 15 : 0)
            + (SiteCode != null ? 15 : 0)
            + (ServiceState != ConfigMgrClientProbe.ServiceNotFound ? 20 : 0)
            + (ServiceState == nameof(ServiceControllerStatus.Running) ? 20 : 0);

        public bool Detected => ConfidenceScore > 0;

        /// <summary>Everything the event reports; a change re-emits.</summary>
        public string Fingerprint => $"{RegistryFound}|{DirectoryFound}|{ServiceState}|{Version}|{SiteCode}";
    }
}
