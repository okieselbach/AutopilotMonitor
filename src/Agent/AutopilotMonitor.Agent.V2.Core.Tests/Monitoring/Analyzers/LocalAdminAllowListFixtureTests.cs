#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// <c>tests/fixtures/local-admin-allow-list/cases.json</c> run against the netstandard2.0 build of
    /// <see cref="LocalAdminAllowList"/> — the one the agent ships. The backend runs the same file against
    /// its net10.0 build, the portal against <c>allowedAccounts.ts</c>.
    /// </summary>
    public sealed class LocalAdminAllowListFixtureTests
    {
        private static IEnumerable<JToken> ParseCases()
        {
            var path = Path.Combine(RepoPaths.Fixtures("local-admin-allow-list"), "cases.json");
            return JObject.Parse(File.ReadAllText(path))["parse"]!;
        }

        public static IEnumerable<object[]> ParseEntries() => ParseCases().Select(c => new object[] { c.Value<string>("entry")! });

        [Theory]
        [MemberData(nameof(ParseEntries))]
        public void Agent_parses_the_entry_as_the_shared_case_says(string entry)
        {
            var c = ParseCases().First(x => x.Value<string>("entry") == entry);

            var parsed = LocalAdminAllowList.TryParseEntry(entry, out var pattern, out var role);

            if (c.Value<bool?>("ignored") == true)
            {
                Assert.False(parsed);
                return;
            }
            Assert.True(parsed);
            Assert.Equal(c.Value<string>("pattern"), pattern);
            Assert.Equal(c.Value<string>("role") == "user" ? LocalAccountRole.StandardUser : LocalAccountRole.Any, role);
        }
    }
}
