#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Registry-path constants + read helpers for RealmJoin (RJ) deployment-state observation.
    /// RJ writes its deployment phase + per-package install state into well-known registry
    /// locations; the agent watches those keys to extend the V2 enrollment session lifetime
    /// while RJ is in flight.
    /// </summary>
    /// <remarks>
    /// <see cref="MachinePackagesRegistryPath"/> uses HKLM. The same sub-path applies under
    /// HKEY_USERS\&lt;sid&gt;\... for user-scope packages; resolve the active user's SID via
    /// <c>UserSidResolver</c> before opening the user hive.
    /// </remarks>
    internal static class RealmJoinInfo
    {
        /// <summary>Service-parameters key. Existence == "RJ is installed on this device".</summary>
        public const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\realmjoin\Parameters";

        /// <summary>Always-existing Services root — parent-watched until <c>realmjoin</c> appears.</summary>
        public const string ServicesRootPath = @"SYSTEM\CurrentControlSet\Services";

        /// <summary>RJ service key (parent of <c>Parameters</c>). Appearance == RJ installed.</summary>
        public const string ServiceRealmJoinKeyPath = @"SYSTEM\CurrentControlSet\Services\realmjoin";

        /// <summary>Service-key subkey name (used by the parent-watch appearance check).</summary>
        public const string ServiceRealmJoinKeyName = "realmjoin";

        /// <summary>Always-existing HKLM\SOFTWARE root — parent-watched until <c>RealmJoin</c> appears.</summary>
        public const string MachineSoftwareRoot = @"SOFTWARE";

        /// <summary>Machine-scope RJ root: <c>HKLM\SOFTWARE\RealmJoin</c>. Watched with <c>watchSubtree:true</c> for all package changes.</summary>
        public const string MachineRealmJoinPath = @"SOFTWARE\RealmJoin";

        /// <summary>Machine-scope RealmJoin subkey name (used by the parent-watch appearance check).</summary>
        public const string MachineRealmJoinKeyName = "RealmJoin";

        /// <summary>Machine-scope packages root: <c>HKLM\SOFTWARE\RealmJoin\Packages</c> — used for sub-key enumeration on each wake-up.</summary>
        public const string MachinePackagesRegistryPath = @"SOFTWARE\RealmJoin\Packages";

        /// <summary>User-hive SOFTWARE sub-root (joined under <c>HKEY_USERS\&lt;sid&gt;\</c>).</summary>
        public const string UserSoftwareSubRoot = @"SOFTWARE";

        /// <summary>User-scope RJ root sub-path (joined under <c>HKEY_USERS\&lt;sid&gt;\</c>).</summary>
        public const string UserRealmJoinSubPath = @"SOFTWARE\RealmJoin";

        /// <summary>User-scope packages sub-path: <c>HKEY_USERS\&lt;sid&gt;\SOFTWARE\RealmJoin\Packages</c>.</summary>
        public const string UserPackagesRegistrySubPath = @"SOFTWARE\RealmJoin\Packages";

        // Value names under the Parameters key.
        public const string DeploymentPhaseValueName = "DeploymentPhase";

        // Value names under each package sub-key.
        public const string DisplayNameValueName = "DisplayName";
        public const string VersionValueName = "Version";
        public const string SuccessValueName = "Success";
        public const string LastExitCodeValueName = "LastExitCode";

        // Resolved-phase value per the RJ enum (RealmJoin.Core.SoftwarePackaging.DeploymentPhase).
        public const int PhaseCompletedFirstDeployment = 110;

        /// <summary>
        /// Minimum DWORD value indicating RJ has moved out of <c>Blank (0)</c> into an active
        /// or terminal deployment phase. Below this the SOFTWARE\RealmJoin subtree may still
        /// be written by adjacent IME packages — package watchers stay disarmed until RJ owns
        /// the keys.
        /// </summary>
        public const int PhaseRunningThresholdMin = 100;

        /// <summary>
        /// Map a raw <c>DeploymentPhase</c> DWORD to its RJ-internal enum name. Mirrors
        /// <c>RealmJoin.Core.SoftwarePackaging.DeploymentPhase</c> as documented at feature
        /// design time. Unknown values fall back to the numeric form so future RJ-introduced
        /// phases stay observable instead of being collapsed to "Unknown".
        /// </summary>
        public static string PhaseDisplayName(int phase)
        {
            switch (phase)
            {
                case 0:   return "Blank";
                case 100: return "RunningFirstDeployment";
                case 101: return "RunningFirstDeploymentAuto";
                case 110: return "CompletedFirstDeployment";
                case 200: return "RunningDeployment";
                case 210: return "CompletedDeployment";
                case 500: return "ManualDeployment";
                case 600: return "NestedFromShim";
                default:  return phase.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Canonical 64-bit install path for the RJ host executable.</summary>
        public const string RealmJoinExePath = @"C:\Program Files\RealmJoin\RealmJoin.exe";

        /// <summary>Defensive fallback for legacy 32-bit installs.</summary>
        public const string RealmJoinExePathX86 = @"C:\Program Files (x86)\RealmJoin\RealmJoin.exe";

        /// <summary>
        /// Release-channel name reported when the version string carries no SemVer prerelease
        /// tag. Per the RJ developer, only beta/canary builds are tagged — untagged == stable.
        /// </summary>
        public const string ReleaseChannelStable = "release";

        /// <summary>
        /// Best-effort read of <c>RealmJoin.exe</c>'s version + release channel via
        /// <see cref="FileVersionInfo.GetVersionInfo"/>. Wrapped in a defensive try/catch so a
        /// missing/locked/inaccessible binary returns an empty result instead of throwing —
        /// version is observability-only and must never block the detection signal. Probes the
        /// canonical 64-bit path first, then the (x86) fallback.
        /// </summary>
        public static RealmJoinVersionInfo TryReadRealmJoinVersionInfo()
        {
            return TryReadVersionInfo(RealmJoinExePath) ?? TryReadVersionInfo(RealmJoinExePathX86) ?? RealmJoinVersionInfo.Empty;
        }

        private static RealmJoinVersionInfo? TryReadVersionInfo(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var info = FileVersionInfo.GetVersionInfo(path);
                // RJ inverts the usual .NET SDK resource layout: the FileVersion STRING entry
                // carries the full SemVer including the channel tag and build metadata
                // ("4.21.6-canary+476277.d320cac0") while the ProductVersion string is the
                // channel-less "4.21.6". Prefer FileVersion, fall back to ProductVersion.
                var raw = !string.IsNullOrWhiteSpace(info.FileVersion) ? info.FileVersion : info.ProductVersion;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return ParseVersionAndChannel(raw!);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Split a SemVer-shaped version string (<c>version[-prerelease][+buildmetadata]</c>)
        /// into the bare version and the RJ release channel. The prerelease tag IS the channel
        /// ("beta", "canary"); absence of a tag means <see cref="ReleaseChannelStable"/>.
        /// Build metadata is dropped.
        /// </summary>
        internal static RealmJoinVersionInfo ParseVersionAndChannel(string raw)
        {
            var value = raw.Trim();
            var plus = value.IndexOf('+');
            if (plus >= 0) value = value.Substring(0, plus);

            var dash = value.IndexOf('-');
            if (dash < 0)
            {
                return new RealmJoinVersionInfo(
                    productVersion: value.Length == 0 ? null : value,
                    releaseChannel: value.Length == 0 ? null : ReleaseChannelStable);
            }

            var version = value.Substring(0, dash).Trim();
            var channel = value.Substring(dash + 1).Trim();
            return new RealmJoinVersionInfo(
                productVersion: version.Length == 0 ? null : version,
                releaseChannel: channel.Length == 0 ? ReleaseChannelStable : channel);
        }

        /// <summary>
        /// Read the <c>DeploymentPhase</c> DWORD from the Parameters key. Returns null when the
        /// key/value does not exist or is unreadable.
        /// </summary>
        public static int? TryReadDeploymentPhase(RegistryHive hive, string? subPathOverride = null)
        {
            try
            {
                var path = subPathOverride ?? ServiceRegistryPath;
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(path, writable: false))
                {
                    if (key == null) return null;
                    var raw = key.GetValue(DeploymentPhaseValueName);
                    if (raw == null) return null;
                    if (raw is int i) return i;
                    if (int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Enumerate the immediate child sub-key names under the given packages root.
        /// Returns an empty list when the root does not exist.
        /// </summary>
        public static IReadOnlyList<string> EnumeratePackageIds(RegistryHive hive, string packagesPath)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(packagesPath, writable: false))
                {
                    if (key == null) return Array.Empty<string>();
                    return key.GetSubKeyNames();
                }
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Read a single package's current snapshot. Returns <c>true</c> when the sub-key
        /// exists and is readable, even if individual values are missing — caller decides
        /// whether <see cref="RealmJoinPackageSnapshot.DisplayName"/> alone counts as "started"
        /// or full Success+LastExitCode counts as "completed".
        /// </summary>
        public static bool TryReadPackage(
            RegistryHive hive,
            string packagesPath,
            string packageId,
            out RealmJoinPackageSnapshot snapshot)
        {
            snapshot = default;
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey($"{packagesPath}\\{packageId}", writable: false))
                {
                    if (key == null) return false;

                    var displayName = key.GetValue(DisplayNameValueName) as string;
                    var version = key.GetValue(VersionValueName) as string;

                    int? successInt = null;
                    var rawSuccess = key.GetValue(SuccessValueName);
                    if (rawSuccess is int sInt) successInt = sInt;
                    else if (rawSuccess != null && int.TryParse(rawSuccess.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sParsed)) successInt = sParsed;

                    int? lastExitCode = null;
                    var rawExit = key.GetValue(LastExitCodeValueName);
                    if (rawExit is int eInt) lastExitCode = eInt;
                    else if (rawExit != null && int.TryParse(rawExit.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var eParsed)) lastExitCode = eParsed;

                    snapshot = new RealmJoinPackageSnapshot(
                        packageId: packageId,
                        displayName: displayName,
                        version: version,
                        success: successInt.HasValue ? (bool?)(successInt.Value != 0) : null,
                        lastExitCode: lastExitCode);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Version + release channel of the installed <c>RealmJoin.exe</c>, parsed from its
    /// file-version resource by <see cref="RealmJoinInfo.TryReadRealmJoinVersionInfo"/>.
    /// Both fields are <c>null</c> when the binary is missing or unreadable.
    /// </summary>
    internal readonly struct RealmJoinVersionInfo
    {
        public static readonly RealmJoinVersionInfo Empty = new RealmJoinVersionInfo(null, null);

        public RealmJoinVersionInfo(string? productVersion, string? releaseChannel)
        {
            ProductVersion = productVersion;
            ReleaseChannel = releaseChannel;
        }

        /// <summary>Bare version without prerelease tag or build metadata, e.g. <c>4.21.6</c>.</summary>
        public string? ProductVersion { get; }

        /// <summary>
        /// SemVer prerelease tag ("beta", "canary") or <see cref="RealmJoinInfo.ReleaseChannelStable"/>
        /// when the version string carries no tag.
        /// </summary>
        public string? ReleaseChannel { get; }
    }

    /// <summary>
    /// Read-only snapshot of a single RealmJoin package sub-key. Built by
    /// <see cref="RealmJoinInfo.TryReadPackage"/>; consumed by <c>RealmJoinWatcher</c> to
    /// decide whether to emit a started / completed event. Public because the watcher's
    /// public <see cref="RealmJoinPackageEventArgs"/> exposes the same fields and the test
    /// suite invokes the trigger helpers across assemblies.
    /// </summary>
    public readonly struct RealmJoinPackageSnapshot
    {
        public RealmJoinPackageSnapshot(string packageId, string? displayName, string? version, bool? success, int? lastExitCode)
        {
            PackageId = packageId;
            DisplayName = displayName;
            Version = version;
            Success = success;
            LastExitCode = lastExitCode;
        }

        public string PackageId { get; }
        public string? DisplayName { get; }
        public string? Version { get; }
        public bool? Success { get; }
        public int? LastExitCode { get; }

        public bool HasStartedMarker => !string.IsNullOrEmpty(DisplayName);
        public bool HasCompletionMarker => Success.HasValue && LastExitCode.HasValue;
    }
}
