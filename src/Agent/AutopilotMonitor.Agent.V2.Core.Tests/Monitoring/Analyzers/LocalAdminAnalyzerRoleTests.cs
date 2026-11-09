#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// Roles on the allowed list: <c>name|user</c> and the standard-user built-ins (Guest,
    /// DefaultAccount, WDAGUtilityAccount by RID; kioskUser0, WsiAccount and other accounts carrying a
    /// built-in name by name) allow an account only while it is not an Administrators member. A
    /// restriction always wins; the four built-in accounts are decided by RID alone; profile folders
    /// have no role; a login allows a profile folder, never an account. All names are synthetic.
    /// </summary>
    public sealed class LocalAdminAnalyzerRoleTests
    {
        private const string Machine = "DESKTOP-TEST01";
        private const string MachineSidPrefix = "S-1-5-21-111-222-333-";

        private static List<LocalAdminAnalyzer.AllowedEntry> Entries(params string[] tenantEntries)
            => LocalAdminAnalyzer.BuildAllowedEntries(tenantEntries, new List<string>());

        private static LocalAdminAnalyzer.LocalAccountInfo Account(string name, int rid, bool disabled = false) =>
            new LocalAdminAnalyzer.LocalAccountInfo { Name = name, Disabled = disabled, Sid = MachineSidPrefix + rid };

        private static LocalGroupMember Member(string name, int rid) =>
            new LocalGroupMember { DomainAndName = Machine + "\\" + name, Sid = MachineSidPrefix + rid, SidUsage = 1 };

        private static LocalAdminAnalyzer.LocalAccountCheckResult Evaluate(
            IList<LocalAdminAnalyzer.LocalAccountInfo> accounts, IList<LocalGroupMember> admins, List<LocalAdminAnalyzer.AllowedEntry> entries,
            bool enumerated = true)
            => LocalAdminAnalyzer.EvaluateAccounts(accounts, admins, enumerated, Machine, entries);

        // -------------------------------------------------------------- WsiAccount (the field case)

        [Fact]
        public void WsiAccount_as_standard_user_with_its_profile_folder_scores_zero()
        {
            // The field case: web sign-in enabled the account for the sign-in, it holds no admin
            // membership and has C:\Users\WsiAccount — before roles existed this scored 80 (Error).
            var accounts = new[]
            {
                Account("Administrator", 500, disabled: true),
                Account("DefaultAccount", 503, disabled: true),
                Account("defaultuser0", 1000, disabled: true),
                Account("Gast", 501, disabled: true),
                Account("WDAGUtilityAccount", 504, disabled: true),
                Account("WsiAccount", 1001, disabled: true),
                Account("ADM-0A1B2C3D", 1002),
            };
            var admins = new List<LocalGroupMember>
            {
                Member("Administrator", 500),
                new LocalGroupMember { DomainAndName = "S-1-12-1-11-22-33-44", Sid = "S-1-12-1-11-22-33-44", SidUsage = 8 },
                Member("ADM-0A1B2C3D", 1002),
            };

            var result = Evaluate(accounts, admins, Entries("ADM-*"));
            var score = LocalAdminAnalyzer.ComputeConfidenceScore(
                bypassNro: false, result.Unexpected, result.UnexpectedAdminMembers, new List<string> { "jane.doe" });

            Assert.Empty(result.Unexpected);
            Assert.Empty(result.UnexpectedAdminMembers);
            Assert.Empty(result.RoleViolations);
            Assert.Equal(0, score);
        }

        [Fact]
        public void WsiAccount_in_administrators_is_a_role_violation_even_while_disabled()
        {
            var result = Evaluate(
                new[] { Account("WsiAccount", 1001, disabled: true) },
                new List<LocalGroupMember> { Member("WsiAccount", 1001) },
                Entries());

            Assert.Equal(new[] { "WsiAccount" }, result.Unexpected);
            Assert.Equal(new[] { "WsiAccount" }, result.UnexpectedAdminMembers);
            Assert.Equal(new[] { "WsiAccount" }, result.RoleViolations);
            Assert.Equal(80, LocalAdminAnalyzer.ComputeConfidenceScore(
                false, result.Unexpected, result.UnexpectedAdminMembers, new List<string>()));
        }

        [Theory]
        [InlineData("WsiAccount")]
        [InlineData("wsiaccount")]
        [InlineData("Wsi*")]
        [InlineData("*")]
        public void Tenant_entries_cannot_lift_a_builtin_restriction(string tenantEntry)
        {
            var result = Evaluate(
                new[] { Account("WsiAccount", 1001) },
                new List<LocalGroupMember> { Member("WsiAccount", 1001) },
                Entries(tenantEntry));

            Assert.Equal(new[] { "WsiAccount" }, result.RoleViolations);
        }

        [Fact]
        public void KioskUser0_in_administrators_is_a_role_violation()
        {
            var result = Evaluate(
                new[] { Account("kioskUser0", 1003) },
                new List<LocalGroupMember> { Member("kioskUser0", 1003) },
                Entries());

            Assert.Equal(new[] { "kioskUser0" }, result.RoleViolations);
        }

        // -------------------------------------------------------------- built-ins by RID

        [Theory]
        [InlineData("Guest", 501)]
        [InlineData("Gast", 501)]
        [InlineData("Invité", 501)]
        [InlineData("DefaultAccount", 503)]
        [InlineData("WDAGUtilityAccount", 504)]
        public void Builtin_accounts_that_are_never_administrators_are_flagged_as_one(string name, int rid)
        {
            // A tenant entry naming the localized account (or even "*") cannot lift it either.
            var result = Evaluate(
                new[] { Account(name, rid, disabled: true) },
                new List<LocalGroupMember> { Member(name, rid) },
                Entries(name, "*"));

            Assert.Equal(new[] { name }, result.Unexpected);
            Assert.Equal(new[] { name }, result.UnexpectedAdminMembers);
            Assert.Equal(new[] { name }, result.RoleViolations);
        }

        [Theory]
        [InlineData("Administrator")]
        [InlineData("Administrateur")]
        [InlineData("Renamed_Admin")]
        public void The_builtin_administrator_is_allowed_by_rid_under_any_name(string name)
        {
            // "Admin*|user" must not turn the real Administrator into a finding: name entries never
            // decide the RID accounts — and the built-in name entry "Administrator|user" neither.
            var result = Evaluate(
                new[] { Account(name, 500) },
                new List<LocalGroupMember> { Member(name, 500) },
                Entries("Admin*|user", "Renamed*|user"));

            Assert.Empty(result.Unexpected);
            Assert.Empty(result.RoleViolations);
            Assert.True(result.BuiltInAdministratorEnabled);
        }

        [Fact]
        public void An_ordinary_account_named_administrator_is_allowed_only_as_a_decoy()
        {
            // French device: the RID-500 account is "Administrateur", so "Administrator" is free.
            var accounts = new[]
            {
                Account("Administrateur", 500, disabled: true),
                Account("Administrator", 1004),
            };

            var decoy = Evaluate(accounts, new List<LocalGroupMember> { Member("Administrateur", 500) }, Entries());
            Assert.Empty(decoy.Unexpected);

            var impostor = Evaluate(accounts,
                new List<LocalGroupMember> { Member("Administrateur", 500), Member("Administrator", 1004) }, Entries());
            Assert.Equal(new[] { "Administrator" }, impostor.RoleViolations);
            Assert.Equal(new[] { "Administrator" }, impostor.UnexpectedAdminMembers);
        }

        [Fact]
        public void The_oobe_account_may_be_an_administrator()
        {
            var result = Evaluate(
                new[] { Account("defaultuser0", 1000) },
                new List<LocalGroupMember> { Member("defaultuser0", 1000) },
                Entries());

            Assert.Empty(result.Unexpected);
        }

        [Fact]
        public void A_missing_sid_lets_the_english_builtin_name_stand_in_for_the_rid()
        {
            var accounts = new List<LocalAdminAnalyzer.LocalAccountInfo>
            {
                new LocalAdminAnalyzer.LocalAccountInfo { Name = "Administrator", Disabled = false, Sid = null! },
                new LocalAdminAnalyzer.LocalAccountInfo { Name = "Guest", Disabled = false, Sid = null! },
            };
            var admins = new List<LocalGroupMember>
            {
                new LocalGroupMember { DomainAndName = Machine + "\\Administrator", Sid = null!, SidUsage = 1 },
                new LocalGroupMember { DomainAndName = Machine + "\\Guest", Sid = null!, SidUsage = 1 },
            };

            var result = Evaluate(accounts, admins, Entries());

            Assert.Equal(new[] { "Guest" }, result.RoleViolations);
            Assert.DoesNotContain("Administrator", result.Unexpected);
        }

        // -------------------------------------------------------------- tenant roles

        [Fact]
        public void A_standard_user_entry_allows_the_account_until_it_is_an_administrator()
        {
            var accounts = new[] { Account("frontdesk", 1005) };

            Assert.Empty(Evaluate(accounts, new List<LocalGroupMember>(), Entries("frontdesk|user")).Unexpected);

            var admin = Evaluate(accounts, new List<LocalGroupMember> { Member("frontdesk", 1005) }, Entries("frontdesk|user"));
            Assert.Equal(new[] { "frontdesk" }, admin.RoleViolations);
        }

        [Fact]
        public void A_standard_user_glob_restricts_each_matching_account()
        {
            var accounts = new[] { Account("svc-print", 1006), Account("svc-scan", 1007) };

            var result = Evaluate(accounts, new List<LocalGroupMember> { Member("svc-scan", 1007) }, Entries("svc-*|user"));

            Assert.Equal(new[] { "svc-scan" }, result.Unexpected);
            Assert.Equal(new[] { "svc-scan" }, result.RoleViolations);
        }

        [Fact]
        public void A_restriction_wins_over_a_broader_entry_that_allows_more()
        {
            var accounts = new[] { Account("adm-kiosk", 1008), Account("adm-4711", 1009) };
            var admins = new List<LocalGroupMember> { Member("adm-kiosk", 1008), Member("adm-4711", 1009) };

            var result = Evaluate(accounts, admins, Entries("adm-*", "adm-kiosk|user"));

            Assert.Equal(new[] { "adm-kiosk" }, result.RoleViolations);
            Assert.DoesNotContain("adm-4711", result.Unexpected);
        }

        [Fact]
        public void Membership_unknown_lets_a_standard_user_entry_act_like_a_plain_one()
        {
            // Enumeration failed: nobody is known to be an administrator, so nothing is a violation.
            var result = Evaluate(new[] { Account("WsiAccount", 1001) }, new List<LocalGroupMember>(), Entries(), enumerated: false);

            Assert.Empty(result.Unexpected);
            Assert.False(result.AdministratorsGroupEnumerated);
        }

        // -------------------------------------------------------------- folders are no accounts

        [Fact]
        public void An_account_named_like_a_profile_folder_is_unexpected_while_the_folder_is_allowed()
        {
            var entries = Entries();

            var result = Evaluate(new[] { Account("Public", 1010) }, new List<LocalGroupMember> { Member("Public", 1010) }, entries);

            Assert.Equal(new[] { "Public" }, result.Unexpected);
            Assert.Equal(new[] { "Public" }, result.UnexpectedAdminMembers);
            Assert.Empty(result.RoleViolations);
            Assert.Equal(LocalAdminAnalyzer.AccountDecision.Unlisted,
                LocalAdminAnalyzer.Decide("Public", MachineSidPrefix + "1010", isAdmin: false, entries));
            Assert.Contains(entries, e => e.Pattern == "Public" && e.ProfileFolderOnly);
        }

        // -------------------------------------------------------------- accounts WMI did not return

        [Fact]
        public void Administrators_members_missing_from_wmi_are_judged_as_administrators()
        {
            var members = new List<LocalGroupMember>
            {
                Member("Administrator", 500),
                Member("WsiAccount", 1001),
                Member("Gast", 501),
                Member("adm-4711", 1009),
            };

            var result = Evaluate(new List<LocalAdminAnalyzer.LocalAccountInfo>(), members, Entries("adm-*"));

            Assert.Equal(new[] { "WsiAccount", "Gast" }, result.RoleViolations);
            Assert.Equal(new[] { "WsiAccount", "Gast" }, result.Unexpected);
            Assert.Equal(new[] { "WsiAccount", "Gast" }, result.UnexpectedAdminMembers);
        }

        [Fact]
        public void A_member_wmi_already_returned_is_not_judged_twice()
        {
            var result = Evaluate(
                new[] { Account("backdoor", 1011) },
                new List<LocalGroupMember> { Member("backdoor", 1011) },
                Entries());

            Assert.Equal(new[] { "backdoor" }, result.Unexpected);
            Assert.Equal(new[] { "backdoor" }, result.UnexpectedAdminMembers);
        }

        [Fact]
        public void A_login_never_allows_an_account_not_even_when_wmi_failed()
        {
            // WMI failed: the split cannot recognise "backdoor" as local and allows it dynamically.
            // The dynamic entry is folder-only, so the signed-in backdoor administrator is still found.
            var split = LocalAdminAnalyzer.SplitLoggedInUsers(new[] { "backdoor" }, null!);
            var entries = Entries();
            entries.AddRange(split.Allowed.Select(u => new LocalAdminAnalyzer.AllowedEntry(u, LocalAccountRole.Any, profileFolderOnly: true)));

            var result = Evaluate(new List<LocalAdminAnalyzer.LocalAccountInfo>(), new List<LocalGroupMember> { Member("backdoor", 1011) }, entries);

            Assert.Equal(new[] { "backdoor" }, result.UnexpectedAdminMembers);
            Assert.Equal(new[] { "backdoor" }, result.Unexpected);
        }

        // -------------------------------------------------------------- the entry list

        [Fact]
        public void Entries_carry_the_builtins_and_skip_invalid_tenant_entries()
        {
            var ignored = new List<string>();
            var entries = LocalAdminAnalyzer.BuildAllowedEntries(
                new[] { "kiosk|user", "x|admin", "|user", "legacy*", "CONTOSO\\admin", "   ", "Guest|user" }, ignored);

            Assert.Equal(new[] { "x|admin", "|user" }, ignored);
            var accounts = entries.Where(e => !e.ProfileFolderOnly).Select(e => e.Display).ToList();
            Assert.Contains("WsiAccount|user", accounts);
            Assert.Contains("kioskUser0|user", accounts);
            Assert.Contains("defaultuser0", accounts);
            Assert.Contains("kiosk|user", accounts);
            Assert.Contains("legacy*", accounts);
            Assert.Contains("CONTOSO\\admin", accounts);
            Assert.Single(accounts, a => a == "Guest|user");
            Assert.DoesNotContain("Public", accounts);
            Assert.Contains(entries, e => e.Pattern == "All Users" && e.ProfileFolderOnly);
        }

        // -------------------------------------------------------------- scoring

        [Fact]
        public void Score_follows_the_signals()
        {
            var none = new List<string>();
            var backdoor = new List<string> { "backdoor" };

            Assert.Equal(0, LocalAdminAnalyzer.ComputeConfidenceScore(false, none, none, backdoor));
            Assert.Equal(20, LocalAdminAnalyzer.ComputeConfidenceScore(true, none, none, none));
            Assert.Equal(40, LocalAdminAnalyzer.ComputeConfidenceScore(false, backdoor, none, none));
            Assert.Equal(80, LocalAdminAnalyzer.ComputeConfidenceScore(false, backdoor, backdoor, none));
            Assert.Equal(80, LocalAdminAnalyzer.ComputeConfidenceScore(false, backdoor, none, new List<string> { "BACKDOOR" }));
            Assert.Equal(100, LocalAdminAnalyzer.ComputeConfidenceScore(true, backdoor, backdoor, backdoor));
        }
    }
}
