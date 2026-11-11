#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Security
{
    /// <summary>
    /// The tally behind <c>console_script_activity</c>: what it counts, what it lists and caps, and
    /// that taking the content empties it.
    /// </summary>
    public sealed class ConsoleScriptActivityTallyTests
    {
        private static readonly DateTime At = new DateTime(2026, 11, 11, 14, 41, 35, DateTimeKind.Utc);
        private const string Cmd = "cmd.exe";
        private const string OobeAccount = "S-1-5-21-1-2-3-1000";

        private static string? NoName(int pid) => null;

        [Fact]
        public void An_empty_tally_has_nothing_to_take()
        {
            Assert.Null(new ConsoleScriptActivityTally().Take());
        }

        [Fact]
        public void Starts_are_counted_with_time_span_sessions_owners_and_parents()
        {
            var sut = new ConsoleScriptActivityTally();

            sut.Add(Cmd, 101, 50, sessionId: 2, OobeAccount, At, _ => "powershell.exe");
            sut.Add(Cmd, 102, 50, sessionId: 1, OobeAccount, At.AddSeconds(1), _ => "powershell.exe");
            sut.Add(Cmd, 103, 60, sessionId: 1, "S-1-5-18", At.AddSeconds(9), _ => "setup.exe");

            var a = sut.Take()!;
            Assert.Equal(Cmd, a.ProcessName);
            Assert.Equal(3, a.Count);
            Assert.Equal(At, a.FirstSeenUtc);
            Assert.Equal(At.AddSeconds(9), a.LastSeenUtc);
            Assert.Equal(new[] { 1, 2 }, a.WindowsSessions);
            Assert.Equal(new[] { OobeAccount, "S-1-5-18" }, a.Owners);
            Assert.Equal(2, a.OwnersTotal);
            Assert.Equal(2, a.ParentsTotal);
            Assert.Equal(new[] { 50, 60 }, a.Parents.Select(p => p.ProcessId));
            Assert.Equal(new[] { 2, 1 }, a.Parents.Select(p => p.Children));
            Assert.Equal(new[] { "powershell.exe", "setup.exe" }, a.Parents.Select(p => p.Name));
        }

        [Fact]
        public void Taking_empties_the_tally_and_the_next_start_opens_it_again()
        {
            var sut = new ConsoleScriptActivityTally();

            Assert.True(sut.Add(Cmd, 101, 50, 1, OobeAccount, At, NoName));
            Assert.False(sut.Add(Cmd, 102, 50, 1, OobeAccount, At, NoName));
            Assert.Equal(2, sut.Take()!.Count);

            Assert.Null(sut.Take());
            Assert.True(sut.Add(Cmd, 103, 50, 1, OobeAccount, At, NoName));
            Assert.Equal(1, sut.Take()!.Count);
        }

        [Fact]
        public void A_parent_is_looked_up_once_even_when_the_lookup_finds_nothing()
        {
            var sut = new ConsoleScriptActivityTally();
            var asked = new List<int>();
            string? Lookup(int pid) { asked.Add(pid); return null; }

            for (var i = 0; i < 5; i++) sut.Add(Cmd, 100 + i, 50, 1, OobeAccount, At, Lookup);

            Assert.Equal(new[] { 50 }, asked);
            var parent = Assert.Single(sut.Take()!.Parents);
            Assert.Null(parent.Name);
            Assert.Equal(5, parent.Children);
        }

        [Fact]
        public void A_parent_that_was_counted_itself_is_named_without_a_lookup()
        {
            // A script shell that is already gone, and the children it started.
            var sut = new ConsoleScriptActivityTally();
            var asked = new List<int>();
            string? Lookup(int pid) { asked.Add(pid); return "AgentExecutor.exe"; }

            sut.Add(Cmd, 100, 50, 2, OobeAccount, At, Lookup);
            for (var i = 1; i <= 6; i++) sut.Add(Cmd, 100 + i, 100, 2, OobeAccount, At, Lookup);

            Assert.Equal(new[] { 50 }, asked);
            var a = sut.Take()!;
            Assert.Equal(7, a.Count);
            Assert.Equal(new[] { 100, 50 }, a.Parents.Select(p => p.ProcessId));
            Assert.Equal(new[] { Cmd, "AgentExecutor.exe" }, a.Parents.Select(p => p.Name));
        }

        [Fact]
        public void Lookups_are_limited_per_window_and_allowed_again_after_taking()
        {
            var sut = new ConsoleScriptActivityTally();
            var lookups = 0;
            string? Lookup(int pid) { lookups++; return "p" + pid + ".exe"; }

            for (var i = 0; i < ConsoleScriptActivityTally.MaxParentLookups + 3; i++)
                sut.Add(Cmd, 1000 + i, 10 + i, 1, OobeAccount, At, Lookup);

            Assert.Equal(ConsoleScriptActivityTally.MaxParentLookups, lookups);
            sut.Take();

            sut.Add(Cmd, 2000, 99, 1, OobeAccount, At, Lookup);
            Assert.Equal(ConsoleScriptActivityTally.MaxParentLookups + 1, lookups);
        }

        [Fact]
        public void Lists_are_capped_and_the_totals_tell_how_much_there_was()
        {
            var sut = new ConsoleScriptActivityTally();
            const int parents = ConsoleScriptActivityTally.MaxParentsListed + 3;
            const int owners = ConsoleScriptActivityTally.MaxOwnersListed + 2;

            for (var p = 0; p < parents; p++)
            {
                // Parent p gets p+1 children, so the busiest parents are the last ones added.
                for (var c = 0; c <= p; c++)
                    sut.Add(Cmd, 10_000 + p * 100 + c, 500 + p, 1, "S-1-5-21-9-9-9-" + (p % owners), At, NoName);
            }

            var a = sut.Take()!;
            Assert.Equal(parents, a.ParentsTotal);
            Assert.Equal(ConsoleScriptActivityTally.MaxParentsListed, a.Parents.Count);
            Assert.Equal(500 + parents - 1, a.Parents[0].ProcessId);
            Assert.Equal(parents, a.Parents[0].Children);
            Assert.Equal(owners, a.OwnersTotal);
            Assert.Equal(ConsoleScriptActivityTally.MaxOwnersListed, a.Owners.Count);
        }

        [Fact]
        public void A_start_without_a_parent_id_or_owner_still_counts()
        {
            var sut = new ConsoleScriptActivityTally();

            sut.Add(Cmd, 101, 0, 1, null, At, _ => throw new InvalidOperationException("no parent to look up"));

            var a = sut.Take()!;
            Assert.Equal(1, a.Count);
            Assert.Empty(a.Parents);
            Assert.Equal(0, a.ParentsTotal);
            Assert.Empty(a.Owners);
        }
    }
}
