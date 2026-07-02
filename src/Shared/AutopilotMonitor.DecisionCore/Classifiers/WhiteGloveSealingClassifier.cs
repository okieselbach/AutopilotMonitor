using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// Scoring-based WhiteGlove Part 1 classifier. Plan §2.4 / §M3.3.
    /// <para>
    /// Scoring rules ported 1:1 from the retired V1 agent's <c>WhiteGloveClassifier</c>
    /// — same weights, same thresholds, same asymmetric-conservative decision rule. The
    /// refactor change is the <see cref="IClassifier"/> contract, the snapshot input type,
    /// and the deterministic <see cref="WhiteGloveSealingSnapshot.ComputeInputHash"/> anti-loop.
    /// </para>
    /// <para>
    /// Asymmetric-conservative: only <see cref="HypothesisLevel.Confirmed"/> drives the
    /// WhiteGloveSealed stage. <see cref="HypothesisLevel.Weak"/> verdicts are recorded for
    /// observability but treated as NOT WhiteGlove — false-positive WG keeps the agent
    /// pending when it should exit; false-negative emits completion early and the later
    /// user sign-in triggers a new session (much lower cost).
    /// </para>
    /// </summary>
    public sealed class WhiteGloveSealingClassifier : IClassifier
    {
        public const string ClassifierId = "whiteglove-sealing";

        // Thresholds (tunable after production data — preserve Legacy baseline)
        internal const int HighThreshold = 70; // >= 70 -> Confirmed
        internal const int LowThreshold = 30;  // 30..69 -> Weak (observability only)

        // Positive-signal weights (port from Legacy)
        internal const int WeightShellCoreWhiteGloveSuccess = 80;
        internal const int WeightWhiteGloveSealingPattern = 40;
        internal const int WeightDeviceOnlyDeployment = 15;
        internal const int WeightSystemRebootObserved = 15;

        // Negative-signal weights (hard excluders)
        internal const int WeightAadJoinedWithUser = -100;
        internal const int WeightDesktopArrived = -100;
        internal const int WeightHelloResolved = -100;
        internal const int WeightAccountSetupActive = -40;

        public string Id => ClassifierId;
        public Type SnapshotType => typeof(WhiteGloveSealingSnapshot);

        public ClassifierVerdict Classify(object snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!(snapshot is WhiteGloveSealingSnapshot s))
            {
                throw new ArgumentException(
                    $"Expected {nameof(WhiteGloveSealingSnapshot)}, got {snapshot.GetType().Name}.",
                    nameof(snapshot));
            }

            var factors = new List<string>();
            int score = 0;

            // Hard excluders — any one flips the verdict to definitive NOT WhiteGlove.
            if (s.AadJoinedWithUser) { score += WeightAadJoinedWithUser; factors.Add($"aad_joined_with_user:{WeightAadJoinedWithUser:+#;-#;0}"); }
            if (s.DesktopArrived)    { score += WeightDesktopArrived;    factors.Add($"desktop_arrived:{WeightDesktopArrived:+#;-#;0}"); }
            if (s.HelloResolved)     { score += WeightHelloResolved;     factors.Add($"hello_resolved:{WeightHelloResolved:+#;-#;0}"); }
            if (s.HasAccountSetupActivity)
            {
                score += WeightAccountSetupActive;
                factors.Add($"account_setup_activity:{WeightAccountSetupActive:+#;-#;0}");
            }

            // Positive signals.
            if (s.ShellCoreWhiteGloveSuccessSeen)
            {
                score += WeightShellCoreWhiteGloveSuccess;
                factors.Add($"shellcore_wg_success:{WeightShellCoreWhiteGloveSuccess:+#;-#;0}");
            }
            if (s.WhiteGloveSealingPatternSeen)
            {
                score += WeightWhiteGloveSealingPattern;
                factors.Add($"sealing_pattern:{WeightWhiteGloveSealingPattern:+#;-#;0}");
            }
            if (s.IsDeviceOnlyDeploymentHypothesis)
            {
                score += WeightDeviceOnlyDeployment;
                factors.Add($"device_only_deployment:{WeightDeviceOnlyDeployment:+#;-#;0}");
            }
            if (s.SystemRebootUtc.HasValue)
            {
                score += WeightSystemRebootObserved;
                factors.Add($"system_reboot:{WeightSystemRebootObserved:+#;-#;0}");
            }

            // Cap to [0, 100].
            if (score < 0) score = 0;
            if (score > 100) score = 100;

            HypothesisLevel level;
            if (score >= HighThreshold)     level = HypothesisLevel.Confirmed;
            else if (score >= LowThreshold) level = HypothesisLevel.Weak;
            else                            level = HypothesisLevel.Rejected;

            return new ClassifierVerdict(
                classifierId: ClassifierId,
                level: level,
                score: score,
                contributingFactors: factors,
                reason: $"confidence={level} score={score}",
                inputHash: s.ComputeInputHash());
        }
    }
}
