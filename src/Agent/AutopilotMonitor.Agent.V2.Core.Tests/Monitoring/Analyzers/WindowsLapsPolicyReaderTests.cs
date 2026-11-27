#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using Microsoft.Win32;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// WindowsLapsPolicyReader — pins the Windows LAPS policy semantics the Local Admin Analyzer allows an
    /// account on: the first root with a value is the active policy and nothing is inherited, BackupDirectory
    /// 0 switches everything off, automatic account management applies from build 26100 on, a present
    /// value of an unexpected type or range makes the policy invalid instead of being coerced, and the
    /// state's LastAccountRidUpdated is read as a DWORD only. The two VM cases are the registry of a
    /// Windows 11 26100 test device with an Intune LAPS profile (2026-11-27).
    /// </summary>
    public sealed class WindowsLapsPolicyReaderTests
    {
        private const int Build24H2 = 26100;

        private static Func<string, IReadOnlyDictionary<string, object?>?> FakeRegistry(
            Dictionary<string, Dictionary<string, object?>> keys)
            => path => keys.TryGetValue(path, out var values) ? values : null;

        private static WindowsLapsPolicy Read(Dictionary<string, Dictionary<string, object?>> keys, int? build = Build24H2)
            => WindowsLapsPolicyReader.ReadCore(FakeRegistry(keys), build);

        private static Dictionary<string, Dictionary<string, object?>> Csp(Dictionary<string, object?> values)
            => new Dictionary<string, Dictionary<string, object?>> { [WindowsLapsPolicyReader.CspKeyPath] = values };

        private static Dictionary<string, object?> AutomaticPolicy() => new Dictionary<string, object?>
        {
            ["BackupDirectory"] = 1,
            ["AutomaticAccountManagementEnabled"] = 1,
        };

        // -------------------------------------------------------------- the test device

        [Fact]
        public void Vm_profile_without_automatic_management_manages_the_builtin_administrator()
        {
            var policy = Read(new Dictionary<string, Dictionary<string, object?>>
            {
                [WindowsLapsPolicyReader.CspKeyPath] = new Dictionary<string, object?>
                {
                    ["BackupDirectory"] = 1, ["PasswordAgeDays"] = 30, ["PasswordComplexity"] = 3,
                    ["PasswordLength"] = 14, ["PostAuthenticationActions"] = 1, ["PostAuthenticationResetDelay"] = 24,
                },
                [WindowsLapsPolicyReader.LocalConfigKeyPath] = new Dictionary<string, object?>(),
                [WindowsLapsPolicyReader.StateKeyPath] = new Dictionary<string, object?>
                {
                    ["PolicySource"] = 1, ["LastAccountRidUpdated"] = 500, ["DSRMMode"] = 0,
                    ["AdministratorAccountName"] = "Administrator",
                    ["LastPasswordUpdateTime"] = RegistryValueKind.QWord, ["AzurePasswordExpiryTime"] = RegistryValueKind.QWord,
                },
            });

            Assert.Equal(WindowsLapsPolicyReader.CspRoot, policy.Root);
            Assert.Equal(WindowsLapsMode.BuiltinAdministrator, policy.Mode);
            Assert.Equal(1, policy.BackupDirectory);
            Assert.Null(policy.AccountName);
            Assert.False(policy.NamesAccount);
            Assert.Equal(500, policy.StateRid);
            Assert.Equal(1, policy.StatePolicySource);
            Assert.Null(policy.Problem);
        }

        [Fact]
        public void Vm_profile_with_automatic_management_names_WLapsAdmin_and_its_rid()
        {
            var policy = Read(new Dictionary<string, Dictionary<string, object?>>
            {
                [WindowsLapsPolicyReader.CspKeyPath] = new Dictionary<string, object?>
                {
                    ["BackupDirectory"] = 1, ["PasswordAgeDays"] = 30, ["PasswordComplexity"] = 3,
                    ["PasswordLength"] = 14, ["PostAuthenticationActions"] = 1, ["PostAuthenticationResetDelay"] = 24,
                    ["AutomaticAccountManagementEnabled"] = 1, ["AutomaticAccountManagementRandomizeName"] = 0,
                    ["AutomaticAccountManagementTarget"] = 1, ["AutomaticAccountManagementEnableAccount"] = 1,
                },
                [WindowsLapsPolicyReader.LocalConfigKeyPath] = new Dictionary<string, object?>(),
                [WindowsLapsPolicyReader.StateKeyPath] = new Dictionary<string, object?>
                {
                    ["PolicySource"] = 1, ["LastAccountRidUpdated"] = 1002, ["DSRMMode"] = 0,
                    ["LastManagedAccountRid"] = 1002, ["LastManagedAccountNameOrPrefix"] = "",
                    ["LastManagedAccountRandomizeName"] = 0,
                },
            });

            Assert.Equal(WindowsLapsMode.Automatic, policy.Mode);
            Assert.Equal("WLapsAdmin", policy.AccountName);
            Assert.False(policy.Randomized);
            Assert.True(policy.NamesAccount);
            Assert.Equal(1002, policy.StateRid);
            Assert.Equal(1002, policy.StateManagedRid);
            Assert.Null(policy.Problem);
        }

        // -------------------------------------------------------------- roots

        [Fact]
        public void Without_a_root_holding_a_value_nothing_is_configured()
        {
            var policy = Read(new Dictionary<string, Dictionary<string, object?>>
            {
                [WindowsLapsPolicyReader.LocalConfigKeyPath] = new Dictionary<string, object?>(),
            });

            Assert.Null(policy.Root);
            Assert.Equal(WindowsLapsMode.NotConfigured, policy.Mode);
            Assert.False(policy.NamesAccount);
        }

        [Fact]
        public void The_first_root_with_any_value_wins_and_nothing_is_inherited()
        {
            // An unknown value is a setting too; the CSP root then defaults BackupDirectory to 0.
            var policy = Read(new Dictionary<string, Dictionary<string, object?>>
            {
                [WindowsLapsPolicyReader.CspKeyPath] = new Dictionary<string, object?> { ["SomeFutureSetting"] = 1 },
                [WindowsLapsPolicyReader.GroupPolicyKeyPath] = AutomaticPolicy(),
            });

            Assert.Equal(WindowsLapsPolicyReader.CspRoot, policy.Root);
            Assert.Equal(WindowsLapsMode.Disabled, policy.Mode);
            Assert.False(policy.NamesAccount);
        }

        [Fact]
        public void A_lower_root_is_used_when_the_higher_ones_hold_no_value()
        {
            var policy = Read(new Dictionary<string, Dictionary<string, object?>>
            {
                [WindowsLapsPolicyReader.CspKeyPath] = new Dictionary<string, object?>(),
                [WindowsLapsPolicyReader.GroupPolicyKeyPath] = new Dictionary<string, object?>
                {
                    ["BackupDirectory"] = 2, ["AdministratorAccountName"] = "LAPSAdmin",
                },
            });

            Assert.Equal(WindowsLapsPolicyReader.GroupPolicyRoot, policy.Root);
            Assert.Equal(WindowsLapsMode.Manual, policy.Mode);
            Assert.Equal("LAPSAdmin", policy.AccountName);
        }

        [Fact]
        public void The_legacy_root_allows_nothing()
        {
            var policy = Read(new Dictionary<string, Dictionary<string, object?>>
            {
                [WindowsLapsPolicyReader.LegacyKeyPath] = new Dictionary<string, object?> { ["AdmPwdEnabled"] = 1, ["AdminAccountName"] = "LAPSAdmin" },
            });

            Assert.Equal(WindowsLapsPolicyReader.LegacyRoot, policy.Root);
            Assert.Equal(WindowsLapsMode.Legacy, policy.Mode);
            Assert.False(policy.NamesAccount);
        }

        [Fact]
        public void A_read_error_never_falls_through_to_a_lower_root()
        {
            var keys = new Dictionary<string, Dictionary<string, object?>> { [WindowsLapsPolicyReader.GroupPolicyKeyPath] = AutomaticPolicy() };
            Func<string, IReadOnlyDictionary<string, object?>?> read = path =>
                path == WindowsLapsPolicyReader.CspKeyPath ? throw new UnauthorizedAccessException() : FakeRegistry(keys)(path);

            var policy = WindowsLapsPolicyReader.ReadCore(read, Build24H2);

            Assert.Equal(WindowsLapsPolicyReader.CspRoot, policy.Root);
            Assert.Equal(WindowsLapsMode.Unreadable, policy.Mode);
            Assert.Contains("UnauthorizedAccessException", policy.Problem);
            Assert.False(policy.NamesAccount);
        }

        [Fact]
        public void Value_names_are_case_insensitive()
        {
            var policy = Read(Csp(new Dictionary<string, object?>
            {
                ["backupdirectory"] = 1, ["AUTOMATICACCOUNTMANAGEMENTENABLED"] = 1, ["automaticAccountManagementNameOrPrefix"] = "LapsAdm",
            }));

            Assert.Equal(WindowsLapsMode.Automatic, policy.Mode);
            Assert.Equal("LapsAdm", policy.AccountName);
        }

        // -------------------------------------------------------------- modes

        [Theory]
        [InlineData(null, WindowsLapsMode.Disabled)]
        [InlineData(0, WindowsLapsMode.Disabled)]
        [InlineData(1, WindowsLapsMode.Automatic)]
        [InlineData(2, WindowsLapsMode.Automatic)]
        public void BackupDirectory_decides_whether_anything_is_managed(int? backupDirectory, string expectedMode)
        {
            var values = new Dictionary<string, object?> { ["AutomaticAccountManagementEnabled"] = 1 };
            if (backupDirectory.HasValue)
                values["BackupDirectory"] = backupDirectory.Value;

            Assert.Equal(expectedMode, Read(Csp(values)).Mode);
        }

        [Theory]
        [InlineData(26099, WindowsLapsMode.Manual, "LAPSAdmin")]
        [InlineData(26100, WindowsLapsMode.Automatic, "WLapsAdmin")]
        [InlineData(26200, WindowsLapsMode.Automatic, "WLapsAdmin")]
        [InlineData(null, WindowsLapsMode.Manual, "LAPSAdmin")]
        public void Automatic_management_applies_from_build_26100_on(int? build, string expectedMode, string expectedName)
        {
            var values = AutomaticPolicy();
            values["AdministratorAccountName"] = "LAPSAdmin";

            var policy = Read(Csp(values), build);

            Assert.Equal(expectedMode, policy.Mode);
            Assert.Equal(expectedName, policy.AccountName);
        }

        [Fact]
        public void Target_zero_manages_the_builtin_administrator()
        {
            var values = AutomaticPolicy();
            values["AutomaticAccountManagementTarget"] = 0;
            values["AutomaticAccountManagementNameOrPrefix"] = "LocalAdmin";

            var policy = Read(Csp(values));

            Assert.Equal(WindowsLapsMode.BuiltinAdministrator, policy.Mode);
            Assert.False(policy.NamesAccount);
        }

        [Fact]
        public void Settings_of_automatic_management_are_ignored_while_it_is_off()
        {
            var policy = Read(Csp(new Dictionary<string, object?>
            {
                ["BackupDirectory"] = 1, ["AutomaticAccountManagementEnabled"] = 0,
                ["AutomaticAccountManagementTarget"] = 7, ["AutomaticAccountManagementNameOrPrefix"] = RegistryValueKind.Binary,
            }));

            Assert.Equal(WindowsLapsMode.BuiltinAdministrator, policy.Mode);
            Assert.Null(policy.Problem);
        }

        [Theory]
        [InlineData(null, WindowsLapsMode.BuiltinAdministrator, null)]
        [InlineData("", WindowsLapsMode.BuiltinAdministrator, null)]
        [InlineData("LAPSAdmin", WindowsLapsMode.Manual, "LAPSAdmin")]
        [InlineData("Twenty-Characters-OK", WindowsLapsMode.Manual, "Twenty-Characters-OK")]
        [InlineData("Twenty-One-Characters", WindowsLapsMode.Invalid, null)]
        public void Manual_mode_names_AdministratorAccountName(string? name, string expectedMode, string? expectedName)
        {
            var values = new Dictionary<string, object?> { ["BackupDirectory"] = 1 };
            if (name != null)
                values["AdministratorAccountName"] = name;

            var policy = Read(Csp(values));

            Assert.Equal(expectedMode, policy.Mode);
            Assert.Equal(expectedName, policy.AccountName);
        }

        [Theory]
        [InlineData("ContosoLapsAdminAccount", false, "ContosoLapsAdm")]
        [InlineData("ContosoLapsAdminAccount", true, "ContosoLapsAdm")]
        [InlineData("Adm", true, "Adm")]
        public void Automatic_names_are_cut_to_fourteen_characters(string nameOrPrefix, bool randomize, string expected)
        {
            var values = AutomaticPolicy();
            values["AutomaticAccountManagementNameOrPrefix"] = nameOrPrefix;
            values["AutomaticAccountManagementRandomizeName"] = randomize ? 1 : 0;

            var policy = Read(Csp(values));

            Assert.Equal(WindowsLapsMode.Automatic, policy.Mode);
            Assert.Equal(expected, policy.AccountName);
            Assert.Equal(randomize, policy.Randomized);
        }

        // -------------------------------------------------------------- strictness

        [Theory]
        [InlineData("")]
        [InlineData(" Admin")]
        [InlineData("Admin ")]
        [InlineData("Ad*min")]
        [InlineData("Ad?min")]
        [InlineData("Ad|min")]
        [InlineData("Ad\"min")]
        [InlineData("Ad/min")]
        [InlineData("Ad\\min")]
        [InlineData("Ad[min")]
        [InlineData("Ad:min")]
        [InlineData("Ad;min")]
        [InlineData("Ad=min")]
        [InlineData("Ad,min")]
        [InlineData("Ad+min")]
        [InlineData("Ad<min")]
        [InlineData("Ad\tmin")]
        [InlineData("...")]
        [InlineData(". .")]
        [InlineData("WLapsPending")]
        [InlineData("WLapsDefuncted123456")]
        [InlineData("ContosoLapsAdmin*")]   // the glob character would be cut away, the name is still refused
        public void An_unusable_name_makes_the_policy_invalid(string nameOrPrefix)
        {
            var values = AutomaticPolicy();
            values["AutomaticAccountManagementNameOrPrefix"] = nameOrPrefix;

            var policy = Read(Csp(values));

            Assert.Equal(WindowsLapsMode.Invalid, policy.Mode);
            Assert.Contains("AutomaticAccountManagementNameOrPrefix", policy.Problem);
            Assert.False(policy.NamesAccount);
        }

        public static IEnumerable<object[]> WrongValues() => new[]
        {
            new object[] { "BackupDirectory", 3, "BackupDirectory=3" },
            new object[] { "BackupDirectory", "1", "BackupDirectory (String)" },
            new object[] { "BackupDirectory", RegistryValueKind.QWord, "BackupDirectory (QWord)" },
            new object[] { "AutomaticAccountManagementEnabled", 2, "AutomaticAccountManagementEnabled=2" },
            new object[] { "AutomaticAccountManagementTarget", -1, "AutomaticAccountManagementTarget=-1" },
            new object[] { "AutomaticAccountManagementRandomizeName", "1", "AutomaticAccountManagementRandomizeName (String)" },
            new object[] { "AutomaticAccountManagementNameOrPrefix", 5, "AutomaticAccountManagementNameOrPrefix=5" },
            new object[] { "AutomaticAccountManagementNameOrPrefix", RegistryValueKind.ExpandString, "AutomaticAccountManagementNameOrPrefix (ExpandString)" },
        };

        [Theory]
        [MemberData(nameof(WrongValues))]
        public void A_value_of_an_unexpected_type_or_range_is_never_coerced(string valueName, object value, string expectedProblem)
        {
            var values = AutomaticPolicy();
            values[valueName] = value;

            var policy = Read(Csp(values));

            Assert.Equal(WindowsLapsMode.Invalid, policy.Mode);
            Assert.Equal(expectedProblem, policy.Problem);
            Assert.False(policy.NamesAccount);
        }

        [Fact]
        public void A_manual_account_name_of_another_type_is_invalid()
        {
            var policy = Read(Csp(new Dictionary<string, object?>
            {
                ["BackupDirectory"] = 1, ["AdministratorAccountName"] = RegistryValueKind.MultiString,
            }));

            Assert.Equal(WindowsLapsMode.Invalid, policy.Mode);
            Assert.Equal("AdministratorAccountName (MultiString)", policy.Problem);
        }

        // -------------------------------------------------------------- state

        [Theory]
        [InlineData(1002, 1002, null)]
        [InlineData(0, null, "LastAccountRidUpdated=0")]
        [InlineData("1002", null, "LastAccountRidUpdated (String)")]
        public void The_state_rid_is_a_positive_dword_only(object raw, int? expectedRid, string? expectedProblem)
        {
            var keys = Csp(AutomaticPolicy());
            keys[WindowsLapsPolicyReader.StateKeyPath] = new Dictionary<string, object?> { ["LastAccountRidUpdated"] = raw };

            var policy = Read(keys);

            Assert.Equal(WindowsLapsMode.Automatic, policy.Mode);
            Assert.Equal(expectedRid, policy.StateRid);
            Assert.Equal(expectedProblem, policy.Problem);
        }

        [Fact]
        public void Without_a_state_key_no_rid_is_known()
        {
            var policy = Read(Csp(AutomaticPolicy()));

            Assert.Null(policy.StateRid);
            Assert.Null(policy.Problem);
        }

        // -------------------------------------------------------------- helpers

        [Theory]
        [InlineData("26100.3476", 26100)]
        [InlineData("26200", 26200)]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("n/a", null)]
        public void The_major_build_is_parsed_from_CurrentBuild_UBR(string? osBuild, int? expected)
            => Assert.Equal(expected, WindowsLapsPolicyReader.ParseMajorBuild(osBuild));

        [Fact]
        public void Reading_this_machine_never_throws()
        {
            var policy = WindowsLapsPolicyReader.Read();

            Assert.NotNull(policy.Mode);
        }
    }
}
