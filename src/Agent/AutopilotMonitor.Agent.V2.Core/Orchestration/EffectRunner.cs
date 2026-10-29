#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Produktions-<see cref="IEffectRunner"/>. Plan §2.5 / §2.7b / L.17.
    /// <para>
    /// Dispatcht Effekte an ihre jeweiligen I/O-Kollaborateure; implementiert die drei
    /// Fehlerklassen (Transient / Kritisch / Optional) streng nach Plan-Vorgabe.
    /// </para>
    /// </summary>
    public sealed class EffectRunner : IEffectRunner
    {
        /// <summary>Plan L.17 — Transient-Retry-Backoffs (100/400/1600 ms).</summary>
        public static readonly IReadOnlyList<TimeSpan> DefaultRetryBackoffs = new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(1600),
        };

        private readonly IDeadlineScheduler _scheduler;
        private readonly IClassifierRegistry _classifiers;
        private readonly ISignalIngressSink _ingress;
        private readonly IEventTimelineEmitter _emitter;
        private readonly ISnapshotPersistence _snapshot;
        private readonly IClock _clock;
        private readonly IReadOnlyList<TimeSpan> _retryBackoffs;
        // PR3-D3: optional logger so transient retries, exhausted retries, critical failures,
        // anti-loop skips, and classifier exceptions are visible. Null in legacy unit tests.
        private readonly AgentLogger? _logger;

        public EffectRunner(
            IDeadlineScheduler scheduler,
            IClassifierRegistry classifiers,
            ISignalIngressSink ingress,
            IEventTimelineEmitter emitter,
            ISnapshotPersistence snapshot,
            IClock clock,
            IReadOnlyList<TimeSpan>? retryBackoffs = null,
            AgentLogger? logger = null)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _classifiers = classifiers ?? throw new ArgumentNullException(nameof(classifiers));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _retryBackoffs = retryBackoffs ?? DefaultRetryBackoffs;
            _logger = logger;
        }

        public async Task<EffectRunResult> RunAsync(
            IReadOnlyList<DecisionEffect> effects,
            DecisionState stateAfterReduce,
            DateTime stepOccurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (effects == null) throw new ArgumentNullException(nameof(effects));
            if (stateAfterReduce == null) throw new ArgumentNullException(nameof(stateAfterReduce));

            var failures = new List<EffectFailure>();
            int invocations = 0;
            int skipped = 0;

            foreach (var effect in effects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (effect.Kind)
                {
                    case DecisionEffectKind.ScheduleDeadline:
                    case DecisionEffectKind.CancelDeadline:
                        var criticalFail = TryRunCriticalDeadlineEffect(effect);
                        if (criticalFail != null)
                        {
                            failures.Add(criticalFail);
                            var abortReason = $"timer_infrastructure_failure: {criticalFail.ErrorReason}";

                            // Codex follow-up (post-#50 #B): responsibility for posting the
                            // synthetic EffectInfrastructureFailure signal moved to SignalIngress
                            // so it can DURABLY append + reduce inline within the same worker
                            // iteration, closing the crash-window between in-memory post and
                            // SignalLog persistence. EffectRunner just signals the abort here.
                            return new EffectRunResult(
                                sessionMustAbort: true,
                                abortReason: abortReason,
                                failures: failures,
                                classifierInvocations: invocations,
                                classifierSkippedByAntiLoop: skipped);
                        }
                        break;

                    case DecisionEffectKind.RunClassifier:
                        var classifierOutcome = RunOptionalClassifierEffect(effect, stateAfterReduce, stepOccurredAtUtc);
                        invocations += classifierOutcome.Invoked ? 1 : 0;
                        skipped += classifierOutcome.SkippedByAntiLoop ? 1 : 0;
                        if (classifierOutcome.Failure != null) failures.Add(classifierOutcome.Failure);
                        break;

                    case DecisionEffectKind.EmitEventTimelineEntry:
                        await RunTransientAsync(
                            effect,
                            () => _emitter.Emit(effect.Parameters, stateAfterReduce, stepOccurredAtUtc, effect.TypedPayload),
                            failures,
                            cancellationToken).ConfigureAwait(false);
                        break;

                    case DecisionEffectKind.PersistSnapshot:
                        await RunTransientAsync(
                            effect,
                            () => _snapshot.Save(stateAfterReduce),
                            failures,
                            cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new InvalidOperationException($"Unhandled DecisionEffectKind: {effect.Kind}");
                }
            }

            return new EffectRunResult(
                sessionMustAbort: false,
                abortReason: null,
                failures: failures,
                classifierInvocations: invocations,
                classifierSkippedByAntiLoop: skipped);
        }

        private EffectFailure? TryRunCriticalDeadlineEffect(DecisionEffect effect)
        {
            try
            {
                if (effect.Kind == DecisionEffectKind.ScheduleDeadline)
                {
                    if (effect.Deadline == null)
                    {
                        throw new InvalidOperationException("ScheduleDeadline effect missing Deadline payload.");
                    }
                    _scheduler.Schedule(effect.Deadline);
                }
                else
                {
                    if (string.IsNullOrEmpty(effect.CancelDeadlineName))
                    {
                        throw new InvalidOperationException("CancelDeadline effect missing CancelDeadlineName.");
                    }
                    _scheduler.Cancel(effect.CancelDeadlineName!);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Warning(
                    $"EffectRunner: CRITICAL effect {effect.Kind} failed — session must abort: {ex.GetType().Name}: {ex.Message}");
                return new EffectFailure(
                    effectKind: effect.Kind,
                    errorReason: $"{ex.GetType().Name}: {ex.Message}",
                    isTransient: false,
                    exhaustedRetries: false);
            }
        }

        private ClassifierEffectOutcome RunOptionalClassifierEffect(
            DecisionEffect effect,
            DecisionState stateAfterReduce,
            DateTime stepOccurredAtUtc)
        {
            if (string.IsNullOrEmpty(effect.ClassifierId))
            {
                return new ClassifierEffectOutcome(
                    false, false,
                    new EffectFailure(effect.Kind, "RunClassifier effect missing ClassifierId.", isTransient: false, exhaustedRetries: false));
            }

            if (!_classifiers.TryGet(effect.ClassifierId!, out var classifier) || classifier == null)
            {
                return new ClassifierEffectOutcome(
                    false, false,
                    new EffectFailure(effect.Kind, $"Classifier '{effect.ClassifierId}' not registered.", isTransient: false, exhaustedRetries: false));
            }

            var snapshot = effect.ClassifierSnapshot;
            if (snapshot == null)
            {
                return new ClassifierEffectOutcome(
                    false, false,
                    new EffectFailure(effect.Kind, $"RunClassifier '{effect.ClassifierId}' missing snapshot.", isTransient: false, exhaustedRetries: false));
            }

            // Anti-loop (§2.4): skip if snapshot-hash matches the last verdict for this classifier.
            var hashable = snapshot as IClassifierSnapshot;
            var snapshotHash = hashable?.ComputeInputHash() ?? string.Empty;
            var lastVerdictId = ClassifierVerdictLookup.LookupLastVerdictId(stateAfterReduce, effect.ClassifierId!);
            if (!string.IsNullOrEmpty(snapshotHash) &&
                !string.IsNullOrEmpty(lastVerdictId) &&
                snapshotHash == lastVerdictId)
            {
                _logger?.Trace(
                    $"EffectRunner: classifier {effect.ClassifierId} skipped (snapshotHash unchanged: {ShortHash(snapshotHash)})");
                return new ClassifierEffectOutcome(invoked: false, skippedByAntiLoop: true, failure: null);
            }

            ClassifierVerdict verdict;
            try
            {
                verdict = classifier.Classify(snapshot);
            }
            catch (Exception ex)
            {
                // Optional-class failure: emit an Inconclusive verdict instead of aborting.
                _logger?.Warning(
                    $"EffectRunner: classifier {effect.ClassifierId} threw -> emitting Inconclusive: {ex.GetType().Name}: {ex.Message}");
                var inconclusive = new ClassifierVerdict(
                    classifierId: effect.ClassifierId!,
                    level: HypothesisLevel.Inconclusive,
                    score: 0,
                    contributingFactors: Array.Empty<string>(),
                    reason: $"exception: {ex.GetType().Name}: {ex.Message}",
                    inputHash: string.IsNullOrEmpty(snapshotHash) ? $"inconclusive:{Guid.NewGuid():N}" : snapshotHash);
                PostClassifierVerdictSignal(inconclusive, effect.ClassifierId!, stepOccurredAtUtc);
                return new ClassifierEffectOutcome(
                    invoked: true,
                    skippedByAntiLoop: false,
                    failure: new EffectFailure(effect.Kind, $"{ex.GetType().Name}: {ex.Message}", isTransient: false, exhaustedRetries: false));
            }

            PostClassifierVerdictSignal(verdict, effect.ClassifierId!, stepOccurredAtUtc);
            return new ClassifierEffectOutcome(invoked: true, skippedByAntiLoop: false, failure: null);
        }

        private void PostClassifierVerdictSignal(ClassifierVerdict verdict, string classifierId, DateTime stepOccurredAtUtc)
        {
            _ingress.Post(
                kind: DecisionSignalKind.ClassifierVerdictIssued,
                occurredAtUtc: stepOccurredAtUtc,
                sourceOrigin: $"effectrunner:classifier:{classifierId}",
                evidence: new Evidence(
                    kind: EvidenceKind.Synthetic,
                    identifier: $"classifier:{classifierId}:{verdict.InputHash}",
                    summary: $"{classifierId}={verdict.Level}/{verdict.Score}"),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["classifier"] = verdict.ClassifierId,
                    ["level"] = verdict.Level.ToString(),
                    ["score"] = verdict.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reason"] = verdict.Reason,
                    ["inputHash"] = verdict.InputHash,
                });
        }

        private async Task RunTransientAsync(
            DecisionEffect effect,
            Action action,
            List<EffectFailure> failures,
            CancellationToken cancellationToken)
        {
            string? lastErr = null;

            for (int attempt = 0; attempt <= _retryBackoffs.Count; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    action();
                    return;  // success
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastErr = $"{ex.GetType().Name}: {ex.Message}";
                    if (attempt < _retryBackoffs.Count)
                    {
                        var backoff = _retryBackoffs[attempt];
                        _logger?.Debug(
                            $"EffectRunner: effect {effect.Kind} attempt {attempt + 1}/{_retryBackoffs.Count + 1} failed -> retry in {backoff.TotalMilliseconds:F0}ms: {lastErr}");
                        await _clock.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            _logger?.Warning(
                $"EffectRunner: TRANSIENT effect {effect.Kind} exhausted after {_retryBackoffs.Count + 1} attempts: {lastErr ?? "unknown transient error"}");

            failures.Add(new EffectFailure(
                effect.Kind,
                lastErr ?? "unknown transient error",
                isTransient: true,
                exhaustedRetries: true));
        }

        // PR3-D3: short hash for log readability — full SHA in the verdict already.
        private static string ShortHash(string h) => string.IsNullOrEmpty(h) ? "(empty)" : (h.Length <= 8 ? h : h.Substring(0, 8));

        private readonly struct ClassifierEffectOutcome
        {
            public ClassifierEffectOutcome(bool invoked, bool skippedByAntiLoop, EffectFailure? failure)
            {
                Invoked = invoked;
                SkippedByAntiLoop = skippedByAntiLoop;
                Failure = failure;
            }

            public bool Invoked { get; }
            public bool SkippedByAntiLoop { get; }
            public EffectFailure? Failure { get; }
        }
    }
}
