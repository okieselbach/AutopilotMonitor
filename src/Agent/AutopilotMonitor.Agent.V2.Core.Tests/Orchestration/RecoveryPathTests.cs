using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    // Snapshot/journal file IO + ThreadPool-driven persistence races make this class
    // intermittently flaky under the parallel xUnit pool. Serialised against other
    // threading-sensitive classes via the SerialThreading collection.
    [Collection("SerialThreading")]
    public sealed class RecoveryPathTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public VirtualClock Clock { get; } = new VirtualClock(At);
            public AgentLogger Logger { get; }
            public FakeBackendTelemetryUploader Uploader { get; } = new FakeBackendTelemetryUploader();
            public string StateDir { get; }
            public string TransportDir { get; }

            public Rig()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                StateDir = Path.Combine(Tmp.Path, "State");
                TransportDir = Path.Combine(Tmp.Path, "Transport");
            }

            public EnrollmentOrchestrator Build(IDeadlineScheduler? schedulerOverride = null) =>
                new EnrollmentOrchestrator(
                    sessionId: "S1",
                    tenantId: "T1",
                    stateDirectory: StateDir,
                    transportDirectory: TransportDir,
                    clock: Clock,
                    logger: Logger,
                    uploader: Uploader,
                    classifiers: new List<IClassifier>(),
                    drainInterval: TimeSpan.FromDays(1),
                    terminalDrainTimeout: TimeSpan.FromSeconds(2),
                    schedulerOverride: schedulerOverride);

            public void Dispose() => Tmp.Dispose();
        }

        // ================================================================= Sonderfall 1: WG Part-1 Resume

        [Fact]
        public void Whiteglove_part1_snapshot_triggers_archive_and_reset_on_start()
        {
            // PR-A V1-symmetric Archive-and-Reset: a persisted WhiteGloveSealed snapshot makes
            // Start() move the reducer-state segment files into a `.part1-<ts>/` subfolder, set
            // the IsWhiteGlovePart2 hint, and run the recovery pipeline as a fresh first-boot.
            // The event-sequence counter is intentionally NOT archived — it stays put so the
            // session-wide event sequence remains monotonic across the resume boundary.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed a snapshot with stage = WhiteGloveSealed (Part 1 complete pre-reboot) plus
            // a non-empty signal-log + journal + event-sequence counter so we can verify which
            // files get archived and which stay put.
            var sealedState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .WithStepIndex(5)
                .Build();
            new SnapshotPersistence(Path.Combine(rig.StateDir, "snapshot.json")).Save(sealedState);
            File.WriteAllText(
                Path.Combine(rig.StateDir, "signal-log.jsonl"),
                "{\"part1-leftover\":true}\n",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(rig.StateDir, "journal.jsonl"),
                "{\"part1-journal\":true}\n",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(rig.StateDir, "event-sequence.json"),
                "{\"LastAssignedSequence\":17}",
                Encoding.UTF8);

            var sut = rig.Build();
            sut.Start();

            Assert.True(sut.IsWhiteGlovePart2);
            Assert.False(sut.WasStartupQuarantine);

            // The recovered state is fresh (StepIndex=0, Stage=SessionStarted) — no WhiteGlove
            // stage on the live engine; the legacy bridge into AwaitingUserSignIn is gone.
            Assert.Equal(0, sut.CurrentState.StepIndex);
            Assert.NotEqual(SessionStage.WhiteGloveSealed, sut.CurrentState.Stage);

            // Archive bucket exists and holds the reducer-state segments.
            var part1Buckets = Directory.GetDirectories(rig.StateDir, ".part1-*");
            Assert.Single(part1Buckets);
            var bucket = part1Buckets[0];
            Assert.True(File.Exists(Path.Combine(bucket, "snapshot.json")));
            Assert.True(File.Exists(Path.Combine(bucket, "signal-log.jsonl")));
            Assert.True(File.Exists(Path.Combine(bucket, "journal.jsonl")));
            Assert.True(File.Exists(Path.Combine(bucket, "reason.txt")));

            // event-sequence.json stayed in place with its Part-1 contents — Part-2
            // emissions continue counting from there, preventing sequence collisions.
            Assert.False(File.Exists(Path.Combine(bucket, "event-sequence.json")));
            var preservedCounter = File.ReadAllText(Path.Combine(rig.StateDir, "event-sequence.json"));
            Assert.Contains("17", preservedCounter);

            // PR-B removed the SessionRecovered enum + handler entirely; nothing in the
            // signal log on resume should match the now-deleted lifecycle bridge. Smoke-check:
            // the only signal kinds we expect on a fresh start are the SessionStarted /
            // EspConfigDetected bootstrap path — never anything carrying "session_recovered".
            var signalLog = GetSignalLog(sut);
            Thread.Sleep(100);
            Assert.DoesNotContain(signalLog.ReadAll(),
                s => s.SourceOrigin != null && s.SourceOrigin.Contains("session_recovered"));

            sut.Stop();

            // After Stop+drain, the uploader must have received a whiteglove_resumed
            // Event-kind item — Plan §4 acceptance criterion. (The corresponding
            // InformationalEvent signal is also persisted but only the Event item is
            // the wire-level enrollment event.)
            Assert.True(SpinWait.SpinUntil(
                () => rig.Uploader.Received
                    .SelectMany(b => b)
                    .Any(item =>
                        item.Kind == TelemetryItemKind.Event &&
                        item.PayloadJson != null &&
                        item.PayloadJson.Contains("\"whiteglove_resumed\"")),
                3000));
        }

        [Fact]
        public void Non_whiteglove_snapshot_does_not_set_part2_resume_flag()
        {
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed snapshot with a non-WG-sealed stage.
            var midState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(3)
                .Build();
            new SnapshotPersistence(Path.Combine(rig.StateDir, "snapshot.json")).Save(midState);

            var sut = rig.Build();
            sut.Start();

            Assert.False(sut.IsWhiteGlovePart2);

            // No archive bucket: this is a regular non-WG snapshot, the orchestrator
            // recovers in-place rather than archiving.
            Assert.Empty(Directory.GetDirectories(rig.StateDir, ".part1-*"));

            sut.Stop();
        }

        // ================================================================= Sonderfall 2: Segment-Quarantine

        [Fact]
        public void Corrupt_snapshot_file_triggers_quarantine_and_fresh_start()
        {
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Write a snapshot file that claims a valid checksum but has mismatching content.
            var snapshotPath = Path.Combine(rig.StateDir, "snapshot.json");
            File.WriteAllText(
                snapshotPath,
                "{\"Checksum\":\"deadbeef\",\"State\":{\"SessionId\":\"S1\",\"TenantId\":\"T1\"}}",
                Encoding.UTF8);

            // Seed a non-empty signal-log too so we can verify it also gets quarantined.
            File.WriteAllText(
                Path.Combine(rig.StateDir, "signal-log.jsonl"),
                "{\"fake\": \"stale signal\"}\n",
                Encoding.UTF8);

            var sut = rig.Build();
            sut.Start();

            Assert.True(sut.WasStartupQuarantine);
            Assert.False(sut.IsWhiteGlovePart2);

            // Original snapshot + signal-log were moved aside.
            Assert.False(File.Exists(snapshotPath));
            var signalLogPath = Path.Combine(rig.StateDir, "signal-log.jsonl");
            if (File.Exists(signalLogPath))
            {
                // Fresh writer: the stale content MUST be gone. A non-empty fresh log is fine —
                // Start() posts bootstrap signals (EspConfigDetected, deadline fires now that
                // the P0 payload-key fix lets them transition cleanly, classifier ticks) and
                // those legitimately land on the new log. What we assert here is strictly that
                // the pre-quarantine "fake stale signal" line did NOT survive into the fresh log.
                //
                // Use FileShare.ReadWrite to avoid racing with an in-flight Append() from the
                // ingress worker: SignalLogWriter.Append opens with FileShare.Read, which
                // disallows a second writer but allows a reader that also grants FileShare.Write.
                // File.ReadAllText uses FileShare.Read, which occasionally throws IOException
                // exactly in the microseconds the writer holds the handle. A manual read with
                // FileShare.ReadWrite is concurrency-safe for our snapshot-of-content check.
                string fresh;
                using (var fs = new FileStream(signalLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    fresh = sr.ReadToEnd();
                }
                Assert.DoesNotContain("fake", fresh);
                Assert.DoesNotContain("stale signal", fresh);
            }

            var quarantineRoot = Path.Combine(rig.StateDir, ".quarantine");
            Assert.True(Directory.Exists(quarantineRoot));
            var buckets = Directory.GetDirectories(quarantineRoot);
            Assert.NotEmpty(buckets);

            // Bucket contains reason + moved segment(s).
            var bucket = buckets[0];
            Assert.True(File.Exists(Path.Combine(bucket, "reason.txt")));

            // Fresh initial state.
            Assert.Equal(SessionStage.SessionStarted, sut.CurrentState.Stage);
            Assert.Equal(0, sut.CurrentState.StepIndex);

            sut.Stop();
        }

        [Fact]
        public void Missing_snapshot_does_not_trigger_quarantine()
        {
            using var rig = new Rig();
            var sut = rig.Build();

            sut.Start();

            Assert.False(sut.WasStartupQuarantine);
            Assert.Equal(SessionStage.SessionStarted, sut.CurrentState.Stage);

            sut.Stop();
        }

        // ============================================================ Codex #1 — ReducerReplay recovery paths

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind = DecisionSignalKind.AppInstallCompleted)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: At.AddSeconds(ordinal),
                sourceOrigin: "recovery-tests",
                evidence: new Evidence(
                    EvidenceKind.Synthetic,
                    $"recovery:ord-{ordinal}",
                    "test signal"));
        }

        private static DecisionTransition MakeTransition(int stepIndex, long signalRef)
        {
            return new DecisionTransition(
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
        }

        [Fact]
        public void Snapshot_plus_pending_tail_replays_tail_onto_snapshot()
        {
            // Crash-lag scenario: SignalLog has caught up to ordinal 1 but the Snapshot was only
            // flushed after the ordinal-0 reduce. On restart the orchestrator must replay the
            // tail (ordinal 1) on top of the snapshot to reach the real post-crash state.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed two signals on the log.
            var signalLogPath = Path.Combine(rig.StateDir, "signal-log.jsonl");
            var seedLog = new SignalLogWriter(signalLogPath);
            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            foreach (var s in sigs) seedLog.Append(s);

            // Compute the expected final state by folding through the real reducer.
            var engine = new DecisionEngine();
            var expected = ReducerReplay.Replay(
                engine, DecisionState.CreateInitial("S1", "T1"), sigs);

            // Persist a snapshot that lags one step behind (only ordinal 0 consumed).
            var snapshotState = engine.Reduce(
                DecisionState.CreateInitial("S1", "T1"), sigs[0]).NewState;
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(snapshotState);

            var sut = rig.Build();
            sut.Start();

            Assert.False(sut.WasStartupQuarantine);
            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);
            Assert.Equal(expected.Stage, sut.CurrentState.Stage);

            sut.Stop();
        }

        [Fact]
        public void No_snapshot_with_intact_log_rebuilds_state_via_full_log_replay()
        {
            // Codex follow-up (post-#50 #A): the snapshot was never written (either an
            // abnormal first-session crash or post-quarantine wipe) BUT the SignalLog
            // already has entries. Branch (c) must NOT fall through to CreateInitial —
            // that would reset StepIndex=0 while the log continues at its original
            // ordinals, breaking monotonicity on the next live append.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed three signals on the log, NO snapshot file on disk.
            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
                MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
            };
            var seedLog = new SignalLogWriter(Path.Combine(rig.StateDir, "signal-log.jsonl"));
            foreach (var s in sigs) seedLog.Append(s);

            Assert.False(File.Exists(Path.Combine(rig.StateDir, "snapshot.json")));

            var engine = new DecisionEngine();
            var expected = ReducerReplay.Replay(
                engine, DecisionState.CreateInitial("S1", "T1"), sigs);

            var sut = rig.Build();
            sut.Start();

            // Full-log replay produced the post-crash state — step/ordinal both at tail.
            Assert.True(sut.WasStartupQuarantine);
            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);
            Assert.Equal(expected.Stage, sut.CurrentState.Stage);

            // SignalLog was NOT quarantined — only the absent snapshot path triggered replay.
            var signalLogPath = Path.Combine(rig.StateDir, "signal-log.jsonl");
            Assert.True(File.Exists(signalLogPath));
            Assert.True(new FileInfo(signalLogPath).Length > 0);

            sut.Stop();
        }

        [Fact]
        public void Corrupt_snapshot_with_intact_log_replays_full_log_without_quarantining_log()
        {
            // Snapshot corrupt (checksum fails) but the SignalLog is fine: the orchestrator
            // quarantines the snapshot only and rebuilds state from the full log.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Write a valid-looking snapshot file with a wrong checksum.
            File.WriteAllText(
                Path.Combine(rig.StateDir, "snapshot.json"),
                "{\"Checksum\":\"deadbeef\",\"State\":{\"SessionId\":\"S1\",\"TenantId\":\"T1\"}}",
                Encoding.UTF8);

            // Seed a parseable SignalLog with real signals.
            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
                MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
            };
            var seedLog = new SignalLogWriter(Path.Combine(rig.StateDir, "signal-log.jsonl"));
            foreach (var s in sigs) seedLog.Append(s);

            var engine = new DecisionEngine();
            var expected = ReducerReplay.Replay(
                engine, DecisionState.CreateInitial("S1", "T1"), sigs);

            var sut = rig.Build();
            sut.Start();

            // Snapshot quarantined, log survived, state reflects the full replay.
            Assert.True(sut.WasStartupQuarantine);
            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);

            var signalLogPath = Path.Combine(rig.StateDir, "signal-log.jsonl");
            Assert.True(File.Exists(signalLogPath));
            Assert.True(new FileInfo(signalLogPath).Length > 0); // log NOT quarantined

            sut.Stop();
        }

        [Fact]
        public void Journal_ahead_of_replayed_state_triggers_phantom_truncate()
        {
            // AHEAD crash scenario: the Journal flushed step for sig1, then also a phantom
            // step for a signal the SignalLog never persisted, while the Snapshot saved
            // only step for sig0. On recovery everything beyond seed.StepIndex goes to the
            // phantom bucket and the replay callback (Codex follow-up post-#50 #C) rebuilds
            // the authoritative tail bit-for-bit in lockstep with state advancement.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Step 1: SignalLog has sig0 + sig1.
            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            var seedLog = new SignalLogWriter(Path.Combine(rig.StateDir, "signal-log.jsonl"));
            foreach (var s in sigs) seedLog.Append(s);

            // Step 2: Produce real engine transitions for sig0 + sig1 — engine semantics
            // say their StepIndex is 1 and 2 (state.StepIndex + 1). Snapshot only captures
            // post-sig0 state; the phantom transition for a non-existent sig beyond has
            // StepIndex=3.
            var engine = new DecisionEngine();
            var step0 = engine.Reduce(DecisionState.CreateInitial("S1", "T1"), sigs[0]);
            var step1 = engine.Reduce(step0.NewState, sigs[1]);
            var snapshotState = step0.NewState; // StepIndex=1
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(snapshotState);

            // Step 3: Journal has 3 transitions on disk — the StepIndex=3 one is phantom
            // (no backing signal in the log).
            var journalPath = Path.Combine(rig.StateDir, "journal.jsonl");
            var seedJournal = new JournalWriter(journalPath, () => rig.Clock.UtcNow);
            seedJournal.Append(step0.Transition);                // StepIndex=1
            seedJournal.Append(step1.Transition);                // StepIndex=2 — pre-crash flushed
            seedJournal.Append(MakeTransition(3, signalRef: 99)); // StepIndex=3 — phantom

            // Expected post-recovery state: replay tail [sig1] → StepIndex=2, LastApplied=1.
            var expected = ReducerReplay.Replay(engine, snapshotState, new[] { sigs[1] });
            Assert.Equal(2, expected.StepIndex);

            var sut = rig.Build();
            sut.Start();

            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);

            // Phantom bucket contains the two entries beyond seed.StepIndex=1 (StepIndex 2 + 3):
            // the pre-crash-flushed real transition AND the signal-less phantom. The replay
            // then rebuilt StepIndex=2 from sig1 via the onTransition callback so the live
            // journal ends in lockstep with state at [1, 2].
            var quarantineRoot = Path.Combine(rig.StateDir, ".quarantine");
            Assert.True(Directory.Exists(quarantineRoot));
            var phantomFiles = Directory.GetFiles(
                quarantineRoot, "journal-phantom-tail.jsonl", SearchOption.AllDirectories);
            Assert.Single(phantomFiles);
            Assert.Equal(2, File.ReadAllLines(phantomFiles[0])
                .Count(l => !string.IsNullOrWhiteSpace(l)));

            var liveJournal = new JournalWriter(journalPath, () => rig.Clock.UtcNow).ReadAll();
            Assert.Equal(2, liveJournal.Count);
            Assert.Equal(1, liveJournal[0].StepIndex);
            Assert.Equal(2, liveJournal[1].StepIndex);

            sut.Stop();
        }

        [Fact]
        public void Signal_log_ahead_of_journal_rebuilds_missing_journal_entries_during_replay()
        {
            // Codex follow-up (post-#50 #C): BEHIND scenario. A crash landed between
            // SignalLog.Append(sig1) and Journal.Append(step1.Transition), so the log is one
            // step ahead of the journal while the snapshot already captures the pre-sig1
            // state. Recovery must NOT leave the journal with a gap — the onTransition
            // callback on ReducerReplay.Replay backfills the missing StepIndex in lockstep.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            var seedLog = new SignalLogWriter(Path.Combine(rig.StateDir, "signal-log.jsonl"));
            foreach (var s in sigs) seedLog.Append(s);

            var engine = new DecisionEngine();
            var step0 = engine.Reduce(DecisionState.CreateInitial("S1", "T1"), sigs[0]);
            var snapshotState = step0.NewState; // StepIndex=1, LastApplied=0
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(snapshotState);

            // Journal only has the sig0 transition — the sig1 transition was never flushed
            // before the crash. LastStepIndex=1 matches snapshotState.StepIndex, so no
            // AHEAD-truncation fires; recovery must backfill StepIndex=2 via the callback.
            var journalPath = Path.Combine(rig.StateDir, "journal.jsonl");
            var seedJournal = new JournalWriter(journalPath, () => rig.Clock.UtcNow);
            seedJournal.Append(step0.Transition); // StepIndex=1

            var expected = ReducerReplay.Replay(engine, snapshotState, new[] { sigs[1] });

            var sut = rig.Build();
            sut.Start();

            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);

            var liveJournal = new JournalWriter(journalPath, () => rig.Clock.UtcNow).ReadAll();
            Assert.Equal(2, liveJournal.Count);
            Assert.Equal(1, liveJournal[0].StepIndex);
            Assert.Equal(2, liveJournal[1].StepIndex);

            // No phantom quarantine happened — nothing was beyond the seed boundary.
            var quarantineRoot = Path.Combine(rig.StateDir, ".quarantine");
            if (Directory.Exists(quarantineRoot))
            {
                var phantomFiles = Directory.GetFiles(
                    quarantineRoot, "journal-phantom-tail.jsonl", SearchOption.AllDirectories);
                Assert.Empty(phantomFiles);
            }

            sut.Stop();
        }

        // ============================================================ Codex post-#50 #E — rehydrate-failure terminal path

        [Fact]
        public void Rehydrate_failure_posts_EffectInfrastructureFailure_and_session_terminates_to_Failed()
        {
            // Codex follow-up (post-#50 #E): if the scheduler can't re-arm a persisted
            // deadline during recovery (ScheduleDeadline throws), DecisionState still claims
            // the deadline is active — same phantom-deadline hang class as the live
            // ScheduleDeadline/CancelDeadline failures. The orchestrator must post a
            // synthetic EffectInfrastructureFailure (v1 payload) so the reducer terminates
            // the session to Stage=Failed / Outcome=EnrollmentFailed rather than waiting
            // out the max-lifetime watchdog.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed a snapshot with ONE persisted deadline so rehydrate runs.
            var persistedDeadline = new ActiveDeadline(
                name: "safety_rearm_target",
                dueAtUtc: At.AddMinutes(30), // future, so the scheduler would normally schedule it
                firesSignalKind: DecisionSignalKind.DeadlineFired);
            var seed = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2)
                .AddDeadline(persistedDeadline)
                .Build();
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(seed);

            // Broken scheduler — throws on every Schedule call, which is what
            // RehydrateFromSnapshot drives internally.
            var brokenScheduler = new FakeDeadlineScheduler
            {
                ThrowOnSchedule = new InvalidOperationException("timer-subsystem-dead"),
            };

            var sut = rig.Build(schedulerOverride: brokenScheduler);
            sut.Start();

            // Expected terminal state: reducer handles EffectInfrastructureFailure →
            // Stage.Failed, Outcome.EnrollmentFailed, ClearDeadlines.
            Assert.True(
                SpinWait.SpinUntil(() => sut.CurrentState.Stage == SessionStage.Failed, 5000),
                $"Expected terminal Failed stage after rehydrate failure; stuck at {sut.CurrentState.Stage}.");
            Assert.Equal(SessionOutcome.EnrollmentFailed, sut.CurrentState.Outcome);

            // Signal landed durably on the SignalLog with the v1 contract payload.
            var signalLog = GetSignalLog(sut);
            var failureSignal = signalLog.ReadAll()
                .FirstOrDefault(s => s.Kind == DecisionSignalKind.EffectInfrastructureFailure);
            Assert.NotNull(failureSignal);
            Assert.NotNull(failureSignal!.Payload);
            Assert.Contains("deadline_rehydrate_failure", failureSignal.Payload!["reason"]);
            Assert.Equal("ScheduleDeadline", failureSignal.Payload["failingEffect"]);
            Assert.Equal("effectrunner:critical:ScheduleDeadline", failureSignal.SourceOrigin);

            sut.Stop();
        }

        // ============================================================ First-session regression — deadline payload key

        [Fact]
        public void Past_due_FinalizingGrace_deadline_advances_stage_to_Completed()
        {
            // Regression guard for session 9ed7021e: the orchestrator's DeadlineFired bridge
            // used to post payload `["deadlineName"]=...` while the reducer reads under
            // SignalPayloadKeys.Deadline. Result: every deadline (FinalizingGrace, HelloSafety,
            // ClassifierTick, …) dead-ended as "deadline_fired_without_name" — FinalizingGrace
            // specifically left every Classic session hanging in Stage=Finalizing forever and
            // blocked enrollment_complete emission, log flush, and diagnostics upload.
            //
            // This test seeds a state that mirrors the real failure: Stage=Finalizing with an
            // already-past-due FinalizingGrace deadline (DueAt = UtcNow - 5m). After Start()
            // rehydrates the deadline the scheduler fires it synchronously via ThreadPool; the
            // bridge must forward ActiveDeadline.FiresPayload verbatim so the reducer can
            // route to HandleFinalizingGraceDeadlineFired and transition Finalizing→Completed.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            var pastDueFinalizing = new ActiveDeadline(
                name: DeadlineNames.FinalizingGrace,
                dueAtUtc: At.AddMinutes(-5),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace,
                });
            var finalizingSeed = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.Finalizing)
                .WithStepIndex(10)
                .WithLastAppliedSignalOrdinal(9)
                .AddDeadline(pastDueFinalizing)
                .Build();
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(finalizingSeed);

            var sut = rig.Build();
            sut.Start();

            Assert.True(
                SpinWait.SpinUntil(() => sut.CurrentState.Stage == SessionStage.Completed, 5000),
                $"Expected Stage=Completed after past-due FinalizingGrace fired; stuck at {sut.CurrentState.Stage}.");
            Assert.Equal(SessionOutcome.EnrollmentComplete, sut.CurrentState.Outcome);

            // Paranoid belt-and-braces: the posted DeadlineFired signal must carry the deadline
            // name under SignalPayloadKeys.Deadline (the key the reducer reads). Absence of this
            // key is precisely how the bug manifested — dead-end "deadline_fired_without_name".
            var signalLog = GetSignalLog(sut);
            var fired = signalLog.ReadAll()
                .FirstOrDefault(s => s.Kind == DecisionSignalKind.DeadlineFired);
            Assert.NotNull(fired);
            Assert.NotNull(fired!.Payload);
            Assert.True(
                fired.Payload!.TryGetValue(SignalPayloadKeys.Deadline, out var name)
                    && name == DeadlineNames.FinalizingGrace,
                $"Expected payload[{SignalPayloadKeys.Deadline}]={DeadlineNames.FinalizingGrace}; " +
                $"got keys=[{string.Join(",", fired.Payload.Keys)}].");

            sut.Stop();
        }

        // ============================================================ Codex #1 Phase 3 — deadline re-arm

        [Fact]
        public void Snapshot_with_past_due_deadline_fires_DeadlineFired_signal_on_start()
        {
            // Past-due re-arm: a deadline whose DueAtUtc lies before clock.UtcNow must fire
            // immediately on rehydrate. The scheduler's past-due path queues to ThreadPool,
            // the Fired bridge synthesises a DeadlineFired signal, and it lands on the log.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            var pastDue = new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: At.AddMinutes(-5),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.Deadline] = "hello_safety",
                });
            var seed = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2)
                .AddDeadline(pastDue)
                .Build();
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(seed);

            var sut = rig.Build();
            sut.Start();

            var signalLog = GetSignalLog(sut);
            Assert.True(
                SpinWait.SpinUntil(
                    () => signalLog.ReadAll().Any(s =>
                        s.Kind == DecisionSignalKind.DeadlineFired &&
                        s.Payload != null &&
                        s.Payload.TryGetValue(SignalPayloadKeys.Deadline, out var n) &&
                        n == "hello_safety"),
                    3000),
                "Expected DeadlineFired signal for re-armed past-due deadline.");

            sut.Stop();
        }

        [Fact]
        public void Snapshot_with_future_deadline_is_scheduled_but_not_yet_fired()
        {
            // Future re-arm: the rehydrated deadline is live on the scheduler (IsScheduled=true)
            // but no DeadlineFired signal has been emitted because its wall-clock due time
            // has not arrived. Stage AwaitingHello (a regular Classic-flow stage that doesn't
            // trigger PR-A's WhiteGlove archive-and-reset path) so the deadline survives recovery.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            var future = new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: DateTime.UtcNow.AddHours(2), // real wall-clock future (scheduler uses wall clock)
                firesSignalKind: DecisionSignalKind.DeadlineFired);
            var seed = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .AddDeadline(future)
                .Build();
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(seed);

            var sut = rig.Build();
            sut.Start();

            // Scheduler must know about the re-armed deadline.
            var scheduler = GetScheduler(sut);
            Assert.True(scheduler.IsScheduled("hello_safety"),
                "Future-due persisted deadline was not re-armed on the scheduler.");

            // Give the ThreadPool a moment to prove we don't fire it prematurely.
            Thread.Sleep(100);
            var signalLog = GetSignalLog(sut);
            Assert.DoesNotContain(signalLog.ReadAll(),
                s => s.Kind == DecisionSignalKind.DeadlineFired);

            sut.Stop();
        }

        // ================================================================= Sonderfall 3: Transport-Resume

        [Fact]
        public void Transport_resumes_from_persisted_cursor_after_restart()
        {
            using var rig = new Rig();
            Directory.CreateDirectory(rig.TransportDir);

            // Seed spool with 3 items (ItemIds 0/1/2, zero-based) and mark ItemId 0 as
            // already uploaded. Remaining pending: ItemIds 1 + 2.
            var spoolDir = rig.TransportDir;
            var seedSpool = new TelemetrySpool(spoolDir, rig.Clock);
            for (int i = 1; i <= 3; i++)
            {
                seedSpool.Enqueue(new TelemetryItemDraft(
                    kind: TelemetryItemKind.Event,
                    partitionKey: "T1_S1",
                    rowKey: $"row-{i}",
                    payloadJson: $"{{\"n\":{i}}}",
                    isSessionScoped: true,
                    requiresImmediateFlush: false));
            }
            seedSpool.MarkUploaded(0);   // pretend ItemId 0 already uploaded → 2 pending

            // Re-open via Orchestrator — uploader is scripted OK so the remaining 2 items drain.
            rig.Uploader.QueueOk(10);
            var sut = rig.Build();
            sut.Start();

            // Explicit drain — tests don't wait for the periodic loop.
            var drainResult = InvokeDrain(sut);
            Assert.True(drainResult.Success);
            Assert.Equal(2, drainResult.UploadedItems);

            // Uploader received one batch containing only items 2 + 3 (cursor was 1 on startup).
            Assert.Single(rig.Uploader.Received);
            Assert.Equal(2, rig.Uploader.Received[0].Count);

            sut.Stop();
        }

        // ================================================================= Helpers

        private static ISignalLogWriter GetSignalLog(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_signalLog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (ISignalLogWriter)field!.GetValue(sut)!;
        }

        private static IDeadlineScheduler GetScheduler(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_scheduler",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (IDeadlineScheduler)field!.GetValue(sut)!;
        }

        private static DrainResult InvokeDrain(EnrollmentOrchestrator sut)
        {
            var method = typeof(EnrollmentOrchestrator).GetMethod(
                "DrainAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var task = (System.Threading.Tasks.Task<DrainResult>)method!.Invoke(sut, new object?[] { CancellationToken.None })!;
            return task.GetAwaiter().GetResult();
        }
    }
}
