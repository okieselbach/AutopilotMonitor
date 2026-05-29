using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests
{
    /// <summary>
    /// Drift guard for the single-source event-type catalog (consolidation 2026-05-29).
    /// Every event type emitted by the V2 agent MUST be defined as a const in
    /// <see cref="Constants.EventTypes"/> — that class is the ONE canonical source the MCP
    /// catalog/search and the backend derive from. This test scans the V2 source for the known
    /// emit shapes and fails if any uses a raw string literal not present in Constants.EventTypes.
    /// <para>
    /// Scope: V2 only (Agent.V2.Core, Agent.V2, DecisionCore). V1 is decommissioning and is not
    /// guarded. Known boundary: gather-rule <c>OutputEventType</c> values are data-driven (rule
    /// JSON), not code literals, so they are out of scope by design.
    /// </para>
    /// </summary>
    public class EventTypeConstantGuardTests
    {
        // Emit shapes that carry an event-type string. Capture group 1 = the literal.
        private static readonly Regex[] EmitPatterns =
        {
            new Regex("\\bEventType\\s*=\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("\\beventType\\s*=\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("\\beventType\\s*==\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("\\.Emit\\(\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("EmitDeviceInfoEvent\\(\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
            new Regex("\\[\"eventType\"\\]\\s*=\\s*\"([a-z][a-z0-9_]*)\"", RegexOptions.Compiled),
        };

        // The reducer/param key name itself ("eventType") and the like are not event types.
        private static readonly HashSet<string> NotEventTypes = new(StringComparer.Ordinal)
        {
            "eventType",
        };

        private static readonly string[] V2SourceDirs =
        {
            Path.Combine("src", "Agent", "AutopilotMonitor.Agent.V2.Core"),
            Path.Combine("src", "Agent", "AutopilotMonitor.Agent.V2"),
            Path.Combine("src", "Shared", "AutopilotMonitor.DecisionCore"),
        };

        [Fact]
        public void All_V2_emitted_event_types_are_defined_in_Constants()
        {
            var known = KnownEventTypes();
            var offenders = new List<string>();

            foreach (var dir in V2SourceDirs)
            {
                var full = Path.Combine(RepoRoot(), dir);
                Assert.True(Directory.Exists(full), $"V2 source dir not found: {full}");

                foreach (var file in EnumerateSourceFiles(full))
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        var trimmed = line.TrimStart();

                        // Skip comments and const DEFINITIONS (Constants/DeadlineNames key names etc.).
                        if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                            continue;
                        if (line.Contains("const ")) continue;

                        foreach (var pattern in EmitPatterns)
                        {
                            foreach (Match m in pattern.Matches(line))
                            {
                                var literal = m.Groups[1].Value;
                                if (NotEventTypes.Contains(literal)) continue;
                                if (!known.Contains(literal))
                                {
                                    offenders.Add(
                                        $"{Path.GetFileName(file)}:{i + 1}  \"{literal}\"  ->  {line.Trim()}");
                                }
                            }
                        }
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "Found V2 event-type string literals NOT defined in Constants.EventTypes. " +
                "Add a const (exact value) and reference it at the emit site:\n  " +
                string.Join("\n  ", offenders));
        }

        /// <summary>All string-const values declared on <see cref="Constants.EventTypes"/>.</summary>
        private static HashSet<string> KnownEventTypes()
        {
            var values = typeof(Constants.EventTypes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(values.Count > 50, $"Expected many event-type consts, found {values.Count}.");
            return values;
        }

        private static IEnumerable<string> EnumerateSourceFiles(string root) =>
            Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                if (File.Exists(Path.Combine(dir, "AutopilotMonitor.sln"))) return dir;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null || parent == dir) break;
                dir = parent;
            }
            throw new DirectoryNotFoundException(
                "Could not locate repo root (AutopilotMonitor.sln) from " + AppContext.BaseDirectory);
        }
    }
}
