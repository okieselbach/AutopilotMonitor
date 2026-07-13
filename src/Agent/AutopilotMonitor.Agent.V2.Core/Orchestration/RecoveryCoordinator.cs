#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Immutable outcome of <see cref="RecoveryCoordinator.Recover"/>: the freshly-opened
    /// persistence writers, the reconstructed seed <see cref="DecisionState"/>, and the
    /// recovery flags the orchestrator surfaces for observability and downstream wiring.
    /// </summary>
    internal sealed class RecoveryResult
    {
        public RecoveryResult(
            SignalLogWriter signalLog,
            JournalWriter journal,
            SnapshotPersistence snapshot,
            EventSequencePersistence eventSequence,
            DecisionState initialState,
            bool isWhiteGlovePart2,
            bool wasStartupQuarantine,
            bool priorRunQuarantined,
            string? priorRunQuarantineReason)
        {
            SignalLog = signalLog;
            Journal = journal;
            Snapshot = snapshot;
            EventSequence = eventSequence;
            InitialState = initialState;
            IsWhiteGlovePart2 = isWhiteGlovePart2;
            WasStartupQuarantine = wasStartupQuarantine;
            PriorRunQuarantined = priorRunQuarantined;
            PriorRunQuarantineReason = priorRunQuarantineReason;
        }

        /// <summary>SignalLog writer opened against the (post-quarantine, post-archive) state dir.</summary>
        public SignalLogWriter SignalLog { get; }

        /// <summary>Journal writer, aligned in lockstep with <see cref="InitialState"/>.</summary>
        public JournalWriter Journal { get; }

        /// <summary>Snapshot persistence handle for the live pipeline's save path.</summary>
        public SnapshotPersistence Snapshot { get; }

        /// <summary>Event-sequence persistence (intentionally never archived across WG resume).</summary>
        public EventSequencePersistence EventSequence { get; }

        /// <summary>The seed state to start the live decision pipeline from.</summary>
        public DecisionState InitialState { get; }

        /// <summary>
        /// <c>true</c> when a persisted <see cref="SessionStage.WhiteGloveSealed"/> snapshot was
        /// detected and the prior state folder archived to <c>.part1-&lt;ts&gt;/</c>.
        /// </summary>
        public bool IsWhiteGlovePart2 { get; }

        /// <summary>
        /// <c>true</c> when a corrupt snapshot / head-corrupt or orphaned SignalLog forced a
        /// startup quarantine of state segments.
        /// </summary>
        public bool WasStartupQuarantine { get; }

        /// <summary><c>true</c> when a prior-run quarantine marker was honoured this start.</summary>
        public bool PriorRunQuarantined { get; }

        /// <summary>Reason text from the prior-run quarantine marker, or <c>null</c>.</summary>
        public string? PriorRunQuarantineReason { get; }
    }

    /// <summary>
    /// Owns the V2 agent's crash-recovery state machine (Plan §2.7). Extracted from
    /// <see cref="EnrollmentOrchestrator.Start"/> (ARCH-F5) so the recovery decision logic is
    /// isolated from the orchestrator's component-wiring steps.
    /// <para>
    /// <b>Order is load-bearing</b> and mirrors the original Start() body exactly:
    /// <list type="number">
    ///   <item><b>-1) Prior-run quarantine marker</b> — honour a marker left by a prior run that
    ///   could no longer persist consistently, quarantining all state segments + snapshot BEFORE
    ///   the writers scan the suspect files. Preserves the "a running process never pulls its own
    ///   state files out from under itself" invariant.</item>
    ///   <item><b>0) WhiteGlove Part-2 resume</b> — when the persisted snapshot is
    ///   <see cref="SessionStage.WhiteGloveSealed"/>, archive the reducer-state segments aside and
    ///   begin a fresh Classic flow. <c>event-sequence.json</c> is intentionally preserved.</item>
    ///   <item><b>1) Persistence writers</b> — opened after -1/0 so they scan a clean directory.</item>
    ///   <item><b>2) Recovery branches</b> — pick the seed + replay stream (forced-clean / corrupt
    ///   snapshot / clean snapshot tail-replay / no-snapshot), align the journal AHEAD-phantom
    ///   boundary, then fold via <see cref="ReducerReplay.Replay"/> with journal backfill.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The invariant throughout: the SignalLog is authoritative truth, the Snapshot is a cache —
    /// every recovery prefers reconstructing from the log over trusting a stale/corrupt snapshot.
    /// </para>
    /// </summary>
    internal static class RecoveryCoordinator
    {
        // TRACE-H2: marker file written by EnrollmentOrchestrator.TriggerQuarantine (mid-run, when
        // the journal can no longer be appended) and honoured on the NEXT Recover — a running
        // process must not pull the state files out from under itself. When the marker is found at
        // startup, all reducer-state segments + snapshot are quarantined and the session starts
        // from a clean Initial state.
        public const string QuarantineRequestedMarkerFile = "quarantine-requested.marker";

        /// <summary>
        /// L7 (delta review 2026-07-02): marker-content prefix written when the quarantine was
        /// performed but the marker file could not be deleted (AV/ACL hold). A marker carrying
        /// this sentinel means "already handled — retry the delete, do NOT re-quarantine",
        /// preventing a stuck marker from wiping the fresh run's state on every restart.
        /// </summary>
        internal const string QuarantineProcessedSentinel = "processed:";

        /// <summary>
        /// Runs the full recovery pipeline against <paramref name="stateDirectory"/> and returns
        /// the opened writers, seed state, and recovery flags. The directory is assumed to exist
        /// (the orchestrator calls <c>EnsureDirectories</c> first).
        /// </summary>
        public static RecoveryResult Recover(
            string stateDirectory,
            string sessionId,
            string tenantId,
            IClock clock,
            AgentLogger logger)
        {
            var snapshotPath = Path.Combine(stateDirectory, "snapshot.json");
            var signalLogPath = Path.Combine(stateDirectory, "signal-log.jsonl");
            var journalPath = Path.Combine(stateDirectory, "journal.jsonl");
            var eventSequencePath = Path.Combine(stateDirectory, "event-sequence.json");

            var isWhiteGlovePart2 = false;
            // A (session 62e603c9): the Part-1 scenario classification (Mode=WhiteGlove/High)
            // captured off the archived snapshot BEFORE it is moved aside, so it can be
            // re-seeded into the fresh Part-2 state below. Without this carry-forward Part 2
            // starts at Mode=Unknown and the device-only ESP-detection deadline can wrongly
            // reclassify a user-driven WhiteGlove flow as SelfDeploying and complete early.
            EnrollmentScenarioProfile? preservedWhiteGloveProfile = null;
            var wasStartupQuarantine = false;
            var priorRunQuarantined = false;
            string? priorRunQuarantineReason = null;

            // P2: set when a prior-run quarantine was REQUESTED (marker present) but did NOT fully
            // succeed this start. The marker demands a clean reset, so we must NOT fall back to normal
            // snapshot/log recovery (which could reload the very state we were told to discard) — this
            // forces the run to seed from a fresh Initial state instead.
            var forceCleanStart = false;

            // -1) TRACE-H2 — honour a prior run's quarantine request BEFORE the writers scan the
            //     (suspect) segment files. A prior run hit the consecutive-journal-failure threshold
            //     and could no longer persist consistently; it left the marker so THIS run resets
            //     cleanly. Quarantining here (not mid-run) preserves the "a running process never
            //     pulls its own state files out from under itself" invariant. event-sequence is
            //     quarantined too — a clean restart re-seeds it from 0 (backend dedups on RowKey).
            var quarantineMarkerPath = Path.Combine(stateDirectory, QuarantineRequestedMarkerFile);
            if (File.Exists(quarantineMarkerPath))
            {
                var quarantineReason = TryReadMarkerReason(quarantineMarkerPath);
                if (quarantineReason.StartsWith(QuarantineProcessedSentinel, StringComparison.Ordinal))
                {
                    // L7 (delta review 2026-07-02): a prior start already PERFORMED this
                    // quarantine but could not delete the marker (AV/ACL hold) and neutralized
                    // it with the processed sentinel instead. Re-running the quarantine here
                    // would wipe the FRESH run's state on every restart — just retry the delete
                    // and continue with normal recovery.
                    logger.Warning(
                        "EnrollmentOrchestrator: neutralized quarantine marker still present — quarantine " +
                        "already performed by a prior start; retrying marker delete only.");
                    try { File.Delete(quarantineMarkerPath); }
                    catch (Exception ex) { logger.Warning($"EnrollmentOrchestrator: quarantine marker delete retry failed: {ex.Message}"); }
                }
                else
                {
                logger.Error(
                    "EnrollmentOrchestrator: prior-run quarantine marker found — quarantining all state " +
                    $"segments + snapshot before recovery. Reason: {quarantineReason}");
                try
                {
                    SegmentQuarantine.QuarantineAll(
                        stateDirectory, "quarantine-requested:" + quarantineReason, () => clock.UtcNow);
                    new SnapshotPersistence(snapshotPath, () => clock.UtcNow)
                        .Quarantine("quarantine-requested:" + quarantineReason);

                    // P2: delete the marker ONLY after the quarantine fully succeeded. If QuarantineAll
                    // or the snapshot quarantine threw (ACL / lock / disk), the marker is LEFT so the
                    // next start retries fail-closed instead of proceeding on possibly-still-corrupt
                    // segments with the retry hint lost.
                    try { File.Delete(quarantineMarkerPath); }
                    catch (Exception deleteEx)
                    {
                        logger.Warning($"EnrollmentOrchestrator: quarantine marker delete failed: {deleteEx.Message}");
                        // L7: the quarantine itself SUCCEEDED — neutralize the stuck marker so the
                        // next start does not re-wipe fresh state. Best-effort: if the write fails
                        // too (same ACL hold), behavior degrades to the previous repeated-wipe.
                        try
                        {
                            File.WriteAllText(
                                quarantineMarkerPath,
                                QuarantineProcessedSentinel + clock.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                                System.Text.Encoding.UTF8);
                        }
                        catch (Exception writeEx)
                        {
                            logger.Warning($"EnrollmentOrchestrator: quarantine marker neutralization failed: {writeEx.Message}");
                        }
                    }

                    priorRunQuarantined = true;
                    priorRunQuarantineReason = quarantineReason;
                }
                catch (Exception ex)
                {
                    // Marker intentionally retained — do NOT delete on failure (P2). AND force a
                    // clean Initial seed for THIS run: a partial failure (e.g. segments moved but the
                    // snapshot quarantine threw) must not let normal recovery reload the discarded
                    // snapshot. Fail-closed to fresh state; the retained marker drives a proper retry
                    // next start.
                    forceCleanStart = true;
                    logger.Error(
                        "EnrollmentOrchestrator: prior-run quarantine cleanup FAILED — marker retained for "
                        + "next-start retry; forcing a clean Initial seed for this run.", ex);
                }
                }
            }

            // 0) WhiteGlove Part-2 resume detection. V1-symmetric Archive-and-Reset
            //    pattern: peek the persisted snapshot via the lock-free static reader; if
            //    the prior run sealed Part-1, move the reducer-state segment files
            //    (snapshot, signal-log, journal) aside to a timestamped
            //    <c>.part1-&lt;ts&gt;/</c> bucket BEFORE the writers below open them for
            //    scanning. <c>event-sequence.json</c> is intentionally preserved so the
            //    session-wide event sequence stays monotonic across the resume boundary
            //    (the backend orders events by <c>(SessionId, Sequence)</c> and the
            //    Web UI splits on <c>resumed.sequence - 1</c>). The orchestrator then
            //    runs through the normal recovery branches as a fresh first-boot
            //    (branch c1), re-emits a <c>whiteglove_resumed</c> lifecycle event
            //    after the onIngressReady hook, and lets the standard Classic
            //    enrollment flow drive completion.
            //    Anti-loop: once archived, the WhiteGloveSealed snapshot is gone, so a
            //    subsequent restart sees no marker and continues as a Classic session.
            //    The hint bool is in-memory only — also matches V1 (which clears the
            //    on-disk marker file before emitting <c>whiteglove_resumed</c>).
            if (Directory.Exists(stateDirectory))
            {
                var rawSnapshot = SnapshotPersistence.TryReadRaw(snapshotPath);
                if (rawSnapshot != null && rawSnapshot.Stage == SessionStage.WhiteGloveSealed)
                {
                    // Capture the Part-1 classification before ArchiveStateFolder moves the
                    // snapshot aside — re-seeded into the fresh Part-2 state after the branch
                    // dispatch below (A, session 62e603c9).
                    preservedWhiteGloveProfile = rawSnapshot.ScenarioProfile;
                    StateArchiver.ArchiveStateFolder(
                        stateDirectory: stateDirectory,
                        reason: "wg_part1_resume_archive",
                        utcNow: () => clock.UtcNow,
                        logger: logger);
                    isWhiteGlovePart2 = true;
                    logger.Info(
                        "EnrollmentOrchestrator: WhiteGlove Part-1 resume detected — state archived; " +
                        "starting fresh Classic enrollment flow for Part-2.");
                }
            }

            // 1) Persistenz. Writer scannen bestehende Files im Ctor (LastOrdinal / LastStepIndex).
            var signalLog = new SignalLogWriter(signalLogPath);
            var journal = new JournalWriter(journalPath, () => clock.UtcNow);
            var snapshot = new SnapshotPersistence(snapshotPath, () => clock.UtcNow);
            var eventSequence = new EventSequencePersistence(eventSequencePath);

            // 2) Recovery (Plan §2.7 Sonderfälle 1+2, Codex follow-up #1).
            //
            //    Invariant: the SignalLog is authoritative truth, the Snapshot is a cache.
            //    Therefore on every recovery we prefer reconstructing state from the log over
            //    trusting a stale/corrupt snapshot. ReducerReplay.Replay (Phase 1) performs the
            //    pure fold; this method decides the seed + which signals to feed it.
            //
            //    Branches:
            //      a) Snapshot present but corrupt → quarantine the snapshot ONLY (not the log!)
            //         and rebuild from the full SignalLog. If the log itself is head-corrupt
            //         (file non-empty but ReadAll yields zero signals) we escalate to a total-
            //         loss quarantine of the log segments and start fresh.
            //      b) Snapshot loaded cleanly → replay any SignalLog tail past the snapshot's
            //         LastAppliedSignalOrdinal so a pre-crash Journal/Snapshot lag is closed.
            //      c) No snapshot file — two sub-cases (Codex follow-up post-#50 #A):
            //         c1) SignalLog also empty → genuinely fresh start.
            //         c2) SignalLog has entries → crash before the first snapshot save ever
            //             landed; rebuild from the full SignalLog like branch (a) does after
            //             snapshot quarantine. Anything else would keep stale log entries on
            //             disk while state resets to StepIndex=0 and break monotonicity on
            //             the next append.
            //
            //    After the seed + replay-stream are selected the Journal is aligned AHEAD-OF
            //    replay (phantom suffix beyond seed.StepIndex-1 goes to the forensic bucket)
            //    and backfilled DURING replay via the onTransition callback. This closes two
            //    crash windows in one mechanism (Codex follow-up post-#50 #C):
            //      • AHEAD — Journal was flushed but Snapshot not; phantom transitions beyond
            //        the seed get quarantined before replay so live Append monotonicity holds.
            //      • BEHIND — SignalLog was flushed but Journal not; the replay callback
            //        rematerialises the missing StepIndex entries onto the Journal so it
            //        ends up in exact lockstep with initialState.
            var snapshotFileExistsPreLoad = File.Exists(snapshotPath);
            var loadedState = snapshot.Load();
            DecisionState initialState;

            // Reducer is stateless — a transient instance is enough for the pure replay below.
            // The live ingress pipeline creates its own instance further down the Start() body.
            var replayEngine = new DecisionEngine();

            // Dispatch: pick the seed + signal stream per branch. Null seed is impossible past
            // this block (every path assigns it), but the compiler needs the explicit init.
            // AgentBootUtc gets stamped from the live clock so reducer deadline-arming sites
            // can floor replayed-signal timestamps at the current run's start (replay-safety).
            var agentBootUtc = clock.UtcNow;
            DecisionState seed = DecisionState.CreateInitial(sessionId, tenantId, agentBootUtc);
            IReadOnlyList<DecisionSignal> signalsToReplay = Array.Empty<DecisionSignal>();
            string branchTag;

            if (forceCleanStart)
            {
                // P2 — a requested prior-run quarantine did not fully succeed. Fail-closed: ignore
                // any (possibly-still-present) snapshot and SignalLog and seed from a fresh Initial
                // state. seed/signalsToReplay are already CreateInitial/empty above. The retained
                // marker drives a proper full quarantine + retry on the next start.
                logger.Warning(
                    "EnrollmentOrchestrator: forcing clean Initial seed (prior-run quarantine incomplete) — "
                    + "skipping snapshot/log recovery for this run.");
                branchTag = "forced-clean-after-failed-quarantine";
            }
            else if (loadedState == null && snapshotFileExistsPreLoad)
            {
                // Branch (a) — Snapshot corrupt. Quarantine the snapshot and attempt log replay.
                logger.Error(
                    "EnrollmentOrchestrator: snapshot present but Load returned null (checksum mismatch or parse error) — quarantining snapshot and attempting SignalLog replay.");
                snapshot.Quarantine("checksum-mismatch-on-startup");

                var loggedSignals = signalLog.ReadAll();
                var logFileInfo = new FileInfo(signalLogPath);
                var logIsHeadCorrupt =
                    logFileInfo.Exists && logFileInfo.Length > 0 && loggedSignals.Count == 0;

                if (logIsHeadCorrupt)
                {
                    logger.Warning(
                        "EnrollmentOrchestrator: SignalLog unreadable from the first line — escalating to total-loss quarantine.");
                    SegmentQuarantine.QuarantineAll(
                        stateDirectory, "log-head-corrupt-after-snapshot-loss", () => clock.UtcNow);

                    // Writer hold paths, not handles — but their in-memory counters are stale
                    // after the quarantine move. Recreate to reset them to -1.
                    signalLog = new SignalLogWriter(signalLogPath);
                    journal = new JournalWriter(journalPath, () => clock.UtcNow);
                    eventSequence = new EventSequencePersistence(eventSequencePath);

                    branchTag = "a-total-loss";
                }
                else
                {
                    signalsToReplay = loggedSignals;
                    branchTag = "a-full-log-replay";
                }

                wasStartupQuarantine = true;
            }
            else if (loadedState != null)
            {
                // Branch (b) — Snapshot loaded. Catch up any SignalLog tail past the snapshot.
                // Re-stamp AgentBootUtc so the current run's deadlines floor at "now" — the
                // persisted boot anchor is from the prior run and using it would let replayed
                // tail signals arm deadlines that are already past-due (premature fire).
                seed = loadedState.ToBuilder().WithAgentBootUtc(agentBootUtc).Build();
                signalsToReplay = CollectSignalLogTailAfter(signalLog, loadedState.LastAppliedSignalOrdinal);
                branchTag = signalsToReplay.Count > 0 ? "b-tail-replay" : "b-snapshot-current";
            }
            else
            {
                // Branch (c) — no snapshot on disk. Two sub-cases:
                //   (c1) SignalLog also empty → genuinely fresh start.
                //   (c2) SignalLog has entries (crash BEFORE first snapshot save) → full
                //        log replay from CreateInitial (Codex follow-up post-#50 #A).
                var loggedSignals = signalLog.ReadAll();
                if (loggedSignals.Count == 0)
                {
                    branchTag = "c1-fresh";
                }
                else
                {
                    signalsToReplay = loggedSignals;
                    wasStartupQuarantine = true;
                    branchTag = "c2-full-log-replay";
                }
            }

            // A (session 62e603c9) — carry the Part-1 WhiteGlove classification into the fresh
            // Part-2 seed. isWhiteGlovePart2 implies branch c1-fresh (the snapshot was archived,
            // so Load returned null), meaning seed is CreateInitial with an Empty profile. We
            // re-stamp Mode=WhiteGlove/High and flip PreProvisioningSide to the user side; the
            // existing monotonic-mode guards (EnrollmentScenarioProfileUpdater +
            // DecisionEngine.SelfDeploying race guard C) then prevent the device-only ESP
            // deadline from reclassifying this user-driven flow as SelfDeploying. JoinMode /
            // EspConfig from Part 1 ride along (e.g. HybridAzureAdJoin) — harmless and useful.
            if (isWhiteGlovePart2 && preservedWhiteGloveProfile != null)
            {
                var part2Profile = preservedWhiteGloveProfile.With(
                    mode: EnrollmentMode.WhiteGlove,
                    confidence: ProfileConfidence.High,
                    preProvisioningSide: PreProvisioningSide.User,
                    reason: "whiteglove_part2_carryforward");
                seed = seed.ToBuilder().WithScenarioProfile(part2Profile).Build();
                logger.Info(
                    "EnrollmentOrchestrator: WhiteGlove Part-2 seed carries forward Mode=WhiteGlove/High " +
                    "(user side) from the archived Part-1 snapshot.");
            }

            // Align Journal AHEAD phantoms before replay. Engine semantics: a transition
            // produced for state at StepIndex=K carries StepIndex=K+1 (see
            // DecisionEngine.BuildTakenTransition); after K reduces from initial the journal
            // holds entries [1..K] with LastStepIndex=K. So any on-disk entry beyond
            // seed.StepIndex is a phantom from a crash between Journal.Append and
            // Snapshot.Save. Truncating first lets the replay callback Append monotonically
            // from seed.StepIndex+1 without colliding.
            var journalBoundary = seed.StepIndex;
            if (journal.LastStepIndex > journalBoundary)
            {
                logger.Warning(
                    $"EnrollmentOrchestrator: Journal ahead of seed state " +
                    $"(journal.LastStepIndex={journal.LastStepIndex}, seed.StepIndex={seed.StepIndex}) — " +
                    $"truncating phantom transitions to boundary={journalBoundary}.");
                journal.TruncateAfter(journalBoundary);
            }

            // Replay + Journal backfill via the onTransition callback (Codex follow-up
            // post-#50 #C). For BEHIND crashes (SignalLog flushed, Journal not) this
            // rematerialises every missing StepIndex. For no-replay branches the callback
            // is simply never invoked; the journal stays aligned from the pre-replay
            // truncate step above.
            try
            {
                initialState = ReducerReplay.Replay(
                    engine: replayEngine,
                    seed: seed,
                    signals: signalsToReplay,
                    onTransition: journal.Append);
            }
            catch (InvalidOperationException ex)
            {
                // ReducerReplay (or the Journal backfill callback) rejected the persisted
                // stream — non-monotonic ordinal, null signal, or a journal monotonicity
                // violation. The exception contract says "caller should quarantine rather
                // than trust this stream": do exactly that instead of letting the throw
                // escape as a fatal startup failure. Field case (session b9b92d89,
                // 2026-07-09): a self-update restart killed the agent mid-append and left a
                // duplicated final line in the SignalLog; the uncaught throw here turned
                // that single crash artifact into a PERMANENT crash-loop — every restart
                // replayed the same corrupt log and died again. Fail-closed to a fresh
                // Initial seed; the quarantine bucket keeps the stream for forensics.
                logger.Error(
                    "EnrollmentOrchestrator: SignalLog replay failed — quarantining all reducer " +
                    "segments and reseeding from Initial state.", ex);

                snapshot.Quarantine("replay-failed: " + ex.Message);
                SegmentQuarantine.QuarantineAll(
                    stateDirectory, "replay-failed: " + ex.Message, () => clock.UtcNow);

                // Same pattern as the log-head-corrupt quarantine above: writers hold paths,
                // not handles, but their in-memory counters are stale after the move —
                // recreate to reset them to -1.
                signalLog = new SignalLogWriter(signalLogPath);
                journal = new JournalWriter(journalPath, () => clock.UtcNow);
                eventSequence = new EventSequencePersistence(eventSequencePath);

                seed = DecisionState.CreateInitial(sessionId, tenantId, agentBootUtc);
                signalsToReplay = Array.Empty<DecisionSignal>();
                initialState = seed;
                branchTag += "+replay-quarantined";
                wasStartupQuarantine = true;
            }

            logger.Info(
                $"EnrollmentOrchestrator: recovery branch={branchTag}, " +
                $"stage={initialState.Stage}, stepIndex={initialState.StepIndex}, " +
                $"signalsReplayed={signalsToReplay.Count}, " +
                $"journal.LastStepIndex={journal.LastStepIndex}.");

            return new RecoveryResult(
                signalLog: signalLog,
                journal: journal,
                snapshot: snapshot,
                eventSequence: eventSequence,
                initialState: initialState,
                isWhiteGlovePart2: isWhiteGlovePart2,
                wasStartupQuarantine: wasStartupQuarantine,
                priorRunQuarantined: priorRunQuarantined,
                priorRunQuarantineReason: priorRunQuarantineReason);
        }

        /// <summary>
        /// Recovery helper — collect all SignalLog entries whose <c>SessionSignalOrdinal</c>
        /// is strictly greater than the snapshot's last-consumed ordinal. Signals equal-or-less
        /// are already baked into the seed state and must never be re-applied.
        /// </summary>
        private static IReadOnlyList<DecisionSignal> CollectSignalLogTailAfter(
            SignalLogWriter signalLog, long lastConsumedOrdinal)
        {
            var all = signalLog.ReadAll();
            var tail = new List<DecisionSignal>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].SessionSignalOrdinal > lastConsumedOrdinal)
                {
                    tail.Add(all[i]);
                }
            }
            return tail;
        }

        /// <summary>Reads the reason text from a quarantine marker; returns a fallback on any error.</summary>
        private static string TryReadMarkerReason(string markerPath)
        {
            try { return File.ReadAllText(markerPath, System.Text.Encoding.UTF8); }
            catch { return "unknown (marker unreadable)"; }
        }
    }
}
