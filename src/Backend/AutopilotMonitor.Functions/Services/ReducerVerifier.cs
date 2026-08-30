using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Structural + semantic verification of a session's persisted SignalLog +
    /// DecisionTransitions journal. Pure function — takes loaded records, produces a
    /// report. No IO, no DI.
    /// <para>
    /// <b>Structural checks</b> (always run): ordinal contiguity, step-index contiguity,
    /// ReducerVersion drift, orphaned <c>SignalOrdinalRef</c>.
    /// </para>
    /// <para>
    /// <b>Semantic replay</b> (Codex follow-up #6, added when preconditions hold):
    /// deserialise <see cref="SignalRecord.PayloadJson"/> into <see cref="DecisionSignal"/>
    /// instances, fold them through the live backend <see cref="DecisionEngine"/>, and
    /// compare the produced transitions to the stored journal on the semantic fields
    /// (Trigger / FromStage / ToStage / Taken / DeadEndReason / StepIndex). The agent-side
    /// journal is the agent's reducer-chain; this replay verifies the backend's reducer
    /// agrees — divergence under the same ReducerVersion is a hard release-gate bug.
    /// </para>
    /// </summary>
    internal static class ReducerVerifier
    {
        /// <summary>Cap for individual replay-divergence issues, mirrors the gap caps.</summary>
        private const int MaxReplayDivergenceIssues = 20;

        public static ReducerVerificationReport Verify(
            string tenantId,
            string sessionId,
            IReadOnlyList<SignalRecord> signals,
            IReadOnlyList<DecisionTransitionRecord> transitions,
            string currentReducerVersion)
        {
            var report = new ReducerVerificationReport
            {
                TenantId              = tenantId,
                SessionId             = sessionId,
                SignalCount           = signals?.Count ?? 0,
                TransitionCount       = transitions?.Count ?? 0,
                CurrentReducerVersion = currentReducerVersion ?? string.Empty,
                Issues                = new List<VerificationIssue>(),
            };

            if ((signals == null || signals.Count == 0) && (transitions == null || transitions.Count == 0))
            {
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Info",
                    Kind     = "empty_session",
                    Message  = "Session has no signals or transitions persisted.",
                });
                report.SemanticReplaySkipReason = "empty_session";
                return report;
            }

            // ---- Signal ordinal contiguity ---------------------------------------------
            if (signals != null && signals.Count > 0)
            {
                var ordered = signals.OrderBy(s => s.SessionSignalOrdinal).ToList();
                report.SignalOrdinalFirst = ordered[0].SessionSignalOrdinal;
                report.SignalOrdinalLast  = ordered[ordered.Count - 1].SessionSignalOrdinal;

                var gaps = new List<(long Prev, long Next)>();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1].SessionSignalOrdinal;
                    var next = ordered[i].SessionSignalOrdinal;
                    if (next != prev + 1) gaps.Add((prev, next));
                }
                report.SignalOrdinalsContiguous = gaps.Count == 0;

                // Report gaps individually up to a cap — an attacker could fill the report
                // with thousands of issues otherwise.
                const int maxGapIssues = 20;
                foreach (var (prev, next) in gaps.Take(maxGapIssues))
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "signal_ordinal_gap",
                        Message  = $"Signal ordinal jumps from {prev} to {next} (missing {next - prev - 1} ordinal(s)).",
                    });
                }
                if (gaps.Count > maxGapIssues)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "signal_ordinal_gap",
                        Message  = $"... {gaps.Count - maxGapIssues} additional ordinal gaps not listed.",
                    });
                }
            }

            // ---- Transition step-index contiguity --------------------------------------
            if (transitions != null && transitions.Count > 0)
            {
                var ordered = transitions.OrderBy(t => t.StepIndex).ToList();
                report.StepIndexFirst = ordered[0].StepIndex;
                report.StepIndexLast  = ordered[ordered.Count - 1].StepIndex;

                var gaps = new List<(int Prev, int Next)>();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1].StepIndex;
                    var next = ordered[i].StepIndex;
                    if (next != prev + 1) gaps.Add((prev, next));
                }
                report.StepIndicesContiguous = gaps.Count == 0;

                const int maxGapIssues = 20;
                foreach (var (prev, next) in gaps.Take(maxGapIssues))
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "step_index_gap",
                        Message  = $"Transition StepIndex jumps from {prev} to {next} (missing {next - prev - 1} step(s)).",
                    });
                }
                if (gaps.Count > maxGapIssues)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "step_index_gap",
                        Message  = $"... {gaps.Count - maxGapIssues} additional step-index gaps not listed.",
                    });
                }

                // StoredReducerVersion + drift check (first transition is representative; the
                // agent-side Journal contract guarantees per-session consistency).
                report.StoredReducerVersion = ordered[0].ReducerVersion;
                report.ReducerVersionDrift = !string.IsNullOrEmpty(report.CurrentReducerVersion) &&
                                             !string.IsNullOrEmpty(report.StoredReducerVersion) &&
                                             !string.Equals(report.StoredReducerVersion, report.CurrentReducerVersion, StringComparison.Ordinal);
                if (report.ReducerVersionDrift)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Info",
                        Kind     = "reducer_version_drift",
                        Message  = $"Stored ReducerVersion {report.StoredReducerVersion} differs from current {report.CurrentReducerVersion}. " +
                                   "Session was journaled under an older reducer build — structural checks still valid, but " +
                                   "a replay would not reproduce the stored transitions exactly.",
                    });
                }

                // ---- Cross-reference: SignalOrdinalRef → existing signal --------------
                if (signals != null && signals.Count > 0)
                {
                    var signalOrdinals = new HashSet<long>(signals.Select(s => s.SessionSignalOrdinal));
                    var orphanCount = 0;
                    const int maxOrphanIssues = 20;

                    foreach (var t in ordered)
                    {
                        if (!signalOrdinals.Contains(t.SignalOrdinalRef))
                        {
                            orphanCount++;
                            if (orphanCount <= maxOrphanIssues)
                            {
                                report.Issues.Add(new VerificationIssue
                                {
                                    Severity = "Warning",
                                    Kind     = "orphaned_transition",
                                    Message  = $"Transition StepIndex={t.StepIndex} references SignalOrdinal={t.SignalOrdinalRef} which is not in the loaded signal set.",
                                });
                            }
                        }
                    }
                    if (orphanCount > maxOrphanIssues)
                    {
                        report.Issues.Add(new VerificationIssue
                        {
                            Severity = "Warning",
                            Kind     = "orphaned_transition",
                            Message  = $"... {orphanCount - maxOrphanIssues} additional orphaned transitions not listed.",
                        });
                    }
                    report.OrphanedTransitionCount = orphanCount;
                }
            }

            // ---- Semantic replay (Codex follow-up #6) ------------------------------------
            RunSemanticReplay(signals, transitions, report);

            return report;
        }

        /// <summary>
        /// Folds the persisted <paramref name="signals"/> stream through a transient
        /// <see cref="DecisionEngine"/> instance and compares the produced transitions to
        /// the journal. Skips when preconditions (contiguity, ReducerVersion agreement) are
        /// not met — divergence under those conditions is not a reducer bug but expected
        /// drift.
        /// </summary>
        private static void RunSemanticReplay(
            IReadOnlyList<SignalRecord>? signals,
            IReadOnlyList<DecisionTransitionRecord>? transitions,
            ReducerVerificationReport report)
        {
            if (signals == null || signals.Count == 0 || transitions == null || transitions.Count == 0)
            {
                // Empty-session skip already recorded above.
                if (string.IsNullOrEmpty(report.SemanticReplaySkipReason))
                {
                    report.SemanticReplaySkipReason = "empty_session";
                }
                return;
            }

            if (report.ReducerVersionDrift)
            {
                report.SemanticReplaySkipReason = "reducer_version_drift";
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Info",
                    Kind     = "replay_skipped",
                    Message  = "Semantic replay skipped because stored ReducerVersion differs from current — replay against a different reducer build is not meaningful.",
                });
                return;
            }

            if (!report.SignalOrdinalsContiguous)
            {
                report.SemanticReplaySkipReason = "non_contiguous_signal_ordinals";
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Info",
                    Kind     = "replay_skipped",
                    Message  = "Semantic replay skipped because the signal stream has ordinal gaps; the structural warnings above are the actionable signal.",
                });
                return;
            }

            if (!report.StepIndicesContiguous)
            {
                report.SemanticReplaySkipReason = "non_contiguous_step_indices";
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Info",
                    Kind     = "replay_skipped",
                    Message  = "Semantic replay skipped because the transition journal has StepIndex gaps.",
                });
                return;
            }

            var orderedSignals = signals.OrderBy(s => s.SessionSignalOrdinal).ToList();
            var orderedTransitions = transitions.OrderBy(t => t.StepIndex).ToList();

            // A malformed blob at the head means the replay cannot start at all. Silent skip —
            // PayloadJson stubs (common in structural tests) should not pollute the issue list;
            // the SemanticReplaySkipReason field carries the diagnostic for real operators. This
            // check runs BEFORE the journal is decoded so stubbed journals stay silent too.
            DecisionSignal headSignal;
            try
            {
                headSignal = SignalSerializer.Deserialize(orderedSignals[0].PayloadJson);
            }
            catch
            {
                report.SemanticReplaySkipReason = "deserialization_failure";
                return;
            }

            // The journal is decoded up-front — we need the semantic fields directly, and
            // PayloadJson round-trips them via TransitionSerializer. The typed columns on the
            // record are projections; the JSON is authoritative.
            var decodedTransitions = new List<DecisionTransition>(orderedTransitions.Count);
            for (var i = 0; i < orderedTransitions.Count; i++)
            {
                try
                {
                    decodedTransitions.Add(TransitionSerializer.Deserialize(orderedTransitions[i].PayloadJson));
                }
                catch (Exception ex)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "replay_deserialization_error",
                        Message  = $"Transition at StepIndex {orderedTransitions[i].StepIndex} PayloadJson failed to deserialise — excluded from comparison: {ex.GetType().Name}: {ex.Message}",
                    });
                }
            }

            if (decodedTransitions.Count == 0)
            {
                report.SemanticReplaySkipReason = "deserialization_failure";
                return;
            }

            var engine = new DecisionEngine();
            // Codex follow-up (post-#50 #D): DecisionState.CreateInitial signature is
            // (sessionId, tenantId); callers must pass them in that order so a replay seed
            // carries the correct identity end-to-end.
            var state = DecisionState.CreateInitial(report.SessionId, report.TenantId);
            var divergences = 0;

            // Signals are decoded one at a time and dropped after the fold (decode-fold-discard):
            // device-uploaded PayloadJson is attacker-shaped and chunk-stored up to the ~1 MB
            // entity limit, so materialising all 5000 decoded payloads at once would let a single
            // poisoned session pin gigabytes on the worker. Peak memory is now one decoded signal
            // plus the state. A mid-stream failure truncates the replay there (the divergence
            // tail then surfaces as a transition-count mismatch naturally).
            var decodedSignalCount = 0;
            for (var i = 0; i < orderedSignals.Count; i++)
            {
                DecisionSignal signal;
                try
                {
                    signal = i == 0 ? headSignal : SignalSerializer.Deserialize(orderedSignals[i].PayloadJson);
                }
                catch (Exception ex)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "replay_deserialization_error",
                        Message  = $"Signal at ordinal {orderedSignals[i].SessionSignalOrdinal} PayloadJson failed to deserialise; replay truncated here: {ex.GetType().Name}: {ex.Message}",
                    });
                    break;
                }

                decodedSignalCount++;
                if (i >= decodedTransitions.Count)
                {
                    // No stored transition to compare against — the signal still counts toward
                    // the signal/transition imbalance reported below, but is not folded.
                    continue;
                }

                var step = engine.Reduce(state, signal);
                state = step.NewState;
                var replayed = step.Transition;
                var stored = decodedTransitions[i];

                if (!TransitionsSemanticallyEqual(replayed, stored))
                {
                    divergences++;
                    if (divergences <= MaxReplayDivergenceIssues)
                    {
                        report.Issues.Add(new VerificationIssue
                        {
                            Severity = "Warning",
                            Kind     = "replay_divergence",
                            Message  =
                                $"Replay vs. stored diverge at StepIndex={stored.StepIndex}: " +
                                $"stored=[Trigger={stored.Trigger}, {stored.FromStage}→{stored.ToStage}, Taken={stored.Taken}, DeadEnd={stored.DeadEndReason ?? "<null>"}] " +
                                $"replayed=[Trigger={replayed.Trigger}, {replayed.FromStage}→{replayed.ToStage}, Taken={replayed.Taken}, DeadEnd={replayed.DeadEndReason ?? "<null>"}].",
                        });
                    }
                }
            }
            if (divergences > MaxReplayDivergenceIssues)
            {
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Warning",
                    Kind     = "replay_divergence",
                    Message  = $"... {divergences - MaxReplayDivergenceIssues} additional replay divergences not listed.",
                });
            }

            // Count imbalance between signals and stored transitions is itself a divergence:
            // every signal should produce exactly one transition (taken or dead-end).
            if (decodedSignalCount != decodedTransitions.Count)
            {
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Warning",
                    Kind     = "replay_divergence",
                    Message  = $"Signal count ({decodedSignalCount}) != transition count ({decodedTransitions.Count}); reducer should produce one transition per signal.",
                });
            }

            // Codex follow-up (post-#50 #D): defensive identity assertion. The final state
            // must still carry the same SessionId/TenantId as the report — a drift here
            // signals a swapped CreateInitial(...) seed upstream, which would otherwise slip
            // through because TransitionsSemanticallyEqual is identity-insensitive.
            if (!string.Equals(state.SessionId, report.SessionId, StringComparison.Ordinal)
                || !string.Equals(state.TenantId, report.TenantId, StringComparison.Ordinal))
            {
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Error",
                    Kind     = "replay_identity_mismatch",
                    Message  = $"Replay seed identity drift: final state (session={state.SessionId}, tenant={state.TenantId}) != report (session={report.SessionId}, tenant={report.TenantId}).",
                });
            }

            // Final-stage comparison against the last stored transition's ToStage.
            var lastStoredToStage = orderedTransitions[orderedTransitions.Count - 1].ToStage;
            var replayedStageName = state.Stage.ToString();
            report.ReplayedFinalStage = replayedStageName;
            report.SemanticReplayFinalStageMatches = string.Equals(
                replayedStageName, lastStoredToStage, StringComparison.Ordinal);
            if (!report.SemanticReplayFinalStageMatches)
            {
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Error",
                    Kind     = "replay_final_stage_mismatch",
                    Message  = $"Replay arrived at final stage '{replayedStageName}' but the last stored transition's ToStage was '{lastStoredToStage}'.",
                });
            }

            report.SemanticReplayPerformed = true;
            report.TransitionDivergenceCount = divergences;
        }

        private static bool TransitionsSemanticallyEqual(DecisionTransition a, DecisionTransition b)
        {
            if (a.StepIndex != b.StepIndex) return false;
            if (!string.Equals(a.Trigger, b.Trigger, StringComparison.Ordinal)) return false;
            if (a.FromStage != b.FromStage) return false;
            if (a.ToStage != b.ToStage) return false;
            if (a.Taken != b.Taken) return false;
            if (!string.Equals(a.DeadEndReason ?? string.Empty, b.DeadEndReason ?? string.Empty, StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
