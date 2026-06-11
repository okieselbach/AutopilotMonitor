using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Uniform structured audit-trail payload for terminal / state-changing
    /// <see cref="DecisionEffectKind.EmitEventTimelineEntry"/> effects emitted by the
    /// <see cref="DecisionEngine"/>. Replaces the per-emission-site ad-hoc payload shapes
    /// that previously left events like <c>whiteglove_complete</c> with empty
    /// <c>EnrollmentEvent.Data</c> on the wire.
    /// <para>
    /// The output is meant to be passed verbatim as <see cref="DecisionEffect.TypedPayload"/>;
    /// <see cref="Telemetry"/> emitters (V2 <c>EventTimelineEmitter</c>) hand the dictionary
    /// directly to <see cref="EnrollmentEvent.Data"/>. Field ordering uses
    /// <see cref="StringComparer.Ordinal"/> so on-disk Snapshot test outputs are stable.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Contract for callers:</b>
    /// <list type="bullet">
    ///   <item><paramref name="postState"/> is the state AFTER the reduce step that emits the
    ///         event. <c>StepIndex</c> and <c>LastAppliedSignalOrdinal</c> on it correspond to
    ///         the emitting transition's StepIndex and the signal that drove the reduce.</item>
    ///   <item><paramref name="decidedStage"/> is what the caller is committing to — usually
    ///         equals <c>postState.Stage</c>; passed explicitly so the field reflects the
    ///         decision's outcome even if the caller still routes through an intermediate
    ///         stage.</item>
    ///   <item><paramref name="trigger"/> mirrors <see cref="DecisionTransition.Trigger"/> so a
    ///         consumer can correlate the audit-trail event with the journal transition without
    ///         joining tables (e.g. <c>"DeadlineFired:FinalizingGrace"</c>,
    ///         <c>"ClassifierVerdictIssued:WhiteGloveSealing:Confirmed"</c>).</item>
    /// </list>
    /// </remarks>
    internal static class DecisionAuditTrailBuilder
    {
        /// <summary>
        /// Stable string the audit trail uses for the <c>decisionSource</c> field. Mirrors the
        /// V2 emitter default (<c>EventTimelineEmitter.SourceId</c>) — keeps backend filters
        /// like <c>Source == "DecisionEngine"</c> uniform across event-type and audit-trail.
        /// </summary>
        public const string DecisionSource = "DecisionEngine";

        /// <summary>
        /// Build the audit-trail payload. All optional parameters default to off — the
        /// resulting dictionary always contains the mandatory anchors (<c>decisionSource</c>,
        /// <c>trigger</c>, <c>sessionStage</c>, <c>stepIndex</c>, <c>signalsSeen</c>,
        /// <c>signalEvidence</c>, <c>signalTimestamps</c>, <c>scenario</c>) and adds the
        /// classifier / failure / context fields only when the corresponding optional argument
        /// or state fact is present.
        /// </summary>
        /// <param name="postState">State produced by the reducer step (post-transition).</param>
        /// <param name="decidedStage">Decision outcome stage to report (often == <c>postState.Stage</c>).</param>
        /// <param name="trigger">The same <see cref="DecisionTransition.Trigger"/> string the reducer attached to the transition.</param>
        /// <param name="failureReason">Optional human-readable failure reason — surfaced as <c>reason</c> when set.</param>
        /// <param name="classifier">Optional classifier verdict (id + level + score + reason + inputHash).</param>
        /// <param name="classifierInputs">Optional classifier-input snapshot (e.g. <see cref="Classifiers.WhiteGloveSealingSnapshot"/>) — flattened to a dict via <see cref="FlattenClassifierInputs"/>.</param>
        public static Dictionary<string, object> Build(
            DecisionState postState,
            SessionStage decidedStage,
            string trigger,
            string? failureReason = null,
            ClassifierVerdictInfo? classifier = null,
            object? classifierInputs = null)
        {
            if (postState == null) throw new ArgumentNullException(nameof(postState));
            if (string.IsNullOrEmpty(trigger)) throw new ArgumentException("Trigger is mandatory.", nameof(trigger));

            // Capacity sized for 8 mandatory anchors + up to ~8 optional fields (classifier,
            // classifierInputs, reason, helloOutcome, helloPolicyEnabled, imePatternMatched,
            // signalOrdinal). Avoids the default 0→3→7→17 resize cascade on every transition.
            var data = new Dictionary<string, object>(capacity: 16, StringComparer.Ordinal)
            {
                ["decisionSource"] = DecisionSource,
                ["trigger"] = trigger,
                ["sessionStage"] = decidedStage.ToString(),
                ["stepIndex"] = postState.StepIndex,
            };

            if (postState.LastAppliedSignalOrdinal >= 0)
            {
                data["signalOrdinal"] = postState.LastAppliedSignalOrdinal;
            }

            // Signal census — both a flat list (cheap rendering / KQL `has` filter) and a
            // structured evidence map (ordinal + UTC for the Inspector's "jump to signal").
            // Shared with the V2 agent's FinalStatusBuilder via DecisionStateSignalCensus so
            // the on-disk final-status.json and the on-the-wire audit trail use identical
            // signal-naming rules.
            var census = DecisionStateSignalCensus.Build(postState);
            data["signalsSeen"] = census.SignalsSeen;
            data["signalEvidence"] = census.SignalEvidence;
            // signalTimestamps mirrors the legacy V2-parity field name (FinalizingStageTests
            // pre-existing assertion) — kept verbatim so audit-trail is a strict superset of
            // what enrollment_complete already published.
            data["signalTimestamps"] = census.SignalTimestamps;

            // Scenario block — single source of truth for "what kind of enrollment was this".
            data["scenario"] = BuildScenarioBlock(postState);

            if (classifier != null)
            {
                data["classifier"] = classifier.ToDictionary();
            }

            if (classifierInputs != null)
            {
                data["classifierInputs"] = FlattenClassifierInputs(classifierInputs);
            }

            if (!string.IsNullOrEmpty(failureReason))
            {
                data["reason"] = failureReason!;
            }

            // Hello / IME context — present in the data only when actually known so the
            // payload doesn't mis-suggest "Hello observed = false" when it's just absent.
            if (postState.HelloOutcome != null)
            {
                data["helloOutcome"] = postState.HelloOutcome.Value;
            }
            if (postState.HelloPolicyEnabled != null)
            {
                data["helloPolicyEnabled"] = postState.HelloPolicyEnabled.Value;
            }
            if (postState.ImeMatchedPatternId != null)
            {
                data["imePatternMatchedPatternId"] = postState.ImeMatchedPatternId.Value;
            }

            if (postState.RealmJoinFacts.DetectedUtc != null)
            {
                data["realmjoinDetectedUtc"] = FormatUtc(postState.RealmJoinFacts.DetectedUtc.Value);
                data["realmjoinOutcome"] = postState.RealmJoinFacts.Outcome?.Value ?? "Pending";
                if (postState.RealmJoinFacts.LastDeploymentPhase != null)
                {
                    data["realmjoinLastPhase"] = postState.RealmJoinFacts.LastDeploymentPhase.Value;
                }
                if (postState.RealmJoinFacts.ResolvedUtc != null)
                {
                    data["realmjoinResolvedUtc"] = FormatUtc(postState.RealmJoinFacts.ResolvedUtc.Value);
                }
                if (postState.RealmJoinFacts.ProductVersion != null)
                {
                    data["realmjoinProductVersion"] = postState.RealmJoinFacts.ProductVersion.Value;
                }
                if (postState.RealmJoinFacts.ReleaseChannel != null)
                {
                    data["realmjoinReleaseChannel"] = postState.RealmJoinFacts.ReleaseChannel.Value;
                }
                data["realmjoinPackages"] = BuildRealmJoinPackageSummary(postState.RealmJoinFacts.Packages);
            }

            return data;
        }

        /// <summary>
        /// Build a compact per-package summary list for the <c>realmjoinPackages</c> audit-trail
        /// field. Each entry carries the identifiers + completion state but omits the per-package
        /// timestamps to keep the payload size predictable.
        /// </summary>
        private static List<Dictionary<string, object>> BuildRealmJoinPackageSummary(
            IReadOnlyList<State.RealmJoinPackageFact> packages)
        {
            var result = new List<Dictionary<string, object>>(packages.Count);
            for (var i = 0; i < packages.Count; i++)
            {
                var p = packages[i];
                var row = new Dictionary<string, object>(capacity: 6, StringComparer.Ordinal)
                {
                    ["packageId"] = p.PackageId,
                    ["displayName"] = p.DisplayName,
                    ["scope"] = p.Scope,
                };
                if (p.Version != null)
                {
                    row["version"] = p.Version;
                }
                if (p.CompletedUtc != null)
                {
                    row["completedUtc"] = FormatUtc(p.CompletedUtc.Value);
                    row["durationSeconds"] = (int)Math.Max(0, (p.CompletedUtc.Value - p.StartedUtc).TotalSeconds);
                }
                if (p.Success != null)
                {
                    row["success"] = p.Success.Value;
                }
                if (p.LastExitCode != null)
                {
                    row["lastExitCode"] = p.LastExitCode.Value;
                }
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// Shorthand to wrap a classifier-verdict signal payload (the parameter dict carried by
        /// <see cref="Signals.DecisionSignalKind.ClassifierVerdictIssued"/>) into a typed
        /// <see cref="ClassifierVerdictInfo"/>. Returns <c>null</c> if the payload is missing
        /// the mandatory <c>classifier</c> + <c>level</c> keys — caller treats null as "no
        /// classifier section in the audit trail" rather than synthesising fake values.
        /// </summary>
        public static ClassifierVerdictInfo? VerdictFromSignalPayload(IReadOnlyDictionary<string, string>? payload)
        {
            if (payload == null) return null;
            if (!payload.TryGetValue("classifier", out var id) || string.IsNullOrEmpty(id)) return null;
            if (!payload.TryGetValue("level", out var levelRaw) || string.IsNullOrEmpty(levelRaw)) return null;

            int score = 0;
            if (payload.TryGetValue("score", out var scoreRaw))
            {
                int.TryParse(scoreRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out score);
            }
            var reason = payload.TryGetValue("reason", out var r) ? r : id;
            var inputHash = payload.TryGetValue("inputHash", out var h) ? h : string.Empty;

            return new ClassifierVerdictInfo(id, levelRaw, score, reason, inputHash);
        }

        // --- helpers ------------------------------------------------------------------------

        private static Dictionary<string, object> BuildScenarioBlock(DecisionState s)
        {
            var block = new Dictionary<string, object>(capacity: 6, StringComparer.Ordinal);
            if (s.ScenarioProfile != null)
            {
                block["mode"] = s.ScenarioProfile.Mode.ToString();
                block["joinMode"] = s.ScenarioProfile.JoinMode.ToString();
                block["espConfig"] = s.ScenarioProfile.EspConfig.ToString();
                block["preProvisioningSide"] = s.ScenarioProfile.PreProvisioningSide.ToString();
                block["confidence"] = s.ScenarioProfile.Confidence.ToString();
            }
            if (s.CurrentEnrollmentPhase != null)
            {
                block["currentPhase"] = s.CurrentEnrollmentPhase.Value.ToString();
            }
            return block;
        }

        /// <summary>
        /// Reflect the public properties of a classifier-input snapshot record into a flat
        /// <c>Dictionary&lt;string, object&gt;</c>. Keeps the audit trail self-describing
        /// without coupling to a specific snapshot type — works for both
        /// <see cref="Classifiers.WhiteGloveSealingSnapshot"/> and the future Part-2 snapshot.
        /// Property names are camelCased so the JSON-on-the-wire matches the surrounding
        /// audit-trail style.
        /// </summary>
        private static Dictionary<string, object> FlattenClassifierInputs(object snapshot)
        {
            var result = new Dictionary<string, object>(capacity: 8, StringComparer.Ordinal);
            foreach (var prop in snapshot.GetType().GetProperties())
            {
                if (!prop.CanRead) continue;
                var value = prop.GetValue(snapshot);
                if (value == null) continue;
                var key = ToCamelCase(prop.Name);
                if (value is DateTime dt)
                {
                    result[key] = FormatUtc(dt);
                }
                else if (value is Enum)
                {
                    result[key] = value.ToString()!;
                }
                else
                {
                    result[key] = value;
                }
            }
            return result;
        }

        private static string FormatUtc(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                .ToString("o", CultureInfo.InvariantCulture);

        private static string ToCamelCase(string s)
        {
            if (string.IsNullOrEmpty(s) || char.IsLower(s[0])) return s;
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }
    }

    /// <summary>
    /// Immutable record of a classifier verdict consumed by
    /// <see cref="DecisionAuditTrailBuilder.Build"/>. Lifted out of the legacy parameter-bag
    /// shape so callers cannot accidentally drop a key on the audit trail.
    /// </summary>
    internal sealed class ClassifierVerdictInfo
    {
        public ClassifierVerdictInfo(string id, string level, int score, string reason, string inputHash)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Level = level ?? throw new ArgumentNullException(nameof(level));
            Score = score;
            Reason = reason ?? string.Empty;
            InputHash = inputHash ?? string.Empty;
        }

        public string Id { get; }
        public string Level { get; }
        public int Score { get; }
        public string Reason { get; }
        public string InputHash { get; }

        public Dictionary<string, object> ToDictionary() =>
            new Dictionary<string, object>(capacity: 5, StringComparer.Ordinal)
            {
                ["id"] = Id,
                ["level"] = Level,
                ["score"] = Score,
                ["reason"] = Reason,
                ["inputHash"] = InputHash,
            };
    }
}
