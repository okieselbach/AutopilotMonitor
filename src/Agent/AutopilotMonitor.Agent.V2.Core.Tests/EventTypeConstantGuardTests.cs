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

        // Multi-line callback shape (2026-09-04, D-072 gap): `OnTraceEvent?.Invoke(` at the end
        // of one line, the event-type literal alone on the NEXT line. The single-line patterns
        // above never saw `desktop_real_user_detected` / `desktop_excluded_user` this way.
        private static readonly Regex InvokeOpener = new Regex("\\.Invoke\\(\\s*$", RegexOptions.Compiled);
        private static readonly Regex BareLiteralLine = new Regex("^\\s*\"([a-z][a-z0-9_]*)\"\\s*,?\\s*$", RegexOptions.Compiled);

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
                    offenders.AddRange(FindOffenders(Path.GetFileName(file), File.ReadAllLines(file), known));
            }

            Assert.True(
                offenders.Count == 0,
                "Found V2 event-type string literals NOT defined in Constants.EventTypes. " +
                "Add a const (exact value) and reference it at the emit site:\n  " +
                string.Join("\n  ", offenders));
        }

        [Fact]
        public void Guard_detects_multiline_Invoke_literal()
        {
            // Mutation proof for the multi-line shape: a synthetic emit site with an unknown
            // literal on the line after `OnTraceEvent?.Invoke(` must be reported; the same
            // shape with a known type must not.
            var known = new HashSet<string>(StringComparer.Ordinal) { "desktop_arrived" };
            string[] bad =
            {
                "                            OnTraceEvent?.Invoke(",
                "                                \"desktop_not_a_real_type\",",
                "                                $\"Real user desktop detected\",",
            };
            string[] good =
            {
                "                            OnTraceEvent?.Invoke(",
                "                                \"desktop_arrived\",",
            };

            var offender = Assert.Single(FindOffenders("X.cs", bad, known));
            Assert.Contains("desktop_not_a_real_type", offender);
            Assert.Empty(FindOffenders("X.cs", good, known));
        }

        private static List<string> FindOffenders(string fileName, string[] lines, HashSet<string> known)
        {
            var offenders = new List<string>();
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
                        Check(m.Groups[1].Value, line, i);
                }

                if (i > 0 && InvokeOpener.IsMatch(lines[i - 1]))
                {
                    var bare = BareLiteralLine.Match(line);
                    if (bare.Success) Check(bare.Groups[1].Value, line, i);
                }
            }
            return offenders;

            void Check(string literal, string line, int index)
            {
                if (NotEventTypes.Contains(literal)) return;
                if (!known.Contains(literal))
                    offenders.Add($"{fileName}:{index + 1}  \"{literal}\"  ->  {line.Trim()}");
            }
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
