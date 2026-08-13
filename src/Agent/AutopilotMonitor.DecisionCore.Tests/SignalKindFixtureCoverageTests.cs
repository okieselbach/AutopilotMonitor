using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Enforces the fixture convention documented on <see cref="DecisionSignalKind"/>:
    /// "New kinds or version bumps require a replay fixture in
    /// <c>tests/fixtures/signal-kinds/{kind}-v{n}.json</c> — missing fixture = merge block."
    /// The comment alone enforced nothing; this test does. Kinds without a fixture today are
    /// carried in an explicit debt set that is only allowed to shrink.
    /// </summary>
    public sealed class SignalKindFixtureCoverageTests
    {
        // DEBT: backfill — new kinds must NOT be added here. Every entry predates this test;
        // when a fixture is added for one of these kinds, the test forces its removal.
        private static readonly HashSet<DecisionSignalKind> KnownMissingFixtures = new HashSet<DecisionSignalKind>
        {
            DecisionSignalKind.EspPhaseChanged,
            DecisionSignalKind.EspExiting,
            DecisionSignalKind.EspResumed,
            DecisionSignalKind.EspTerminalFailure,
            DecisionSignalKind.DesktopArrived,
            DecisionSignalKind.HelloResolved,
            DecisionSignalKind.ImeUserSessionCompleted,
            DecisionSignalKind.DeviceSetupProvisioningComplete,
            DecisionSignalKind.WhiteGloveShellCoreSuccess,
            DecisionSignalKind.WhiteGloveSealingPatternDetected,
            DecisionSignalKind.AadUserJoinedLate,
            DecisionSignalKind.DeviceInfoCollected,
            DecisionSignalKind.AutopilotProfileRead,
            DecisionSignalKind.EspConfigDetected,
            DecisionSignalKind.HelloPolicyDetected,
            DecisionSignalKind.ClassifierVerdictIssued,
            DecisionSignalKind.SessionStarted,
            DecisionSignalKind.SessionAborted,
        };

        /// <summary>
        /// Kinds that exist in the enum but are never posted as their own signal: they are
        /// dispatched over the <see cref="DecisionSignalKind.DeadlineFired"/> rail (the reducer's
        /// DeadlineFiredV1 switch routes on the <c>deadline</c> payload). Their fixture is named
        /// after the kind, but its declared <c>Kind</c> is <c>DeadlineFired</c> — e.g.
        /// <c>realmjoin-timeout-v1.json</c> carries <c>Payload.deadline = "realmjoin_timeout"</c>.
        /// </summary>
        private static readonly HashSet<DecisionSignalKind> DeadlineDispatchedKinds = new HashSet<DecisionSignalKind>
        {
            DecisionSignalKind.RealmJoinTimeout,
        };

        /// <summary>Locate <c>tests/fixtures/signal-kinds</c> by walking up to the repo root (AutopilotMonitor.sln).</summary>
        private static string FixtureRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                if (File.Exists(Path.Combine(dir, "AutopilotMonitor.sln")))
                {
                    return Path.Combine(dir, "tests", "fixtures", "signal-kinds");
                }
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null || parent == dir) break;
                dir = parent;
            }
            throw new DirectoryNotFoundException(
                "Could not locate repo root (AutopilotMonitor.sln) walking up from " + AppContext.BaseDirectory);
        }

        private static IReadOnlyList<string> FixtureFiles()
        {
            var root = FixtureRoot();
            Assert.True(Directory.Exists(root), $"Fixture directory not found: {root}");
            var files = Directory.GetFiles(root, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(files);
            return files;
        }

        /// <summary>Signal JSONL lines of one fixture ('#' comments and blank lines stripped).</summary>
        private static IReadOnlyList<string> SignalLines(string path)
        {
            var lines = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l[0] != '#')
                .ToArray();
            Assert.True(lines.Length > 0, $"Fixture contains no signal lines: {path}");
            return lines;
        }

        /// <summary>
        /// Strictly parse the raw <c>Kind</c> of a fixture line. Deliberately NOT via
        /// <see cref="SignalSerializer"/> alone: its unknown-kind fallback maps typos to
        /// <see cref="DecisionSignalKind.SessionStarted"/>, which would silently mis-count coverage.
        /// </summary>
        private static DecisionSignalKind StrictKind(string line, string path)
        {
            var raw = JObject.Parse(line).Value<string>("Kind");
            Assert.False(string.IsNullOrEmpty(raw), $"Fixture line has no Kind: {path}");
            Assert.True(
                Enum.TryParse<DecisionSignalKind>(raw, ignoreCase: false, out var kind)
                    && Enum.IsDefined(typeof(DecisionSignalKind), kind),
                $"Fixture {Path.GetFileName(path)} declares unknown Kind '{raw}'.");
            return kind;
        }

        /// <summary>Dash-insensitive lowercase — "realmjoin-detected-v1" and "RealmJoinDetected" both normalize consistently.</summary>
        private static string Normalize(string value) => value.Replace("-", string.Empty).ToLowerInvariant();

        /// <summary>
        /// Map a fixture file name to the kind it is named after: the longest enum member whose
        /// normalized name prefixes the normalized file name, followed by <c>v{n}[-variant]</c>
        /// (dash placement per the existing corpus, e.g. <c>realmjoin-detected-v1.json</c> /
        /// <c>admin-preemption-detected-v1-failed.json</c>).
        /// </summary>
        private static DecisionSignalKind KindFromFileName(string fileNameWithoutExtension)
        {
            var normalized = Normalize(fileNameWithoutExtension);
            var match = ((DecisionSignalKind[])Enum.GetValues(typeof(DecisionSignalKind)))
                .Where(k =>
                {
                    var prefix = Normalize(k.ToString());
                    return normalized.StartsWith(prefix, StringComparison.Ordinal)
                        && Regex.IsMatch(normalized.Substring(prefix.Length), "^v[0-9]+");
                })
                .OrderByDescending(k => k.ToString().Length)
                .Cast<DecisionSignalKind?>()
                .FirstOrDefault();
            Assert.True(
                match.HasValue,
                $"Fixture file name '{fileNameWithoutExtension}.json' does not follow " +
                "'{kind}-v{n}[-variant].json' for any DecisionSignalKind.");
            return match!.Value;
        }

        /// <summary>
        /// Load every fixture through the REAL deserialization path and return the set of kinds
        /// the corpus covers. Also enforces per-file consistency: the declared Kind must match
        /// the file name (or be the DeadlineFired dispatch shape for deadline-dispatched kinds).
        /// </summary>
        private static ISet<DecisionSignalKind> LoadAndVerifyFixtures()
        {
            var covered = new HashSet<DecisionSignalKind>();
            foreach (var file in FixtureFiles())
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var namedKind = KindFromFileName(fileName);

                foreach (var line in SignalLines(file))
                {
                    var declaredKind = StrictKind(line, file);

                    // Real deserialization path — the same call FixtureLoader/ReplayHarness use.
                    var signal = SignalSerializer.Deserialize(line);
                    Assert.Equal(declaredKind, signal.Kind);
                    Assert.True(signal.KindSchemaVersion >= 1, $"KindSchemaVersion must be >= 1 in {fileName}.");

                    if (declaredKind == namedKind)
                    {
                        covered.Add(declaredKind);
                    }
                    else
                    {
                        // Only the documented dispatch exception is allowed: a fixture named after
                        // a deadline-dispatched kind carries a DeadlineFired signal with the
                        // deadline name in its payload.
                        string? deadlineName = null;
                        signal.Payload?.TryGetValue("deadline", out deadlineName);
                        Assert.True(
                            DeadlineDispatchedKinds.Contains(namedKind)
                                && declaredKind == DecisionSignalKind.DeadlineFired
                                && !string.IsNullOrEmpty(deadlineName),
                            $"Fixture '{fileName}.json' is named after {namedKind} but declares Kind={declaredKind}. " +
                            "Either the file is misnamed, or a new deadline-dispatched kind must be " +
                            "registered in DeadlineDispatchedKinds (with a payload.deadline entry).");
                        covered.Add(namedKind);
                        covered.Add(declaredKind); // it IS also a genuine DeadlineFired fixture
                    }
                }
            }
            return covered;
        }

        [Fact]
        public void Every_fixture_deserializes_through_the_real_signal_path_and_matches_its_filename()
        {
            LoadAndVerifyFixtures();
        }

        [Fact]
        public void Every_kind_is_either_fixtured_or_registered_as_known_debt()
        {
            var fixtured = LoadAndVerifyFixtures();
            var allKinds = (DecisionSignalKind[])Enum.GetValues(typeof(DecisionSignalKind));

            var unfixturedAndUntracked = allKinds
                .Where(k => !fixtured.Contains(k) && !KnownMissingFixtures.Contains(k))
                .ToArray();
            Assert.True(
                unfixturedAndUntracked.Length == 0,
                "DecisionSignalKind values without a replay fixture in tests/fixtures/signal-kinds/: " +
                $"[{string.Join(", ", unfixturedAndUntracked)}]. The convention on DecisionSignalKind is " +
                "'new kind => fixture' — add a {kind}-v{n}.json fixture (do NOT extend KnownMissingFixtures).");

            // Debt only shrinks: once a fixture exists, the kind must leave the debt set.
            var fixturedButStillListedAsDebt = KnownMissingFixtures.Where(fixtured.Contains).ToArray();
            Assert.True(
                fixturedButStillListedAsDebt.Length == 0,
                "Kinds now have fixtures but are still listed in KnownMissingFixtures — remove them: " +
                $"[{string.Join(", ", fixturedButStillListedAsDebt)}].");
        }
    }
}
