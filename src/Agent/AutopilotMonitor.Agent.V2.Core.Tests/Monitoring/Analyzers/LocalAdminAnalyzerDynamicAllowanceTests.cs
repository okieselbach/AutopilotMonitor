#nullable enable
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// LocalAdminAnalyzer shutdown-time dynamic allowance and profile-folder filtering.
    /// Pins the CWE-807 fix: a logged-in user is only exempted when the name is NOT a local
    /// SAM account — a backdoor account that signs itself in before the shutdown scan must
    /// still be scored. All names are synthetic.
    /// </summary>
    public sealed class LocalAdminAnalyzerDynamicAllowanceTests
    {
        private static LocalAdminAnalyzer.LocalAccountInfo Account(string name, int rid = 1001) =>
            new LocalAdminAnalyzer.LocalAccountInfo { Name = name, Disabled = false, Sid = "S-1-5-21-111-222-333-" + rid };

        private static readonly List<LocalAdminAnalyzer.LocalAccountInfo> Inventory = new List<LocalAdminAnalyzer.LocalAccountInfo>
        {
            Account("Administrator", 500),
            Account("Guest", 501),
            Account("defaultuser0", 1000),
            Account("backdoor", 1001)
        };

        // -------------------------------------------------------------- logged-in split

        [Fact]
        public void Entra_or_domain_user_is_allowed_dynamically()
        {
            var split = LocalAdminAnalyzer.SplitLoggedInUsers(new[] { "A0D715F", "jane.doe" }, Inventory);

            Assert.Equal(new[] { "A0D715F", "jane.doe" }, split.Allowed);
            Assert.Empty(split.LocalAccounts);
        }

        [Fact]
        public void Logged_in_local_account_is_never_allowed_dynamically()
        {
            var split = LocalAdminAnalyzer.SplitLoggedInUsers(new[] { "backdoor" }, Inventory);

            Assert.Empty(split.Allowed);
            Assert.Equal(new[] { "backdoor" }, split.LocalAccounts);
        }

        [Fact]
        public void Local_account_match_is_case_insensitive()
        {
            var split = LocalAdminAnalyzer.SplitLoggedInUsers(new[] { "BACKDOOR" }, Inventory);

            Assert.Empty(split.Allowed);
            Assert.Equal(new[] { "BACKDOOR" }, split.LocalAccounts);
        }

        [Fact]
        public void Mixed_logins_are_split_and_blank_names_dropped()
        {
            var split = LocalAdminAnalyzer.SplitLoggedInUsers(new[] { "kftest", "", "backdoor", " " }, Inventory);

            Assert.Equal(new[] { "kftest" }, split.Allowed);
            Assert.Equal(new[] { "backdoor" }, split.LocalAccounts);
        }

        [Fact]
        public void No_logins_or_no_inventory_yield_empty_or_all_allowed()
        {
            var none = LocalAdminAnalyzer.SplitLoggedInUsers(null, Inventory);
            Assert.Empty(none.Allowed);
            Assert.Empty(none.LocalAccounts);

            // WMI failed: nothing to compare against — the SAM membership check still covers admins.
            var noInventory = LocalAdminAnalyzer.SplitLoggedInUsers(new[] { "backdoor" }, null);
            Assert.Equal(new[] { "backdoor" }, noInventory.Allowed);
        }

        // -------------------------------------------------------------- profile folders

        [Theory]
        [InlineData("All Users",         FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden, true)]
        [InlineData("Default User",      FileAttributes.Directory | FileAttributes.ReparsePoint, true)]
        [InlineData("Все пользователи",  FileAttributes.Directory | FileAttributes.ReparsePoint, true)]
        [InlineData("$4720C0479E8F4B51ABD4C5195A3F6399", FileAttributes.Directory, true)]
        [InlineData("$4720c0479e8f4b51abd4c5195a3f6399", FileAttributes.Directory, true)]
        [InlineData("Public",            FileAttributes.Directory, false)]
        [InlineData("Default",           FileAttributes.Directory | FileAttributes.Hidden, false)]
        [InlineData("backdoor",          FileAttributes.Directory, false)]
        [InlineData("$backdoor",         FileAttributes.Directory, false)]
        [InlineData("$4720C0479E8F4B51ABD4C5195A3F639",  FileAttributes.Directory, false)]   // 31 hex
        [InlineData("$4720C0479E8F4B51ABD4C5195A3F6399G", FileAttributes.Directory, false)]  // non-hex
        public void Non_profile_folders_are_recognised(string name, FileAttributes attributes, bool expected)
        {
            Assert.Equal(expected, LocalAdminAnalyzer.IsNonProfileFolder(name, attributes));
        }
    }
}
