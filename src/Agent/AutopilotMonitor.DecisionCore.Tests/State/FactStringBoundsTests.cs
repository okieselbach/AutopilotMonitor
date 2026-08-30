using System;
using System.Collections.Generic;
using System.Diagnostics;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.State
{
    /// <summary>
    /// Every payload-derived string that lands in <see cref="DecisionState"/> is bounded by
    /// <see cref="FactStringBounds"/> at the reducer boundary. The server-side semantic replay
    /// (<c>ReducerVerifier</c>) folds device-uploaded payloads through the same engine, so an
    /// unbounded string there is a per-request memory/CPU amplifier. These tests pin that the
    /// bound is applied BEFORE any dedupe comparison (so a forged multi-hundred-KB key neither
    /// costs a full-length compare nor lands in state) and that real-sized values pass through
    /// unchanged.
    /// </summary>
    public sealed class FactStringBoundsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);

        private static string Long(char fill, int length, char last)
            => new string(fill, length - 1) + last;

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"t-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);
        }

        // ---------------------------------------------------------------- helper

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("generic-vlc", "generic-vlc")]
        public void Bound_passes_null_and_short_values_through(string? input, string? expected)
        {
            Assert.Equal(expected, FactStringBounds.Bound(input));
        }

        [Fact]
        public void Bound_truncates_to_MaxLength()
        {
            var bounded = FactStringBounds.Bound(new string('x', FactStringBounds.MaxLength + 1))!;
            Assert.Equal(FactStringBounds.MaxLength, bounded.Length);
            Assert.Equal(FactStringBounds.MaxLength, RealmJoinPackageFact.MaxDisplayNameLength);
        }

        // ---------------------------------------------------------------- RealmJoinFacts

        [Fact]
        public void RealmJoin_package_keys_are_bounded_before_dedupe_so_forged_near_duplicates_collapse()
        {
            // Two ~300 KB ids that differ only in the final character — the exploit shape.
            // After bounding both become the same 256-char key: one row, second start ignored.
            var idA = Long('a', 300_000, '1');
            var idB = Long('a', 300_000, '2');

            var facts = RealmJoinFacts.Empty
                .WithPackageStarted(idA, "A", Long('v', 5000, '1'), RealmJoinPackageFact.ScopeMachine, T0)
                .WithPackageStarted(idB, "B", null, RealmJoinPackageFact.ScopeMachine, T0.AddSeconds(1));

            var row = Assert.Single(facts.Packages);
            Assert.Equal(FactStringBounds.MaxLength, row.PackageId.Length);
            Assert.Equal(FactStringBounds.MaxLength, row.Version!.Length);
            Assert.Equal("A", row.DisplayName);
        }

        [Fact]
        public void RealmJoin_package_completed_matches_started_row_through_the_bound()
        {
            var id = Long('b', 100_000, 'x');
            var facts = RealmJoinFacts.Empty
                .WithPackageStarted(id, "B", "1.0", RealmJoinPackageFact.ScopeUser, T0)
                .WithPackageCompleted(id, "B", "1.0", RealmJoinPackageFact.ScopeUser, T0.AddMinutes(1), success: true, lastExitCode: 0);

            var row = Assert.Single(facts.Packages);
            Assert.True(row.Success);
            Assert.NotNull(row.CompletedUtc);
            Assert.Equal(RealmJoinPackageFact.ScopeUser, row.Scope);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("MACHINE")]
        [InlineData("forged-scope-value")]
        public void RealmJoin_scope_is_normalised_to_the_two_known_constants(string? scope)
        {
            var facts = RealmJoinFacts.Empty.WithPackageStarted("generic-vlc", "VLC", null, scope!, T0);
            Assert.Equal(RealmJoinPackageFact.ScopeMachine, Assert.Single(facts.Packages).Scope);
            Assert.Equal(RealmJoinPackageFact.ScopeUser, RealmJoinPackageFact.NormalizeScope("user"));
        }

        [Fact]
        public void RealmJoin_real_sized_values_are_stored_verbatim()
        {
            var facts = RealmJoinFacts.Empty.WithPackageStarted("generic-vlc", "VLC media player", "3.0.21.0", RealmJoinPackageFact.ScopeMachine, T0);
            var row = Assert.Single(facts.Packages);
            Assert.Equal("generic-vlc", row.PackageId);
            Assert.Equal("3.0.21.0", row.Version);
            Assert.Equal("VLC media player", row.DisplayName);
        }

        [Fact]
        public void RealmJoin_replay_of_forged_package_stream_stays_cheap()
        {
            // 300 distinct ~200 KB ids against a full 200-row list. Unbounded, this was
            // ~300 × 200 × 200 KB byte compares (tens of seconds); bounded it is microseconds.
            // Generous ceiling — a regression is minutes, not milliseconds.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("bounds-sess", "bounds-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var sw = Stopwatch.StartNew();
            for (var i = 1; i <= 300; i++)
            {
                var id = new string('p', 200_000 - 8) + i.ToString("D8");
                state = engine.Reduce(state, MakeSignal(i, DecisionSignalKind.RealmJoinPackageStarted, T0.AddSeconds(i),
                    new Dictionary<string, string>
                    {
                        [DecisionEngine.RealmJoinPayloadKeys.PackageId] = id,
                        [DecisionEngine.RealmJoinPayloadKeys.Scope] = RealmJoinPackageFact.ScopeMachine,
                    })).NewState;
            }
            sw.Stop();

            // All 300 ids share their first 256 chars → they collapse to a single bounded row.
            var row = Assert.Single(state.RealmJoinFacts.Packages);
            Assert.Equal(FactStringBounds.MaxLength, row.PackageId.Length);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"replay took {sw.Elapsed}");
        }

        [Fact]
        public void RealmJoin_version_override_is_bounded()
        {
            var facts = RealmJoinFacts.Empty.WithVersionOverride(Long('v', 10_000, 'x'), Long('c', 10_000, 'y'), 7);
            Assert.Equal(FactStringBounds.MaxLength, facts.ProductVersion!.Value.Length);
            Assert.Equal(FactStringBounds.MaxLength, facts.ReleaseChannel!.Value.Length);
        }

        // ---------------------------------------------------------------- AppInstallFacts

        [Fact]
        public void AppInstall_failed_app_ids_are_bounded_before_dedupe()
        {
            var facts = AppInstallFacts.Empty
                .WithFailed(Long('a', 50_000, '1'))
                .WithFailed(Long('a', 50_000, '2'))
                .WithFailed("real-app-guid");

            Assert.Equal(3, facts.FailedCount);
            Assert.Equal(2, facts.FailedAppIds.Count);
            Assert.Equal(FactStringBounds.MaxLength, facts.FailedAppIds[0].Length);
            Assert.Equal("real-app-guid", facts.FailedAppIds[1]);
        }

        // ---------------------------------------------------------------- scalar facts

        [Fact]
        public void EspAdvisoryFailureCategory_is_bounded()
        {
            var builder = DecisionState.CreateInitial("s", "t", T0).ToBuilder()
                .WithEspAdvisoryFailureRecorded(T0, 1, Long('c', 10_000, 'x'));
            Assert.Equal(FactStringBounds.MaxLength, builder.EspAdvisoryFailureCategory!.Value.Length);
        }

        [Fact]
        public void HelloOutcome_from_payload_is_bounded_by_the_engine()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("hello-sess", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspExiting, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.HelloResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = Long('h', 100_000, 'x') })).NewState;

            Assert.NotNull(state.HelloOutcome);
            Assert.Equal(FactStringBounds.MaxLength, state.HelloOutcome!.Value.Length);
        }

        [Fact]
        public void ImeMatchedPatternId_from_payload_is_bounded_by_the_engine()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("ime-sess", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(15, DecisionSignalKind.EspExiting, T0.AddMinutes(16.5))).NewState;
            state = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(17),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(27),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = Long('i', 100_000, 'x') })).NewState;

            Assert.NotNull(state.ImeMatchedPatternId);
            Assert.Equal(FactStringBounds.MaxLength, state.ImeMatchedPatternId!.Value.Length);
        }
    }
}
