#nullable enable
using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Persistence
{
    /// <summary>
    /// Tests for <see cref="DeathRattlePrelude.TryCapture"/> — the extracted gate +
    /// path-build + snapshot-read that lives in front of <c>orchestrator.Start()</c> in
    /// AgentRuntimeHost. These tests directly cover the three regress classes that the
    /// host wiring is fragile against:
    /// <list type="bullet">
    /// <item>TryReadRaw moved to AFTER Start — caught by the no-mutation contract test
    /// (file must be readable bit-identically by a fresh SnapshotPersistence after the
    /// prelude returns) and by the path-layout test (prelude reads the same hardcoded
    /// "snapshot.json" the orchestrator writes to).</item>
    /// <item>WhiteGlove Part-2 resume not skipped — caught by the WG-skip-table test.</item>
    /// <item><c>clean</c> / <c>first_run</c> exits firing — caught by the clean-exit-table test.</item>
    /// </list>
    /// </summary>
    public sealed class DeathRattlePreludeTests
    {
        private static AgentLogger NewLogger(TempDirectory tmp)
            => new AgentLogger(Path.Combine(tmp.Path, "logs"), AgentLogLevel.Debug);

        private static DecisionState BuildState(SessionStage stage = SessionStage.AwaitingHello, int stepIndex = 7)
        {
            var b = DecisionState.CreateInitial("S1", "T1").ToBuilder()
                .WithStage(stage)
                .WithStepIndex(stepIndex)
                .WithLastAppliedSignalOrdinal(stepIndex);
            b.DesktopArrivedUtc = new SignalFact<DateTime>(
                new DateTime(2026, 5, 1, 13, 45, 37, DateTimeKind.Utc),
                sourceSignalOrdinal: 28);
            return b.Build();
        }

        private static string SaveSnapshot(string dir, DecisionState? state = null)
        {
            var path = Path.Combine(dir, DeathRattlePrelude.SnapshotFileName);
            new SnapshotPersistence(path).Save(state ?? BuildState());
            return path;
        }

        // ============================================================================
        // Clean-exit table — none of the planned exit classifications triggers
        // ============================================================================

        [Theory]
        [InlineData("clean")]
        [InlineData("first_run")]
        [InlineData("CLEAN")]
        [InlineData("First_Run")]
        public void Returns_null_for_planned_exit_types_even_when_snapshot_present(string exitType)
        {
            using var tmp = new TempDirectory();
            SaveSnapshot(tmp.Path);

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: exitType,
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.Null(result);
        }

        [Fact]
        public void Returns_null_for_null_previousExitType_with_snapshot_present()
        {
            using var tmp = new TempDirectory();
            SaveSnapshot(tmp.Path);

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: null,
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.Null(result);
        }

        [Fact]
        public void Returns_null_for_unknown_exit_type_with_snapshot_present()
        {
            // Defensive: if Program.DetectPreviousExit ever produces an unrecognised
            // value, the prelude must default to "no death-rattle" rather than
            // misclassify it as unclean. Telemetry-noise-by-default is a worse failure
            // mode than missed-attestation-on-edge-case.
            using var tmp = new TempDirectory();
            SaveSnapshot(tmp.Path);

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: "totally_made_up_type",
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.Null(result);
        }

        // ============================================================================
        // Unclean-exit table — every classification on the allowlist triggers
        // ============================================================================

        [Theory]
        [InlineData("reboot_kill")]
        [InlineData("hard_kill")]
        [InlineData("exception_crash")]
        [InlineData("REBOOT_KILL")]      // case-insensitive
        [InlineData("Hard_Kill")]
        [InlineData("Exception_Crash")]
        public void Returns_state_for_unclean_exit_types_when_snapshot_present(string exitType)
        {
            using var tmp = new TempDirectory();
            SaveSnapshot(tmp.Path);

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: exitType,
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.NotNull(result);
            Assert.Equal(SessionStage.AwaitingHello, result!.Stage);
            Assert.Equal(7, result.StepIndex);
        }

        [Fact]
        public void IsUncleanExit_pins_the_allowlist_explicitly()
        {
            // Public gate is also exposed for callers that want to log the decision
            // independently. Kept covered so a future "let me also include X" change
            // forces the test to be updated alongside the policy.
            Assert.True(DeathRattlePrelude.IsUncleanExit("reboot_kill"));
            Assert.True(DeathRattlePrelude.IsUncleanExit("hard_kill"));
            Assert.True(DeathRattlePrelude.IsUncleanExit("exception_crash"));

            Assert.False(DeathRattlePrelude.IsUncleanExit("clean"));
            Assert.False(DeathRattlePrelude.IsUncleanExit("first_run"));
            Assert.False(DeathRattlePrelude.IsUncleanExit(null));
            Assert.False(DeathRattlePrelude.IsUncleanExit(""));
            Assert.False(DeathRattlePrelude.IsUncleanExit("totally_made_up"));
        }

        // ============================================================================
        // WhiteGlove-Resume gate — wins even on unclean exit
        // ============================================================================

        [Theory]
        [InlineData("reboot_kill")]
        [InlineData("hard_kill")]
        [InlineData("exception_crash")]
        public void Returns_null_when_isWhiteGloveResume_even_on_unclean_exit(string exitType)
        {
            // Plan §B: WhiteGlove Part-2 resume's "death" is the planned Part-1 sealing
            // exit, which kicks the box through Sysprep + reboot. That's not an unclean
            // death — emitting a death-rattle there would be telemetry noise.
            using var tmp = new TempDirectory();
            SaveSnapshot(tmp.Path);

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: exitType,
                isWhiteGloveResume: true,
                logger: NewLogger(tmp));

            Assert.Null(result);
        }

        // ============================================================================
        // Snapshot-availability cases — missing / corrupt → null, never throws
        // ============================================================================

        [Fact]
        public void Returns_null_when_snapshot_file_missing()
        {
            using var tmp = new TempDirectory();
            // No SaveSnapshot call.

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: "reboot_kill",
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.Null(result);
        }

        [Fact]
        public void Returns_null_when_snapshot_corrupt_and_does_not_throw()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(
                Path.Combine(tmp.Path, DeathRattlePrelude.SnapshotFileName),
                "<not a valid envelope>");

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: "reboot_kill",
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.Null(result);
        }

        // ============================================================================
        // Path-layout contract — must match orchestrator's hardcoded path
        // ============================================================================

        [Fact]
        public void Reads_from_path_layout_matching_orchestrator()
        {
            // EnrollmentOrchestrator hardcodes Path.Combine(_stateDirectory,
            // "snapshot.json") at line 309. If anyone renames the file or moves it under
            // a subfolder on either side without updating the other, the death-rattle
            // would silently miss every snapshot. This test pins the exact filename
            // contract — a change of the literal "snapshot.json" requires the test to
            // be updated alongside both producers.
            using var tmp = new TempDirectory();
            var expectedPath = Path.Combine(tmp.Path, "snapshot.json");
            new SnapshotPersistence(expectedPath).Save(BuildState(stepIndex: 99));

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: "reboot_kill",
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.NotNull(result);
            Assert.Equal(99, result!.StepIndex);

            // Belt + suspenders: the file at the literal path is what we read.
            Assert.True(File.Exists(expectedPath));
        }

        // ============================================================================
        // Non-mutation contract — orchestrator.Start() must see the snapshot intact
        // ============================================================================

        [Fact]
        public void Does_NOT_modify_or_quarantine_the_snapshot_file_on_success()
        {
            // Critical for ordering: orchestrator.Start runs Snapshot.Load() right after
            // the prelude. If the prelude moves / overwrites / quarantines the file,
            // Start would see no snapshot and start from scratch — losing all recovery
            // state. Pin via byte-level equality + post-prelude Load() round-trip.
            using var tmp = new TempDirectory();
            var path = SaveSnapshot(tmp.Path);
            var bytesBefore = File.ReadAllBytes(path);

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: "reboot_kill",
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.NotNull(result);
            var bytesAfter = File.ReadAllBytes(path);
            Assert.Equal(bytesBefore, bytesAfter);

            // The file is also still loadable through the orchestrator-style instance API.
            var viaInstance = new SnapshotPersistence(path).Load();
            Assert.NotNull(viaInstance);
            Assert.Equal(result!.Stage, viaInstance!.Stage);
            Assert.Equal(result.StepIndex, viaInstance.StepIndex);

            // No quarantine directory has been created either.
            Assert.False(Directory.Exists(Path.Combine(tmp.Path, ".quarantine")));
        }

        [Fact]
        public void Does_NOT_modify_or_quarantine_the_snapshot_file_on_corruption()
        {
            // Even when TryReadRaw fails the prelude must NEVER touch the file —
            // orchestrator.Start owns the quarantine decision. If the prelude
            // quarantined corrupt snapshots, Start would see a missing file and
            // skip its own quarantine path → loss of forensic evidence.
            using var tmp = new TempDirectory();
            var path = Path.Combine(tmp.Path, DeathRattlePrelude.SnapshotFileName);
            File.WriteAllText(path, "<corrupt>");
            var bytesBefore = File.ReadAllBytes(path);

            var result = DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: "reboot_kill",
                isWhiteGloveResume: false,
                logger: NewLogger(tmp));

            Assert.Null(result);
            Assert.True(File.Exists(path));
            Assert.Equal(bytesBefore, File.ReadAllBytes(path));
            Assert.False(Directory.Exists(Path.Combine(tmp.Path, ".quarantine")));
        }

        // ============================================================================
        // Argument validation
        // ============================================================================

        [Fact]
        public void Throws_for_null_logger()
        {
            using var tmp = new TempDirectory();
            Assert.Throws<ArgumentNullException>(() => DeathRattlePrelude.TryCapture(
                stateDirectory: tmp.Path,
                previousExitType: "reboot_kill",
                isWhiteGloveResume: false,
                logger: null!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Throws_for_null_or_empty_stateDirectory(string? stateDirectory)
        {
            using var tmp = new TempDirectory();
            Assert.Throws<ArgumentException>(() => DeathRattlePrelude.TryCapture(
                stateDirectory: stateDirectory!,
                previousExitType: "reboot_kill",
                isWhiteGloveResume: false,
                logger: NewLogger(tmp)));
        }
    }
}
