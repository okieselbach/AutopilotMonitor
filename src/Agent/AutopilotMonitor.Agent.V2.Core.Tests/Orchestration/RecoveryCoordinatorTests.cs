using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Direct unit tests for <see cref="RecoveryCoordinator.Recover"/> — the crash-recovery
    /// state machine extracted from <c>EnrollmentOrchestrator.Start</c> (ARCH-F5). These exercise
    /// the recovery branches in isolation (no ingress worker, drain loop, collectors, or scheduler),
    /// asserting directly on the returned <see cref="RecoveryResult"/> and the on-disk side effects.
    /// <para>
    /// The orchestrator-level <c>RecoveryPathTests</c> still cover the same branches end-to-end
    /// (replay reaches the live pipeline, deadlines re-arm, the Terminated event fires). This class
    /// pins the coordinator's own contract: which branch is taken, which flags it returns, and which
    /// segment files it moves — including branches the end-to-end tests under-assert
    /// (<c>a-total-loss</c>, the prior-run quarantine-marker flags).
    /// </para>
    /// </summary>
    public sealed class RecoveryCoordinatorTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
        private const string SessionId = "S1";
        private const string TenantId = "T1";

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public VirtualClock Clock { get; } = new VirtualClock(At);
            public AgentLogger Logger { get; }
            public string StateDir { get; }

            public Rig()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                StateDir = Path.Combine(Tmp.Path, "State");
                Directory.CreateDirectory(StateDir);
            }

            // The orchestrator runs EnsureDirectories() before Recover(); mirror that contract.
            public RecoveryResult Recover() =>
                RecoveryCoordinator.Recover(StateDir, SessionId, TenantId, Clock, Logger);

            public string StatePath(string name) => System.IO.Path.Combine(StateDir, name);

            public void Dispose() => Tmp.Dispose();
        }

        // ----------------------------------------------------------------- Fixtures

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind = DecisionSignalKind.AppInstallCompleted) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: At.AddSeconds(ordinal),
                sourceOrigin: "recovery-coordinator-tests",
                evidence: new Evidence(EvidenceKind.Synthetic, $"recovery:ord-{ordinal}", "test signal"));

        private static DecisionTransition MakeTransition(int stepIndex, long signalRef) =>
            new DecisionTransition(
                stepIndex: stepIndex,
                sessionTraceOrdinal: stepIndex,
                signalOrdinalRef: signalRef,
                occurredAtUtc: At.AddSeconds(stepIndex),
                trigger: "TestTrigger",
                fromStage: SessionStage.SessionStarted,
                toStage: SessionStage.SessionStarted,
                taken: true,
                deadEndReason: null,
                reducerVersion: "2.0.0.0");

        private void SeedSnapshot(Rig rig, DecisionState state) =>
            new SnapshotPersistence(rig.StatePath("snapshot.json"), () => rig.Clock.UtcNow).Save(state);

        private void SeedSignalLog(Rig rig, params DecisionSignal[] signals)
        {
            var writer = new SignalLogWriter(rig.StatePath("signal-log.jsonl"));
            foreach (var s in signals) writer.Append(s);
        }

        // ================================================================= Branch c1 — fresh start

        [Fact]
        public void Empty_state_directory_yields_fresh_initial_state_no_flags()
        {
            using var rig = new Rig();

            var result = rig.Recover();

            Assert.Equal(SessionStage.SessionStarted, result.InitialState.Stage);
            Assert.Equal(0, result.InitialState.StepIndex);
            Assert.Equal(SessionId, result.InitialState.SessionId);
            Assert.Equal(TenantId, result.InitialState.TenantId);

            Assert.False(result.IsWhiteGlovePart2);
            Assert.False(result.WasStartupQuarantine);
            Assert.False(result.PriorRunQuarantined);
            Assert.Null(result.PriorRunQuarantineReason);

            // All four writers are returned and ready for the wiring steps.
            Assert.NotNull(result.SignalLog);
            Assert.NotNull(result.Journal);
            Assert.NotNull(result.Snapshot);
            Assert.NotNull(result.EventSequence);
        }

        // ================================================================= Branch b — clean snapshot

        [Fact]
        public void Clean_snapshot_without_tail_is_loaded_as_seed()
        {
            using var rig = new Rig();
            var saved = DecisionState.CreateInitial(SessionId, TenantId)
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2)
                .Build();
            SeedSnapshot(rig, saved);

            var result = rig.Recover();

            Assert.False(result.WasStartupQuarantine);
            Assert.False(result.IsWhiteGlovePart2);
            Assert.Equal(SessionStage.EspDeviceSetup, result.InitialState.Stage);
            Assert.Equal(3, result.InitialState.StepIndex);
            Assert.Equal(2, result.InitialState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void Clean_snapshot_restamps_agent_boot_utc_to_current_run()
        {
            // Branch (b) re-stamps AgentBootUtc to "now" so rehydrated deadlines floor at the
            // current run start rather than the prior run's (premature-fire safety).
            using var rig = new Rig();
            var priorBoot = At.AddHours(-5);
            var saved = DecisionState.CreateInitial(SessionId, TenantId, priorBoot)
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(4)
                .WithLastAppliedSignalOrdinal(3)
                .Build();
            SeedSnapshot(rig, saved);

            var result = rig.Recover();

            Assert.Equal(At, result.InitialState.AgentBootUtc);
            Assert.Equal(SessionStage.AwaitingHello, result.InitialState.Stage);
        }

        [Fact]
        public void Snapshot_plus_pending_tail_replays_tail_onto_snapshot()
        {
            using var rig = new Rig();

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            SeedSignalLog(rig, sigs);

            // Snapshot lags one step behind (only ordinal 0 consumed).
            var engine = new DecisionEngine();
            var lagged = engine.Reduce(DecisionState.CreateInitial(SessionId, TenantId), sigs[0]).NewState;
            SeedSnapshot(rig, lagged);

            var expected = ReducerReplay.Replay(engine, DecisionState.CreateInitial(SessionId, TenantId), sigs);

            var result = rig.Recover();

            Assert.False(result.WasStartupQuarantine);
            Assert.Equal(expected.StepIndex, result.InitialState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, result.InitialState.LastAppliedSignalOrdinal);
            Assert.Equal(expected.Stage, result.InitialState.Stage);
        }

        // ================================================================= Branch a — corrupt snapshot

        [Fact]
        public void Corrupt_snapshot_with_intact_log_quarantines_snapshot_only_and_replays_log()
        {
            using var rig = new Rig();

            File.WriteAllText(
                rig.StatePath("snapshot.json"),
                "{\"Checksum\":\"deadbeef\",\"State\":{\"SessionId\":\"S1\",\"TenantId\":\"T1\"}}",
                Encoding.UTF8);

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
                MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
            };
            SeedSignalLog(rig, sigs);

            var expected = ReducerReplay.Replay(
                new DecisionEngine(), DecisionState.CreateInitial(SessionId, TenantId), sigs);

            var result = rig.Recover();

            Assert.True(result.WasStartupQuarantine);
            Assert.False(result.IsWhiteGlovePart2);
            Assert.Equal(expected.StepIndex, result.InitialState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, result.InitialState.LastAppliedSignalOrdinal);

            // Snapshot moved aside; SignalLog survived (NOT quarantined).
            Assert.False(File.Exists(rig.StatePath("snapshot.json")));
            Assert.True(File.Exists(rig.StatePath("signal-log.jsonl")));
            Assert.True(new FileInfo(rig.StatePath("signal-log.jsonl")).Length > 0);

            Assert.True(Directory.Exists(Path.Combine(rig.StateDir, ".quarantine")));
        }

        [Fact]
        public void Corrupt_snapshot_with_head_corrupt_log_escalates_to_total_loss_quarantine()
        {
            // Branch (a-total-loss): the snapshot is corrupt AND the SignalLog is unreadable from
            // its first line (non-empty file, zero parseable signals). Both segment groups are
            // quarantined and the run seeds from a fresh Initial state. This is the branch the
            // end-to-end RecoveryPathTests do not directly exercise.
            using var rig = new Rig();

            File.WriteAllText(
                rig.StatePath("snapshot.json"),
                "{\"Checksum\":\"deadbeef\",\"State\":{\"SessionId\":\"S1\",\"TenantId\":\"T1\"}}",
                Encoding.UTF8);

            // Non-empty but unparseable from line one → ReadAll yields 0 → head-corrupt.
            File.WriteAllText(rig.StatePath("signal-log.jsonl"), "not-json-garbage\n", Encoding.UTF8);

            var result = rig.Recover();

            Assert.True(result.WasStartupQuarantine);
            Assert.Equal(SessionStage.SessionStarted, result.InitialState.Stage);
            Assert.Equal(0, result.InitialState.StepIndex);

            // The garbage log was moved into the quarantine bucket; the live writer is fresh.
            Assert.True(Directory.Exists(Path.Combine(rig.StateDir, ".quarantine")));
            if (File.Exists(rig.StatePath("signal-log.jsonl")))
            {
                var fresh = File.ReadAllText(rig.StatePath("signal-log.jsonl"));
                Assert.DoesNotContain("not-json-garbage", fresh);
            }

            // Fresh writer's recovery counters reset to -1 (no stale ordinal carried over).
            Assert.Equal(-1, result.SignalLog.LastOrdinal);
        }

        // ================================================================= Branch c2 — no snapshot, intact log

        [Fact]
        public void No_snapshot_with_intact_log_rebuilds_via_full_log_replay()
        {
            using var rig = new Rig();

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
                MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
            };
            SeedSignalLog(rig, sigs);
            Assert.False(File.Exists(rig.StatePath("snapshot.json")));

            var expected = ReducerReplay.Replay(
                new DecisionEngine(), DecisionState.CreateInitial(SessionId, TenantId), sigs);

            var result = rig.Recover();

            Assert.True(result.WasStartupQuarantine);
            Assert.Equal(expected.StepIndex, result.InitialState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, result.InitialState.LastAppliedSignalOrdinal);

            // Log was not quarantined — only the missing snapshot drove the replay.
            Assert.True(File.Exists(rig.StatePath("signal-log.jsonl")));
            Assert.True(new FileInfo(rig.StatePath("signal-log.jsonl")).Length > 0);
        }

        // ================================================================= Replay failure → quarantine

        [Fact]
        public void Byte_identical_duplicate_log_line_replays_clean_without_quarantine()
        {
            // Field case (session b9b92d89, 2026-07-09): the dying process double-flushed
            // its final Append → identical JSONL line twice. ReadAll dedupes the artifact,
            // so recovery replays the distinct signals and does NOT quarantine anything.
            using var rig = new Rig();

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            SeedSignalLog(rig, sigs);

            var logPath = rig.StatePath("signal-log.jsonl");
            var lines = File.ReadAllLines(logPath, Encoding.UTF8);
            File.AppendAllText(logPath, lines[lines.Length - 1] + "\n", Encoding.UTF8);

            var expected = ReducerReplay.Replay(
                new DecisionEngine(), DecisionState.CreateInitial(SessionId, TenantId), sigs);

            var result = rig.Recover();

            Assert.Equal(expected.StepIndex, result.InitialState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, result.InitialState.LastAppliedSignalOrdinal);
            Assert.False(Directory.Exists(Path.Combine(rig.StateDir, ".quarantine")));
        }

        [Fact]
        public void Non_monotonic_log_is_quarantined_and_reseeds_fresh_instead_of_throwing()
        {
            // A duplicate ordinal on a NON-identical line (true corruption) must not escape
            // Recover as a fatal exception — pre-fix this produced a permanent startup
            // crash-loop (every restart replayed the same corrupt log and died again).
            // Contract: quarantine all reducer segments, reseed from Initial.
            using var rig = new Rig();

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            SeedSignalLog(rig, sigs);

            // Same ordinal 1, different kind → different bytes → survives ReadAll dedup.
            var rogue = AutopilotMonitor.DecisionCore.Serialization.SignalSerializer.Serialize(
                MakeSignal(1, DecisionSignalKind.DesktopArrived));
            File.AppendAllText(rig.StatePath("signal-log.jsonl"), rogue + "\n", Encoding.UTF8);

            var result = rig.Recover();

            Assert.True(result.WasStartupQuarantine);
            Assert.Equal(SessionStage.SessionStarted, result.InitialState.Stage);
            Assert.Equal(0, result.InitialState.StepIndex);
            Assert.Equal(-1, result.InitialState.LastAppliedSignalOrdinal);

            // Corrupt stream preserved for forensics; live writers are fresh.
            Assert.True(Directory.Exists(Path.Combine(rig.StateDir, ".quarantine")));
            var quarantinedLogs = Directory.GetFiles(
                Path.Combine(rig.StateDir, ".quarantine"), "signal-log.jsonl", SearchOption.AllDirectories);
            Assert.Single(quarantinedLogs);
            Assert.Equal(-1, result.SignalLog.LastOrdinal);
            Assert.Equal(-1, result.Journal.LastStepIndex);
        }

        // ================================================================= Journal alignment

        [Fact]
        public void Journal_ahead_of_seed_truncates_phantom_tail_and_backfills_in_lockstep()
        {
            using var rig = new Rig();

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            SeedSignalLog(rig, sigs);

            var engine = new DecisionEngine();
            var step0 = engine.Reduce(DecisionState.CreateInitial(SessionId, TenantId), sigs[0]);
            var step1 = engine.Reduce(step0.NewState, sigs[1]);
            SeedSnapshot(rig, step0.NewState); // StepIndex=1

            // Journal holds 3 entries — StepIndex 3 is a phantom (no backing signal).
            var journalPath = rig.StatePath("journal.jsonl");
            var seedJournal = new JournalWriter(journalPath, () => rig.Clock.UtcNow);
            seedJournal.Append(step0.Transition);                 // StepIndex=1
            seedJournal.Append(step1.Transition);                 // StepIndex=2
            seedJournal.Append(MakeTransition(3, signalRef: 99));  // StepIndex=3 phantom

            var expected = ReducerReplay.Replay(engine, step0.NewState, new[] { sigs[1] });

            var result = rig.Recover();

            Assert.Equal(expected.StepIndex, result.InitialState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, result.InitialState.LastAppliedSignalOrdinal);

            // Two entries beyond seed.StepIndex=1 went to the phantom bucket.
            var phantomFiles = Directory.GetFiles(
                Path.Combine(rig.StateDir, ".quarantine"),
                "journal-phantom-tail.jsonl",
                SearchOption.AllDirectories);
            Assert.Single(phantomFiles);
            Assert.Equal(2, File.ReadAllLines(phantomFiles[0]).Count(l => !string.IsNullOrWhiteSpace(l)));

            // Live journal rebuilt in lockstep: [1, 2].
            var live = new JournalWriter(journalPath, () => rig.Clock.UtcNow).ReadAll();
            Assert.Equal(2, live.Count);
            Assert.Equal(1, live[0].StepIndex);
            Assert.Equal(2, live[1].StepIndex);
        }

        [Fact]
        public void Signal_log_ahead_of_journal_backfills_missing_journal_entries()
        {
            using var rig = new Rig();

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            SeedSignalLog(rig, sigs);

            var engine = new DecisionEngine();
            var step0 = engine.Reduce(DecisionState.CreateInitial(SessionId, TenantId), sigs[0]);
            SeedSnapshot(rig, step0.NewState); // StepIndex=1, LastApplied=0

            // Journal only has the sig0 transition — sig1's was never flushed.
            var journalPath = rig.StatePath("journal.jsonl");
            new JournalWriter(journalPath, () => rig.Clock.UtcNow).Append(step0.Transition);

            var expected = ReducerReplay.Replay(engine, step0.NewState, new[] { sigs[1] });

            var result = rig.Recover();

            Assert.Equal(expected.StepIndex, result.InitialState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, result.InitialState.LastAppliedSignalOrdinal);

            var live = new JournalWriter(journalPath, () => rig.Clock.UtcNow).ReadAll();
            Assert.Equal(2, live.Count);
            Assert.Equal(1, live[0].StepIndex);
            Assert.Equal(2, live[1].StepIndex);

            // Nothing was beyond the seed boundary → no phantom quarantine.
            var quarantineRoot = Path.Combine(rig.StateDir, ".quarantine");
            if (Directory.Exists(quarantineRoot))
            {
                Assert.Empty(Directory.GetFiles(
                    quarantineRoot, "journal-phantom-tail.jsonl", SearchOption.AllDirectories));
            }
        }

        // ================================================================= WhiteGlove Part-2 resume

        [Fact]
        public void WhiteGloveSealed_snapshot_archives_state_and_sets_part2_flag()
        {
            using var rig = new Rig();

            var sealedState = DecisionState.CreateInitial(SessionId, TenantId)
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .WithStepIndex(5)
                .Build();
            SeedSnapshot(rig, sealedState);
            File.WriteAllText(rig.StatePath("signal-log.jsonl"), "{\"part1-leftover\":true}\n", Encoding.UTF8);
            File.WriteAllText(rig.StatePath("journal.jsonl"), "{\"part1-journal\":true}\n", Encoding.UTF8);
            File.WriteAllText(rig.StatePath("event-sequence.json"), "{\"LastAssignedSequence\":17}", Encoding.UTF8);

            var result = rig.Recover();

            Assert.True(result.IsWhiteGlovePart2);
            Assert.False(result.WasStartupQuarantine);

            // Fresh live state — not the sealed stage.
            Assert.Equal(0, result.InitialState.StepIndex);
            Assert.NotEqual(SessionStage.WhiteGloveSealed, result.InitialState.Stage);

            // Reducer-state segments archived to .part1-*; event-sequence intentionally preserved.
            var buckets = Directory.GetDirectories(rig.StateDir, ".part1-*");
            Assert.Single(buckets);
            Assert.True(File.Exists(Path.Combine(buckets[0], "snapshot.json")));
            Assert.True(File.Exists(Path.Combine(buckets[0], "signal-log.jsonl")));
            Assert.True(File.Exists(Path.Combine(buckets[0], "journal.jsonl")));
            Assert.False(File.Exists(Path.Combine(buckets[0], "event-sequence.json")));
            Assert.Contains("17", File.ReadAllText(rig.StatePath("event-sequence.json")));
        }

        [Fact]
        public void Non_whiteglove_snapshot_does_not_set_part2_flag_or_archive()
        {
            using var rig = new Rig();
            SeedSnapshot(rig, DecisionState.CreateInitial(SessionId, TenantId)
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(3)
                .Build());

            var result = rig.Recover();

            Assert.False(result.IsWhiteGlovePart2);
            Assert.Empty(Directory.GetDirectories(rig.StateDir, ".part1-*"));
        }

        // ================================================================= Prior-run quarantine marker

        [Fact]
        public void Prior_run_quarantine_marker_quarantines_all_segments_and_sets_flag()
        {
            using var rig = new Rig();

            // Seed a snapshot + log so we can prove they get quarantined.
            SeedSnapshot(rig, DecisionState.CreateInitial(SessionId, TenantId)
                .ToBuilder().WithStage(SessionStage.EspDeviceSetup).WithStepIndex(3).Build());
            SeedSignalLog(rig, MakeSignal(0, DecisionSignalKind.SessionStarted));

            const string reason = "consecutive-journal-append-failures";
            File.WriteAllText(
                rig.StatePath(RecoveryCoordinator.QuarantineRequestedMarkerFile),
                "2026-04-20T09:59:00.0000000Z " + reason,
                Encoding.UTF8);

            var result = rig.Recover();

            Assert.True(result.PriorRunQuarantined);
            Assert.NotNull(result.PriorRunQuarantineReason);
            Assert.Contains(reason, result.PriorRunQuarantineReason!);

            // Fresh Initial state — the discarded snapshot did NOT seed it.
            Assert.Equal(SessionStage.SessionStarted, result.InitialState.Stage);
            Assert.Equal(0, result.InitialState.StepIndex);

            // Marker consumed (deleted after a successful quarantine).
            Assert.False(File.Exists(rig.StatePath(RecoveryCoordinator.QuarantineRequestedMarkerFile)));

            // Old snapshot moved aside.
            Assert.False(File.Exists(rig.StatePath("snapshot.json")));
            Assert.True(Directory.Exists(Path.Combine(rig.StateDir, ".quarantine")));
        }

        [Fact]
        public void No_quarantine_marker_leaves_prior_run_flags_clear()
        {
            using var rig = new Rig();

            var result = rig.Recover();

            Assert.False(result.PriorRunQuarantined);
            Assert.Null(result.PriorRunQuarantineReason);
            Assert.False(File.Exists(rig.StatePath(RecoveryCoordinator.QuarantineRequestedMarkerFile)));
        }
    }
}
