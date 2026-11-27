#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers
{
    /// <summary>
    /// What the device's Windows LAPS configuration says about the local account it manages: the active
    /// policy (documented, Microsoft Learn "Configure policy settings for Windows LAPS") and LAPS's own
    /// record of the account whose password it last set (<c>LAPS\State</c>, undocumented). The Local
    /// Admin Analyzer allows that one account; the policy grounds the allowance, the state only ever
    /// narrows it.
    /// </summary>
    internal sealed class WindowsLapsPolicy
    {
        /// <summary>Active policy root (<see cref="WindowsLapsPolicyReader"/> root names); null when none has a value.</summary>
        public string? Root { get; set; }

        /// <summary>One of the <see cref="WindowsLapsMode"/> values.</summary>
        public string Mode { get; set; } = WindowsLapsMode.NotConfigured;

        public int? BackupDirectory { get; set; }

        /// <summary>The managed account's name; with <see cref="Randomized"/> the prefix of it. Set in the automatic and manual modes only.</summary>
        public string? AccountName { get; set; }

        /// <summary>Automatic mode only: the name is the prefix plus six random digits.</summary>
        public bool? Randomized { get; set; }

        /// <summary>Windows build (CurrentBuild) that decided whether automatic account management applies.</summary>
        public int? OsBuild { get; set; }

        /// <summary><c>LAPS\State\LastAccountRidUpdated</c>: RID of the account whose password LAPS last set.</summary>
        public int? StateRid { get; set; }

        /// <summary><c>LAPS\State\LastManagedAccountRid</c> (automatic mode) — reported only.</summary>
        public int? StateManagedRid { get; set; }

        /// <summary><c>LAPS\State\PolicySource</c> — reported only.</summary>
        public int? StatePolicySource { get; set; }

        /// <summary>The value that could not be read or understood ("BackupDirectory=7", "LastAccountRidUpdated (QWord)").</summary>
        public string? Problem { get; set; }

        /// <summary>True when the policy names one local account (automatic or manual mode).</summary>
        public bool NamesAccount => (Mode == WindowsLapsMode.Automatic || Mode == WindowsLapsMode.Manual) && AccountName != null;
    }

    /// <summary>Wire values of <see cref="WindowsLapsPolicy.Mode"/>.</summary>
    internal static class WindowsLapsMode
    {
        public const string NotConfigured = "not_configured";

        /// <summary><c>BackupDirectory</c> 0: "If BackupDirectory is set to Disabled, all other settings are ignored."</summary>
        public const string Disabled = "disabled";

        /// <summary>Legacy Microsoft LAPS policy root — not evaluated.</summary>
        public const string Legacy = "legacy";

        /// <summary>LAPS manages the built-in Administrator; RID 500 is allowed by RID already.</summary>
        public const string BuiltinAdministrator = "builtin_administrator";

        public const string Automatic = "automatic";

        public const string Manual = "manual";

        public const string Invalid = "invalid";

        public const string Unreadable = "unreadable";
    }

    /// <summary>
    /// Reads the Windows LAPS policy and state fresh at every analysis (Intune policies land DURING the
    /// enrollment). Strict on purpose: a present value of an unexpected type or range makes the policy
    /// <see cref="WindowsLapsMode.Invalid"/> instead of being coerced, because a value Windows LAPS itself
    /// ignores must never widen what the analyzer allows. Value types verified on Windows 11 26100 with an
    /// Intune LAPS profile (2026-11-27): every number is a REG_DWORD, an unset setting is absent.
    /// </summary>
    internal static class WindowsLapsPolicyReader
    {
        public const string CspKeyPath = @"SOFTWARE\Microsoft\Policies\LAPS";
        public const string GroupPolicyKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\LAPS";
        public const string LocalConfigKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\LAPS\Config";
        public const string LegacyKeyPath = @"SOFTWARE\Policies\Microsoft Services\AdmPwd";
        public const string StateKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\LAPS\State";

        public const string BackupDirectoryValue = "BackupDirectory";
        public const string AdministratorAccountNameValue = "AdministratorAccountName";
        public const string AutomaticEnabledValue = "AutomaticAccountManagementEnabled";
        public const string AutomaticTargetValue = "AutomaticAccountManagementTarget";
        public const string AutomaticNameOrPrefixValue = "AutomaticAccountManagementNameOrPrefix";
        public const string AutomaticRandomizeNameValue = "AutomaticAccountManagementRandomizeName";
        public const string StateRidValue = "LastAccountRidUpdated";
        public const string StateManagedRidValue = "LastManagedAccountRid";
        public const string StatePolicySourceValue = "PolicySource";

        /// <summary>"This setting defaults to WLapsAdmin."</summary>
        public const string DefaultAutomaticAccountName = "WLapsAdmin";

        /// <summary>Automatic account management exists from Windows 11 24H2 / Server 2025 on; older builds ignore its settings.</summary>
        public const int AutomaticAccountManagementMinBuild = 26100;

        /// <summary>"Account names specified by AutomaticAccountManagementNameOrPrefix that are longer than 14 characters are truncated."</summary>
        public const int AutomaticNameMaxLength = 14;

        /// <summary>"Windows local account names have a maximum length of 20 characters."</summary>
        public const int AccountNameMaxLength = 20;

        public const string CspRoot = "csp";
        public const string GroupPolicyRoot = "group_policy";
        public const string LocalConfigRoot = "local_config";
        public const string LegacyRoot = "legacy";

        /// <summary>
        /// Windows LAPS queries the roots top-down; the first with at least one setting is the active policy
        /// ("Policy settings are never shared or inherited across policy key roots"). An existing key without
        /// values is skipped — on a Windows 11 26100 test device <c>LAPS\Config</c> exists without values.
        /// </summary>
        private static readonly (string Name, string KeyPath)[] Roots =
        {
            (CspRoot, CspKeyPath),
            (GroupPolicyRoot, GroupPolicyKeyPath),
            (LocalConfigRoot, LocalConfigKeyPath),
            (LegacyRoot, LegacyKeyPath),
        };

        private static readonly char[] ForbiddenNameCharacters =
            (LocalAdminAllowList.ForbiddenNameCharacters + "|*?").ToCharArray();

        /// <summary>Reads policy and state from the 64-bit registry view.</summary>
        public static WindowsLapsPolicy Read() => ReadCore(ReadKeyValues, ParseMajorBuild(DeviceInfoProvider.GetOsBuild()));

        /// <summary>
        /// Testable core: <paramref name="readKeyValues"/> maps a key path to all its values (null when the
        /// key is missing); REG_DWORD arrives as <see cref="int"/>, REG_SZ as <see cref="string"/>, any other
        /// type as its <see cref="RegistryValueKind"/>.
        /// </summary>
        internal static WindowsLapsPolicy ReadCore(Func<string, IReadOnlyDictionary<string, object?>?> readKeyValues, int? osBuild)
        {
            var policy = new WindowsLapsPolicy { OsBuild = osBuild };
            ReadState(readKeyValues, policy);

            foreach (var (name, keyPath) in Roots)
            {
                IReadOnlyDictionary<string, object?>? values;
                try
                {
                    values = readKeyValues(keyPath);
                }
                catch (Exception ex)
                {
                    // Never fall through to a lower root: LAPS would still use this one.
                    policy.Root = name;
                    policy.Mode = WindowsLapsMode.Unreadable;
                    policy.Problem = $"{keyPath} ({ex.GetType().Name})";
                    return policy;
                }

                if (values == null || values.Count == 0)
                    continue;

                policy.Root = name;
                ApplyRoot(policy, ToCaseInsensitive(values), osBuild);
                return policy;
            }

            return policy;
        }

        private static void ApplyRoot(WindowsLapsPolicy policy, Dictionary<string, object?> values, int? osBuild)
        {
            if (policy.Root == LegacyRoot)
            {
                policy.Mode = WindowsLapsMode.Legacy;
                return;
            }

            string? problem = null;
            var backupDirectory = ReadDword(values, BackupDirectoryValue, fallback: 0, max: 2, ref problem);
            if (problem != null)
            {
                SetInvalid(policy, problem);
                return;
            }

            policy.BackupDirectory = backupDirectory;
            if (backupDirectory == 0)
            {
                policy.Mode = WindowsLapsMode.Disabled;
                return;
            }

            // Older builds ignore the automatic settings, and with automatic management off its other
            // settings are ignored too: only values on the path Windows evaluates are read.
            if (osBuild >= AutomaticAccountManagementMinBuild)
            {
                var enabled = ReadDword(values, AutomaticEnabledValue, fallback: 0, max: 1, ref problem);
                if (problem != null)
                {
                    SetInvalid(policy, problem);
                    return;
                }

                if (enabled == 1)
                {
                    ApplyAutomatic(policy, values);
                    return;
                }
            }

            // Manual mode: "If the AdministratorAccountName setting is empty, the built-in Administrator
            // account is managed, otherwise AdministratorAccountName specifies the name of a custom local account."
            var administratorAccountName = ReadString(values, AdministratorAccountNameValue, ref problem);
            if (problem != null)
            {
                SetInvalid(policy, problem);
                return;
            }

            if (administratorAccountName == null || administratorAccountName.Length == 0)
            {
                policy.Mode = WindowsLapsMode.BuiltinAdministrator;
                return;
            }

            if (administratorAccountName.Length > AccountNameMaxLength || !IsUsableAccountName(administratorAccountName))
            {
                SetInvalid(policy, $"{AdministratorAccountNameValue}='{administratorAccountName}'");
                return;
            }

            policy.Mode = WindowsLapsMode.Manual;
            policy.AccountName = administratorAccountName;
        }

        private static void ApplyAutomatic(WindowsLapsPolicy policy, Dictionary<string, object?> values)
        {
            string? problem = null;
            var target = ReadDword(values, AutomaticTargetValue, fallback: 1, max: 1, ref problem);
            var randomize = ReadDword(values, AutomaticRandomizeNameValue, fallback: 0, max: 1, ref problem);
            var nameOrPrefix = ReadString(values, AutomaticNameOrPrefixValue, ref problem);
            if (problem != null)
            {
                SetInvalid(policy, problem);
                return;
            }

            if (target == 0)
            {
                policy.Mode = WindowsLapsMode.BuiltinAdministrator;
                return;
            }

            // Absent means the documented default; present but empty is not documented and allows nothing.
            var name = nameOrPrefix ?? DefaultAutomaticAccountName;
            var truncated = name.Length > AutomaticNameMaxLength ? name.Substring(0, AutomaticNameMaxLength) : name;
            if (!IsUsableAccountName(name) || !IsUsableAccountName(truncated))
            {
                SetInvalid(policy, $"{AutomaticNameOrPrefixValue}='{name}'");
                return;
            }

            policy.Mode = WindowsLapsMode.Automatic;
            policy.AccountName = truncated;
            policy.Randomized = randomize == 1;
        }

        private static void ReadState(Func<string, IReadOnlyDictionary<string, object?>?> readKeyValues, WindowsLapsPolicy policy)
        {
            IReadOnlyDictionary<string, object?>? raw;
            try
            {
                raw = readKeyValues(StateKeyPath);
            }
            catch (Exception ex)
            {
                policy.Problem = $"{StateKeyPath} ({ex.GetType().Name})";
                return;
            }

            if (raw == null)
                return;

            var values = ToCaseInsensitive(raw);
            policy.StateManagedRid = values.TryGetValue(StateManagedRidValue, out var managed) && managed is int managedRid ? managedRid : (int?)null;
            policy.StatePolicySource = values.TryGetValue(StatePolicySourceValue, out var source) && source is int sourceValue ? sourceValue : (int?)null;

            if (!values.TryGetValue(StateRidValue, out var rid) || rid == null)
                return;
            if (rid is int ridValue && ridValue > 0)
                policy.StateRid = ridValue;
            else
                policy.Problem = Describe(StateRidValue, rid);
        }

        /// <summary>
        /// A name Windows would accept for a local account, checked character by character (length is the
        /// caller's): not empty, no surrounding white space, no control characters, none of the characters
        /// Windows rejects, not only dots and spaces, and not one of LAPS's own temporary or retired names.
        /// </summary>
        internal static bool IsUsableAccountName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[name.Length - 1]))
                return false;
            if (name.Any(char.IsControl) || name.IndexOfAny(ForbiddenNameCharacters) >= 0)
                return false;
            if (name.All(c => c == '.' || c == ' '))
                return false;
            return !name.StartsWith("WLapsPending", StringComparison.OrdinalIgnoreCase)
                   && !name.StartsWith("WLapsDefuncted", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Major build of a "CurrentBuild.UBR" string ("26100.3476" → 26100); null when unknown.</summary>
        internal static int? ParseMajorBuild(string? osBuild)
        {
            if (osBuild == null || osBuild.Length == 0)
                return null;
            var dot = osBuild.IndexOf('.');
            var major = dot >= 0 ? osBuild.Substring(0, dot) : osBuild;
            return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var build) ? build : (int?)null;
        }

        private static int? ReadDword(Dictionary<string, object?> values, string name, int fallback, int max, ref string? problem)
        {
            if (!values.TryGetValue(name, out var raw) || raw == null)
                return fallback;
            if (raw is int value && value >= 0 && value <= max)
                return value;
            problem ??= Describe(name, raw);
            return null;
        }

        private static string? ReadString(Dictionary<string, object?> values, string name, ref string? problem)
        {
            if (!values.TryGetValue(name, out var raw) || raw == null)
                return null;
            if (raw is string value)
                return value;
            problem ??= Describe(name, raw);
            return null;
        }

        private static string Describe(string name, object raw)
            => raw is int value ? $"{name}={value}" : $"{name} ({(raw is string ? "String" : raw.ToString())})";

        private static void SetInvalid(WindowsLapsPolicy policy, string problem)
        {
            policy.Mode = WindowsLapsMode.Invalid;
            policy.Problem = problem;
        }

        private static Dictionary<string, object?> ToCaseInsensitive(IReadOnlyDictionary<string, object?> values)
        {
            var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
                copy[pair.Key] = pair.Value;
            return copy;
        }

        private static IReadOnlyDictionary<string, object?>? ReadKeyValues(string keyPath)
        {
            // Registry64 explicitly — SOFTWARE is WOW64-redirected, and the LAPS keys live in the 64-bit view
            // regardless of the agent's bitness.
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(keyPath))
            {
                if (key == null)
                    return null;

                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in key.GetValueNames())
                {
                    var kind = key.GetValueKind(name);
                    var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var understood = (kind == RegistryValueKind.DWord && value is int)
                                     || (kind == RegistryValueKind.String && value is string);
                    values[name] = understood ? value : kind;
                }
                return values;
            }
        }
    }
}
