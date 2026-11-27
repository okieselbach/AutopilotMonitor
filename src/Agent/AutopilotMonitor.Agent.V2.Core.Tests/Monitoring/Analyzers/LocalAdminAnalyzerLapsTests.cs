#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// The account the device's own Windows LAPS manages is allowed in any role — only when the active
    /// policy names it AND LAPS's state records its RID as the account whose password it last set. The
    /// entry is bound to that SID; a squatter is allowed only once LAPS has overwritten its password, and
    /// what LAPS pushes aside (WLapsDefuncted…) or leaves behind (WLapsPending…) stays reported. Chains
    /// BuildAllowedEntries → ResolveLapsManagedAccount → BuildEffectiveEntries → EvaluateAccounts → score.
    /// All names and SIDs are synthetic.
    /// </summary>
    public sealed class LocalAdminAnalyzerLapsTests
    {
        private const string Machine = "DESKTOP-TEST01";
        private const string MachineSidPrefix = "S-1-5-21-111-222-333-";

        private static string Sid(int rid) => MachineSidPrefix + rid;

        private static LocalAdminAnalyzer.LocalAccountInfo Account(string name, int rid, bool disabled = false) =>
            new LocalAdminAnalyzer.LocalAccountInfo { Name = name, Disabled = disabled, Sid = Sid(rid) };

        private static LocalGroupMember Member(string name, int rid) =>
            new LocalGroupMember { DomainAndName = Machine + "\\" + name, Sid = Sid(rid), SidUsage = 1 };

        private static WindowsLapsPolicy Automatic(int? stateRid, string name = "WLapsAdmin", bool randomized = false) => new WindowsLapsPolicy
        {
            Root = WindowsLapsPolicyReader.CspRoot, Mode = WindowsLapsMode.Automatic, BackupDirectory = 1,
            AccountName = name, Randomized = randomized, OsBuild = 26100, StateRid = stateRid,
        };

        private static WindowsLapsPolicy Manual(int? stateRid, string name) => new WindowsLapsPolicy
        {
            Root = WindowsLapsPolicyReader.CspRoot, Mode = WindowsLapsMode.Manual, BackupDirectory = 1,
            AccountName = name, OsBuild = 26100, StateRid = stateRid,
        };

        private sealed class Outcome
        {
            public LocalAdminAnalyzer.LapsResolution Resolution = null!;
            public LocalAdminAnalyzer.LocalAccountCheckResult Accounts = null!;
            public int Score;
        }

        /// <summary>One run: LAPS resolution against the inventory, merge, evaluation, score (no profile folders).</summary>
        private static Outcome Run(
            WindowsLapsPolicy? policy, IList<LocalAdminAnalyzer.LocalAccountInfo> accounts, IList<LocalGroupMember> admins,
            params string[] tenantEntries)
        {
            var resolution = LocalAdminAnalyzer.ResolveLapsManagedAccount(policy!, accounts);
            var entries = LocalAdminAnalyzer.BuildEffectiveEntries(
                LocalAdminAnalyzer.BuildAllowedEntries(tenantEntries, new List<string>()), new List<string>(), resolution.Entry);
            var result = LocalAdminAnalyzer.EvaluateAccounts(accounts, admins, true, Machine, entries);
            var score = LocalAdminAnalyzer.ComputeConfidenceScore(false, result.Unexpected, result.UnexpectedAdminMembers, new List<string>());
            return new Outcome { Resolution = resolution, Accounts = result, Score = score };
        }

        // -------------------------------------------------------------- the field case

        [Fact]
        public void The_automatic_LAPS_account_LAPS_rotated_scores_zero()
        {
            var outcome = Run(Automatic(stateRid: 1002),
                new[] { Account("Administrator", 500, disabled: true), Account("WLapsAdmin", 1002) },
                new List<LocalGroupMember> { Member("Administrator", 500), Member("WLapsAdmin", 1002) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Matched, outcome.Resolution.Outcome);
            Assert.Equal("WLapsAdmin", outcome.Resolution.Account);
            Assert.Empty(outcome.Accounts.Unexpected);
            Assert.Equal(0, outcome.Score);
        }

        [Fact]
        public void Without_a_LAPS_policy_the_same_account_is_an_unexpected_administrator()
        {
            var outcome = Run(new WindowsLapsPolicy(),
                new[] { Account("WLapsAdmin", 1002) }, new List<LocalGroupMember> { Member("WLapsAdmin", 1002) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.NotApplicable, outcome.Resolution.Outcome);
            Assert.Equal(new[] { "WLapsAdmin" }, outcome.Accounts.UnexpectedAdminMembers);
            Assert.Equal(80, outcome.Score);
        }

        [Theory]
        [InlineData(WindowsLapsMode.NotConfigured)]
        [InlineData(WindowsLapsMode.Disabled)]
        [InlineData(WindowsLapsMode.BuiltinAdministrator)]
        [InlineData(WindowsLapsMode.Legacy)]
        [InlineData(WindowsLapsMode.Invalid)]
        [InlineData(WindowsLapsMode.Unreadable)]
        public void A_policy_that_names_no_account_allows_nothing(string mode)
        {
            var policy = Automatic(stateRid: 1002);
            policy.Mode = mode;

            var outcome = Run(policy, new[] { Account("WLapsAdmin", 1002) }, new List<LocalGroupMember> { Member("WLapsAdmin", 1002) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.NotApplicable, outcome.Resolution.Outcome);
            Assert.Null(outcome.Resolution.Entry);
            Assert.Equal(80, outcome.Score);
        }

        // -------------------------------------------------------------- the state binding

        [Fact]
        public void Before_LAPS_rotated_anything_nothing_is_allowed()
        {
            var outcome = Run(Automatic(stateRid: null),
                new[] { Account("WLapsAdmin", 1002) }, new List<LocalGroupMember> { Member("WLapsAdmin", 1002) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.NotRotated, outcome.Resolution.Outcome);
            Assert.Equal(80, outcome.Score);
        }

        [Fact]
        public void A_squatter_with_the_policy_name_is_reported_until_LAPS_takes_the_name_over()
        {
            var admins = new List<LocalGroupMember> { Member("WLapsAdmin", 1003) };

            // Before LAPS processed the policy: the state still names the built-in Administrator.
            var before = Run(Automatic(stateRid: 500), new[] { Account("Administrator", 500, disabled: true), Account("WLapsAdmin", 1003) }, admins);
            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Builtin, before.Resolution.Outcome);
            Assert.Equal(new[] { "WLapsAdmin" }, before.Accounts.UnexpectedAdminMembers);
            Assert.Equal(80, before.Score);

            // LAPS renamed the squatter aside (same RID, still an administrator) and created its own account.
            var after = Run(Automatic(stateRid: 1004),
                new[] { Account("WLapsDefuncted482913", 1003, disabled: true), Account("WLapsAdmin", 1004) },
                new List<LocalGroupMember> { Member("WLapsDefuncted482913", 1003), Member("WLapsAdmin", 1004) });
            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Matched, after.Resolution.Outcome);
            Assert.Equal(new[] { "WLapsDefuncted482913" }, after.Accounts.UnexpectedAdminMembers);
            Assert.Equal(80, after.Score);
        }

        [Fact]
        public void A_retired_account_without_admin_rights_still_counts_as_unexpected()
        {
            var outcome = Run(Automatic(stateRid: 1004),
                new[] { Account("WLapsDefuncted482913", 1003, disabled: true), Account("WLapsPending117204", 1005, disabled: true), Account("WLapsAdmin", 1004) },
                new List<LocalGroupMember> { Member("WLapsAdmin", 1004) });

            Assert.Equal(new[] { "WLapsDefuncted482913", "WLapsPending117204" }, outcome.Accounts.Unexpected);
            Assert.Empty(outcome.Accounts.UnexpectedAdminMembers);
            Assert.Equal(40, outcome.Score);
        }

        [Fact]
        public void A_state_pointing_at_an_account_the_policy_does_not_name_allows_nothing()
        {
            // After a policy change: LAPS last rotated the old manual account, the policy now names WLapsAdmin.
            var outcome = Run(Automatic(stateRid: 1001),
                new[] { Account("LAPSAdmin", 1001), Account("WLapsAdmin", 1004) },
                new List<LocalGroupMember> { Member("LAPSAdmin", 1001), Member("WLapsAdmin", 1004) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.NameMismatch, outcome.Resolution.Outcome);
            Assert.Equal("LAPSAdmin", outcome.Resolution.Account);
            Assert.Equal(new[] { "LAPSAdmin", "WLapsAdmin" }, outcome.Accounts.UnexpectedAdminMembers);
        }

        [Fact]
        public void A_state_rid_no_local_account_carries_allows_nothing()
        {
            var outcome = Run(Automatic(stateRid: 1009),
                new[] { Account("WLapsAdmin", 1002) }, new List<LocalGroupMember> { Member("WLapsAdmin", 1002) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Absent, outcome.Resolution.Outcome);
            Assert.Equal(80, outcome.Score);
        }

        [Fact]
        public void With_the_wmi_inventory_failed_the_LAPS_account_is_not_allowed()
        {
            // The SAM member list still reveals the account; without the inventory the RID cannot be resolved.
            var outcome = Run(Automatic(stateRid: 1002),
                new List<LocalAdminAnalyzer.LocalAccountInfo>(), new List<LocalGroupMember> { Member("WLapsAdmin", 1002) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Absent, outcome.Resolution.Outcome);
            Assert.Equal(new[] { "WLapsAdmin" }, outcome.Accounts.UnexpectedAdminMembers);
        }

        // -------------------------------------------------------------- manual mode

        [Fact]
        public void The_manual_LAPS_account_is_allowed_once_LAPS_rotated_it()
        {
            var accounts = new[] { Account("Administrator", 500, disabled: true), Account("LAPSAdmin", 1001) };
            var admins = new List<LocalGroupMember> { Member("Administrator", 500), Member("LAPSAdmin", 1001) };

            var rotated = Run(Manual(stateRid: 1001, name: "LAPSAdmin"), accounts, admins);
            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Matched, rotated.Resolution.Outcome);
            Assert.Equal(0, rotated.Score);

            // Created by a script after the policy arrived: not rotated yet, reported like today.
            var notYet = Run(Manual(stateRid: 500, name: "LAPSAdmin"), accounts, admins);
            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Builtin, notYet.Resolution.Outcome);
            Assert.Equal(80, notYet.Score);
        }

        // -------------------------------------------------------------- randomized names

        [Fact]
        public void A_randomized_name_is_allowed_for_the_rid_LAPS_rotated_only()
        {
            var outcome = Run(Automatic(stateRid: 1002, randomized: true),
                new[] { Account("WLapsAdmin482913", 1002), Account("WLapsAdmin000000", 1003) },
                new List<LocalGroupMember> { Member("WLapsAdmin482913", 1002), Member("WLapsAdmin000000", 1003) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Matched, outcome.Resolution.Outcome);
            Assert.Equal(new[] { "WLapsAdmin000000" }, outcome.Accounts.UnexpectedAdminMembers);
        }

        [Theory]
        [InlineData("WLapsAdmin123456", true)]
        [InlineData("wlapsadmin123456", true)]
        [InlineData("WLapsAdmin12345", false)]
        [InlineData("WLapsAdmin1234567", false)]
        [InlineData("WLapsAdmin12345a", false)]
        [InlineData("WLapsAdmin١٢٣٤٥٦", false)]   // Arabic-Indic digits are no ASCII digits
        [InlineData("WLapsAdmin", false)]
        public void A_randomized_name_is_the_prefix_and_exactly_six_ascii_digits(string accountName, bool expected)
            => Assert.Equal(expected, LocalAdminAnalyzer.MatchesLapsName(accountName, "WLapsAdmin", randomized: true));

        [Theory]
        [InlineData("WLapsAdmin", true)]
        [InlineData("WLAPSADMIN", true)]
        [InlineData("WLapsAdmin1", false)]
        [InlineData("WLapsAdm", false)]
        public void An_exact_name_matches_without_case(string accountName, bool expected)
            => Assert.Equal(expected, LocalAdminAnalyzer.MatchesLapsName(accountName, "WLapsAdmin", randomized: false));

        // -------------------------------------------------------------- the rules that stay

        [Fact]
        public void A_tenant_restriction_still_wins_over_the_LAPS_account()
        {
            var outcome = Run(Automatic(stateRid: 1002),
                new[] { Account("WLapsAdmin", 1002) }, new List<LocalGroupMember> { Member("WLapsAdmin", 1002) },
                "WLaps*|user");

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Matched, outcome.Resolution.Outcome);
            Assert.Equal(new[] { "WLapsAdmin" }, outcome.Accounts.RoleViolations);
            Assert.Equal(80, outcome.Score);
        }

        [Fact]
        public void A_policy_naming_a_restricted_builtin_name_does_not_lift_the_restriction()
        {
            var outcome = Run(Manual(stateRid: 1006, name: "WsiAccount"),
                new[] { Account("WsiAccount", 1006) }, new List<LocalGroupMember> { Member("WsiAccount", 1006) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Matched, outcome.Resolution.Outcome);
            Assert.Equal(new[] { "WsiAccount" }, outcome.Accounts.RoleViolations);
        }

        [Fact]
        public void A_state_naming_a_builtin_rid_adds_no_entry_the_rid_decides()
        {
            var outcome = Run(Manual(stateRid: 501, name: "Gast"),
                new[] { Account("Gast", 501) }, new List<LocalGroupMember> { Member("Gast", 501) });

            Assert.Equal(LocalAdminAnalyzer.LapsOutcome.Builtin, outcome.Resolution.Outcome);
            Assert.Null(outcome.Resolution.Entry);
            Assert.Equal(new[] { "Gast" }, outcome.Accounts.RoleViolations);
        }

        // -------------------------------------------------------------- the bound entry

        [Fact]
        public void The_LAPS_entry_matches_its_sid_and_its_literal_folder_only()
        {
            var entry = new LocalAdminAnalyzer.AllowedEntry("WLaps*", LocalAccountRole.Any, profileFolderOnly: false, accountSid: Sid(1002));

            Assert.True(entry.MatchesAccount("renamed", Sid(1002)));
            Assert.False(entry.MatchesAccount("WLapsAdmin", Sid(1003)));
            Assert.False(entry.MatchesAccount("WLapsAdmin", null!));
            Assert.True(entry.MatchesFolder("wlaps*"));
            Assert.False(entry.MatchesFolder("WLapsAdmin"));   // never a glob
        }

        [Fact]
        public void The_effective_list_is_always_new_and_carries_every_allowance()
        {
            var staticEntries = LocalAdminAnalyzer.BuildAllowedEntries(new[] { "adm-*" }, new List<string>());
            var count = staticEntries.Count;
            var laps = new LocalAdminAnalyzer.AllowedEntry("WLapsAdmin", LocalAccountRole.Any, profileFolderOnly: false, accountSid: Sid(1002));

            var effective = LocalAdminAnalyzer.BuildEffectiveEntries(staticEntries, new[] { "jane.doe", "Public" }, laps);
            var empty = LocalAdminAnalyzer.BuildEffectiveEntries(staticEntries, new List<string>(), null!);

            Assert.Equal(count, staticEntries.Count);
            Assert.NotSame(staticEntries, empty);
            Assert.Equal(count, empty.Count);
            Assert.Contains(laps, effective);
            Assert.Contains(effective, e => e.Pattern == "jane.doe" && e.ProfileFolderOnly);
            Assert.Single(effective, e => e.Pattern == "Public");   // already a built-in folder entry
            Assert.Contains(effective, e => e.MatchesFolder("WLapsAdmin"));
        }

        // -------------------------------------------------------------- payload

        [Fact]
        public void The_payload_names_the_managed_account_only_when_matched()
        {
            var policy = Automatic(stateRid: 1002);
            policy.StateManagedRid = 1002;
            policy.StatePolicySource = 1;

            var matched = LocalAdminAnalyzer.BuildLapsPayload(policy,
                LocalAdminAnalyzer.ResolveLapsManagedAccount(policy, new[] { Account("WLapsAdmin", 1002) }));
            var mismatch = LocalAdminAnalyzer.BuildLapsPayload(policy,
                LocalAdminAnalyzer.ResolveLapsManagedAccount(policy, new[] { Account("LAPSAdmin", 1002) }));

            Assert.Equal(
                new[] { "root", "mode", "backup_directory", "account_name", "randomized", "os_build", "state_rid",
                        "state_managed_rid", "state_policy_source", "problem", "outcome", "managed_account" },
                matched.Keys);
            Assert.Equal("csp", matched["root"]);
            Assert.Equal("automatic", matched["mode"]);
            Assert.Equal(1002, matched["state_rid"]);
            Assert.Equal("matched", matched["outcome"]);
            Assert.Equal("WLapsAdmin", matched["managed_account"]);
            Assert.Equal("name_mismatch", mismatch["outcome"]);
            Assert.Null(mismatch["managed_account"]);
        }

        [Fact]
        public void A_failed_check_still_yields_a_payload()
        {
            var payload = LocalAdminAnalyzer.BuildLapsPayload(null!,
                new LocalAdminAnalyzer.LapsResolution(LocalAdminAnalyzer.LapsOutcome.Error, problem: "UnauthorizedAccessException"));

            Assert.Equal("error", payload["outcome"]);
            Assert.Equal("UnauthorizedAccessException", payload["problem"]);
            Assert.Null(payload["mode"]);
            Assert.Null(payload["managed_account"]);
        }
    }
}
