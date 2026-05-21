#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
