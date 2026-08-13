using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.State
{
    /// <summary>
    /// Drift tripwire for the 4-site sync required when adding a field to
    /// <see cref="DecisionState"/>: ctor parameter + <see cref="DecisionStateBuilder"/>
    /// copy-ctor line + builder property + <c>Build()</c> argument. A forgotten copy-ctor
    /// line silently resets the fact on every reducer step; a forgotten <c>Build()</c>
    /// argument resets it on the first copy-with-changes. These tests are fully
    /// reflection-driven so a new field fails them until all four sites are wired
    /// (and until a value factory for any brand-new property type is added below).
    /// </summary>
    public sealed class DecisionStateBuilderCompletenessTests
    {
        private static readonly DateTime BaseUtc = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static PropertyInfo[] StateProperties() =>
            typeof(DecisionState)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();

        private static PropertyInfo[] BuilderProperties() =>
            typeof(DecisionStateBuilder)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod != null && p.SetMethod.IsPublic)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();

        /// <summary>
        /// Produce a distinct, non-default value for a builder property. <paramref name="seed"/>
        /// is folded into every value so no two properties of the same type ever share an
        /// instance — a swapped <c>Build()</c> argument (e.g. espFinalExitUtc passed into
        /// desktopArrivedUtc) is therefore detected, not masked. A property type without a
        /// factory entry fails loudly: extend the factory when a new field type appears.
        /// </summary>
        private static object CreateDistinctValue(Type type, int seed)
        {
            long ordinal = 100 + seed;
            if (type == typeof(string)) return "test-value-" + seed;
            if (type == typeof(int)) return 7 + seed;
            if (type == typeof(long)) return 7L + seed;
            if (type == typeof(DateTime?)) return BaseUtc.AddMinutes(seed);
            if (type == typeof(SessionStage)) return SessionStage.AwaitingHello;
            if (type == typeof(SessionOutcome?)) return SessionOutcome.EnrollmentComplete;
            if (type == typeof(SignalFact<DateTime>)) return new SignalFact<DateTime>(BaseUtc.AddMinutes(seed), ordinal);
            if (type == typeof(SignalFact<string>)) return new SignalFact<string>("test-value-" + seed, ordinal);
            if (type == typeof(SignalFact<bool>)) return new SignalFact<bool>(true, ordinal);
            if (type == typeof(SignalFact<EnrollmentPhase>)) return new SignalFact<EnrollmentPhase>(EnrollmentPhase.AccountSetup, ordinal);
            if (type == typeof(List<ActiveDeadline>))
            {
                return new List<ActiveDeadline>
                {
                    new ActiveDeadline(
                        name: "deadline-" + seed,
                        dueAtUtc: BaseUtc.AddMinutes(seed),
                        firesSignalKind: DecisionSignalKind.DeadlineFired,
                        firesPayload: new Dictionary<string, string> { ["k"] = "v" + seed }),
                };
            }
            // Immutable fact aggregates — built through their public APIs so the instances are
            // reference-distinct from their Empty defaults AND fully serializable (a dropped
            // copy-ctor line or Build() argument falls back to Empty → reference inequality).
            if (type == typeof(AppInstallFacts)) return AppInstallFacts.Empty.WithCompleted("Installed").WithFailed("app-" + seed);
            if (type == typeof(EnrollmentScenarioProfile))
            {
                return EnrollmentScenarioProfile.Empty.With(
                    mode: EnrollmentMode.WhiteGlove,
                    confidence: ProfileConfidence.High,
                    evidenceOrdinal: ordinal,
                    reason: "test-value-" + seed);
            }
            if (type == typeof(EnrollmentScenarioObservations)) return EnrollmentScenarioObservations.Empty.WithSkipUserEsp(true, ordinal);
            if (type == typeof(ClassifierOutcomes))
            {
                return ClassifierOutcomes.Empty.WithWhiteGloveSealing(new Hypothesis(
                    level: HypothesisLevel.Strong,
                    reason: "test-value-" + seed,
                    score: 50,
                    lastUpdatedUtc: BaseUtc.AddMinutes(seed),
                    lastClassifierVerdictId: "verdict-" + seed));
            }
            if (type == typeof(RealmJoinFacts)) return RealmJoinFacts.Empty.WithDetected(BaseUtc.AddMinutes(seed), ordinal);

            throw new Xunit.Sdk.XunitException(
                $"No value factory for builder property type {type}. A new DecisionState field " +
                "introduced a new type — add a distinct-value factory entry so the completeness " +
                "tests can populate it.");
        }

        /// <summary>Populate every settable builder property with a distinct non-default value.</summary>
        private static Dictionary<string, object> PopulateBuilder(DecisionStateBuilder builder)
        {
            var assigned = new Dictionary<string, object>(StringComparer.Ordinal);
            int seed = 0;
            foreach (var prop in BuilderProperties())
            {
                var value = CreateDistinctValue(prop.PropertyType, seed++);
                prop.SetValue(builder, value);
                assigned[prop.Name] = value;
            }
            return assigned;
        }

        private static void AssertMatches(string propertyName, object expected, object? actual)
        {
            // Deadlines: Build() copies the List into an array and the copy-ctor re-wraps it —
            // the container is a new instance by design, but the elements must be the SAME refs.
            if (expected is List<ActiveDeadline> expectedDeadlines)
            {
                var actualDeadlines = Assert.IsAssignableFrom<IEnumerable<ActiveDeadline>>(actual).ToList();
                Assert.Equal(expectedDeadlines.Count, actualDeadlines.Count);
                for (int i = 0; i < expectedDeadlines.Count; i++)
                {
                    Assert.Same(expectedDeadlines[i], actualDeadlines[i]);
                }
                return;
            }

            // Value types (enums, int, long, DateTime?) and strings compare by value; every
            // reference-typed fact must be the identical instance (a re-created equal-looking
            // value would still hide a dropped pass-through).
            if (expected is string || expected.GetType().IsValueType)
            {
                Assert.True(
                    expected.Equals(actual),
                    $"{propertyName}: expected {expected}, got {actual ?? "(null)"}.");
            }
            else
            {
                Assert.Same(expected, actual);
            }
        }

        // ============================================================= (a) name-set parity

        [Fact]
        public void Builder_property_set_matches_DecisionState_property_set()
        {
            var stateNames = StateProperties().Select(p => p.Name).ToArray();
            var builderNames = BuilderProperties().Select(p => p.Name).ToArray();

            var missingOnBuilder = stateNames.Except(builderNames, StringComparer.Ordinal).ToArray();
            var missingOnState = builderNames.Except(stateNames, StringComparer.Ordinal).ToArray();

            Assert.True(
                missingOnBuilder.Length == 0 && missingOnState.Length == 0,
                "DecisionState and DecisionStateBuilder property sets drifted apart. " +
                $"State-only: [{string.Join(", ", missingOnBuilder)}]; " +
                $"Builder-only: [{string.Join(", ", missingOnState)}]. " +
                "Adding a DecisionState field requires: ctor parameter, builder copy-ctor line, " +
                "builder property, and Build() argument.");
        }

        // ============================================================= (b) Build() completeness

        [Fact]
        public void Build_carries_every_builder_property_onto_the_state()
        {
            var builder = DecisionState.CreateInitial("seed-session", "seed-tenant").ToBuilder();
            var assigned = PopulateBuilder(builder);

            var state = builder.Build();

            foreach (var stateProp in StateProperties())
            {
                Assert.True(
                    assigned.TryGetValue(stateProp.Name, out var expected),
                    $"DecisionState.{stateProp.Name} has no builder counterpart — see the name-set parity test.");
                AssertMatches($"DecisionState.{stateProp.Name}", expected!, stateProp.GetValue(state));
            }
        }

        // ============================================================= (c) copy-ctor completeness

        [Fact]
        public void ToBuilder_copies_every_property_back_from_the_state()
        {
            var builder = DecisionState.CreateInitial("seed-session", "seed-tenant").ToBuilder();
            var assigned = PopulateBuilder(builder);
            var state = builder.Build();

            var roundtrippedBuilder = state.ToBuilder();

            foreach (var builderProp in BuilderProperties())
            {
                var expected = assigned[builderProp.Name];
                AssertMatches($"DecisionStateBuilder.{builderProp.Name}", expected, builderProp.GetValue(roundtrippedBuilder));
            }
        }

        // ============================================================= (d) serializer roundtrip

        [Fact]
        public void StateSerializer_roundtrip_of_fully_populated_state_is_lossless()
        {
            // Catches Newtonsoft ctor-binding mismatches: deserialization binds JSON properties
            // onto the single public ctor by (case-insensitive) parameter name, so a renamed
            // ctor parameter silently drops the field back to its default. Serializing the
            // deserialized state must therefore reproduce the exact same JSON.
            var builder = DecisionState.CreateInitial("seed-session", "seed-tenant").ToBuilder();
            PopulateBuilder(builder);
            var state = builder.Build();

            var json = StateSerializer.Serialize(state);
            var roundtrippedJson = StateSerializer.Serialize(StateSerializer.Deserialize(json));

            Assert.Equal(json, roundtrippedJson);
        }
    }
}
