#nullable enable
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// LocalAdminAnalyzer.MatchesAllowedEntry — the allowed-list matching contract.
    /// Entries without wildcard characters match exactly (case-insensitive); entries
    /// containing * or ? are glob patterns with the same semantics as the hardware
    /// whitelist. The same matcher is applied to local accounts and profile folders.
    /// </summary>
    public sealed class LocalAdminAnalyzerTests
    {
        // -------------------------------------------------------------- literal entries

        [Theory]
        [InlineData("SupportAdmin", "SupportAdmin")]
        [InlineData("supportadmin", "SupportAdmin")]
        [InlineData("SUPPORTADMIN", "supportadmin")]
        public void Literal_entry_matches_exactly_case_insensitive(string name, string entry)
        {
            Assert.True(LocalAdminAnalyzer.MatchesAllowedEntry(name, entry));
        }

        [Theory]
        [InlineData("SupportAdmin2", "SupportAdmin")]
        [InlineData("SupportAdmin", "SupportAdmin2")]
        [InlineData("Support", "SupportAdmin")]
        public void Literal_entry_does_not_substring_match(string name, string entry)
        {
            Assert.False(LocalAdminAnalyzer.MatchesAllowedEntry(name, entry));
        }

        // -------------------------------------------------------------- glob patterns

        [Theory]
        [InlineData("adm-12345", "adm-*")]
        [InlineData("adm-", "adm-*")]
        [InlineData("ADM-9F3B2C", "adm-*")]
        [InlineData("lapsAdmin01", "lapsAdmin??")]
        [InlineData("svc-backup-01", "svc-*-01")]
        public void Wildcard_entry_matches_glob(string name, string entry)
        {
            Assert.True(LocalAdminAnalyzer.MatchesAllowedEntry(name, entry));
        }

        [Theory]
        [InlineData("administrator2", "adm-*")]   // no literal "adm-" prefix
        [InlineData("xadm-123", "adm-*")]          // pattern is anchored at start
        [InlineData("lapsAdmin1", "lapsAdmin??")]  // ? is exactly one char
        [InlineData("adm.123", "adm-*")]           // "-" is literal, not a regex class
        public void Wildcard_entry_does_not_overmatch(string name, string entry)
        {
            Assert.False(LocalAdminAnalyzer.MatchesAllowedEntry(name, entry));
        }

        [Fact]
        public void Regex_metacharacters_in_entry_are_treated_literally()
        {
            // "adm.123" as an entry has no wildcard chars → exact match only,
            // the dot must not act as a regex any-char.
            Assert.True(LocalAdminAnalyzer.MatchesAllowedEntry("adm.123", "adm.123"));
            Assert.False(LocalAdminAnalyzer.MatchesAllowedEntry("admX123", "adm.123"));

            // Mixed: wildcard present, remaining metacharacters stay literal.
            Assert.True(LocalAdminAnalyzer.MatchesAllowedEntry("adm.123", "adm.*"));
            Assert.False(LocalAdminAnalyzer.MatchesAllowedEntry("admX123", "adm.*"));
        }
    }
}
