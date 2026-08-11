using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Pins the display mirror in rules/guardrails.json (embedded into this assembly)
    /// to the agent's hard-block CONSTANTS in <see cref="GatherRuleGuards"/>.
    /// Enforcement is code-only by design (a JSON parse error must never lift a hard
    /// block); the JSON mirror exists so the portal and the MCP validator can
    /// pre-flight the same blocks. This test keeps the two from drifting: an addition
    /// on either side fails here until the other side follows.
    /// </summary>
    public sealed class GuardrailsParityTests
    {
        private static JObject LoadEmbeddedGuardrails()
        {
            var assembly = typeof(GatherRuleGuards).Assembly;
            using var stream = assembly.GetManifestResourceStream(
                "AutopilotMonitor.Agent.V2.Core.Resources.guardrails.json");
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            return JObject.Parse(reader.ReadToEnd());
        }

        [Fact]
        public void Blocked_file_prefixes_match_code_constants()
        {
            var json = LoadEmbeddedGuardrails()["blockedFilePrefixes"]!.ToObject<string[]>()!;
            var code = new[] { GatherRuleGuards.BlockedUsersPrefix }
                .Concat(GatherRuleGuards.AdditionalHardBlockedPathPrefixes);

            Assert.Equal(
                code.Select(p => p.ToLowerInvariant()).OrderBy(p => p),
                json.Select(p => Path.GetFullPath(p).ToLowerInvariant()).OrderBy(p => p));
        }

        [Fact]
        public void Blocked_command_patterns_match_code_constants()
        {
            var json = LoadEmbeddedGuardrails()["blockedCommandPatterns"]!.ToObject<string[]>()!;

            Assert.Equal(
                GatherRuleGuards.HardBlockedCommandPatterns.OrderBy(p => p),
                json.OrderBy(p => p));
        }

        [Fact]
        public void Max_command_length_matches_code_constant()
        {
            Assert.Equal(
                GatherRuleGuards.MaxCommandLength,
                LoadEmbeddedGuardrails()["maxCommandLength"]!.Value<int>());
        }

        [Fact]
        public void Blocked_event_log_channels_match_code_constants()
        {
            var json = LoadEmbeddedGuardrails()["blockedEventLogChannels"]!.ToObject<string[]>()!;

            Assert.Equal(
                GatherRuleGuards.HardBlockedEventLogChannels.OrderBy(c => c),
                json.OrderBy(c => c));
        }
    }
}
