#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// LocalAdminAnalyzer.EvaluateAccounts — the account/admin-membership evaluation contract.
    /// Pins the two evasions that the old name-only, enabled-only scan missed: a disabled
    /// (dormant) backdoor account, and Administrators membership as an independent signal.
    /// All names are synthetic; the machine name is a fixture constant.
    /// </summary>
    public sealed class LocalAdminAnalyzerEvaluateAccountsTests
    {
        private const string Machine = "DESKTOP-TEST01";
        private const string MachineSidPrefix = "S-1-5-21-111-222-333-";

        private static readonly List<string> Allowed = new List<string>
        {
            "Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount", "defaultuser0", "defaultuser1", "adm-*"
        };

        private static LocalAdminAnalyzer.LocalAccountInfo Account(string name, bool disabled, int rid) =>
            new LocalAdminAnalyzer.LocalAccountInfo { Name = name, Disabled = disabled, Sid = MachineSidPrefix + rid };

        private static LocalGroupMember LocalMember(string name, int rid) =>
            new LocalGroupMember { DomainAndName = Machine + "\\" + name, Sid = MachineSidPrefix + rid, SidUsage = 1 };

        // -------------------------------------------------------------- disabled backdoor

        [Fact]
        public void Disabled_unexpected_account_is_still_reported()
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo>
            {
                Account("Administrator", disabled: true, rid: 500),
                Account("backdoor", disabled: true, rid: 1001)
            };

            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, new List<LocalGroupMember>(), true, Machine, Allowed);

            Assert.Equal(new[] { "backdoor" }, result.Unexpected);
            Assert.Empty(result.UnexpectedAdminMembers);
            var detail = result.AccountDetails.Single(d => (string)d["name"] == "backdoor");
            Assert.True((bool)detail["disabled"]);
            Assert.False((bool)detail["administrators_member"]);
        }

        [Fact]
        public void Disabled_unexpected_account_with_admin_membership_is_an_unexpected_admin_member()
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo>
            {
                Account("Administrator", disabled: true, rid: 500),
                Account("backdoor", disabled: true, rid: 1001)
            };
            var members = new List<LocalGroupMember>
            {
                LocalMember("Administrator", 500),
                LocalMember("backdoor", 1001)
            };

            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, members, true, Machine, Allowed);

            Assert.Equal(new[] { "backdoor" }, result.Unexpected);
            Assert.Equal(new[] { "backdoor" }, result.UnexpectedAdminMembers);
            var detail = result.AccountDetails.Single(d => (string)d["name"] == "backdoor");
            Assert.True((bool)detail["administrators_member"]);
        }

        // -------------------------------------------------------------- allowed list

        [Fact]
        public void Allowed_admin_members_are_not_flagged_but_listed()
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo>
            {
                Account("Administrator", disabled: false, rid: 500),
                Account("adm-4711", disabled: false, rid: 1002)
            };
            var members = new List<LocalGroupMember>
            {
                LocalMember("Administrator", 500),
                LocalMember("adm-4711", 1002)
            };

            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, members, true, Machine, Allowed);

            Assert.Empty(result.Unexpected);
            Assert.Empty(result.UnexpectedAdminMembers);
            Assert.Equal(2, result.AdministratorsGroupMembers.Count);
            Assert.True(result.AccountDetails.All(d => (bool)d["administrators_member"]));
        }

        // -------------------------------------------------------------- non-local members

        [Fact]
        public void Members_outside_the_machine_domain_are_listed_but_never_flagged()
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo>
            {
                Account("Administrator", disabled: true, rid: 500)
            };
            var members = new List<LocalGroupMember>
            {
                LocalMember("Administrator", 500),
                // Entra role SIDs on an Entra-joined device resolve to nothing — the API returns the SID
                new LocalGroupMember { DomainAndName = "S-1-12-1-123-456-789-1011", Sid = "S-1-12-1-123-456-789-1011", SidUsage = 8 },
                // Domain group on a hybrid device
                new LocalGroupMember { DomainAndName = "CORP\\Workstation Admins", Sid = "S-1-5-21-9-9-9-1105", SidUsage = 2 }
            };

            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, members, true, Machine, Allowed);

            Assert.Empty(result.Unexpected);
            Assert.Empty(result.UnexpectedAdminMembers);
            Assert.Equal(3, result.AdministratorsGroupMembers.Count);
            Assert.Contains("S-1-12-1-123-456-789-1011", result.AdministratorsGroupMembers);
        }

        // -------------------------------------------------------------- WMI gap

        [Fact]
        public void Local_admin_member_missing_from_the_wmi_inventory_is_still_flagged()
        {
            // WMI failed or did not list the account — the SAM group membership alone is enough.
            var members = new List<LocalGroupMember>
            {
                LocalMember("Administrator", 500),
                LocalMember("backdoor", 1001)
            };

            var result = LocalAdminAnalyzer.EvaluateAccounts(
                new List<LocalAdminAnalyzer.LocalAccountInfo>(), members, true, Machine, Allowed);

            Assert.Empty(result.Unexpected);
            Assert.Equal(new[] { "backdoor" }, result.UnexpectedAdminMembers);
        }

        [Fact]
        public void Membership_matches_by_sid_when_the_member_name_differs_in_case()
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo> { Account("BackDoor", false, 1001) };
            var members  = new List<LocalGroupMember> { LocalMember("backdoor", 1001) };

            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, members, true, Machine, Allowed);

            Assert.Equal(new[] { "BackDoor" }, result.UnexpectedAdminMembers);
        }

        // -------------------------------------------------------------- built-in Administrator

        [Theory]
        [InlineData(true,  false)]
        [InlineData(false, true)]
        public void Builtin_administrator_enabled_state_is_reported_by_rid_500(bool disabled, bool expectedEnabled)
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo>
            {
                Account("Administrator", disabled, rid: 500),
                Account("Guest", disabled: true, rid: 501)
            };

            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, new List<LocalGroupMember>(), true, Machine, Allowed);

            Assert.Equal(expectedEnabled, result.BuiltInAdministratorEnabled);
            Assert.Empty(result.Unexpected);
        }

        // -------------------------------------------------------------- native path (Windows only, net48)

        [Fact]
        public void Native_administrators_enumeration_succeeds_and_contains_the_builtin_administrator()
        {
            // Proves the P/Invoke marshalling end-to-end on the executing machine: the SAM
            // always holds the RID-500 Administrator in BUILTIN\Administrators.
            var result = LocalGroupNativeMethods.GetAdministratorsMembers();

            Assert.True(result.Succeeded, $"NET_API_STATUS={result.ErrorCode}");
            Assert.False(string.IsNullOrEmpty(result.GroupName));
            Assert.Contains(result.Members, m => m.Sid != null && m.Sid.EndsWith("-500"));
            Assert.All(result.Members, m => Assert.False(string.IsNullOrEmpty(m.DomainAndName)));
        }

        [Fact]
        public void Builtin_administrator_state_is_null_when_rid_500_is_absent()
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo> { Account("Guest", true, 501) };

            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, new List<LocalGroupMember>(), false, Machine, Allowed);

            Assert.Null(result.BuiltInAdministratorEnabled);
            Assert.False(result.AdministratorsGroupEnumerated);
        }
    }
}
