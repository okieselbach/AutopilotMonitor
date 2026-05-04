using System;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// Maps a classifier id to the hypothesis slot in <see cref="DecisionState"/> that holds
    /// its last verdict. Plan §2.4 anti-loop — the <c>InputHash</c> of the last verdict is
    /// compared against the new snapshot hash to decide whether to skip a re-run.
    /// <para>
    /// Shared between the harness (<c>ClassifierAwareReplayHarness</c>) and the production
    /// <c>EffectRunner</c> so both use identical anti-loop semantics. Adding a new classifier
    /// = one new case here.
    /// </para>
    /// </summary>
    public static class ClassifierVerdictLookup
    {
        /// <summary>
        /// Returns the <c>LastClassifierVerdictId</c> of the hypothesis corresponding to
        /// <paramref name="classifierId"/>, or <c>null</c> if the classifier id is unknown
        /// or the hypothesis hasn't been touched yet.
        /// </summary>
        public static string? LookupLastVerdictId(DecisionState state, string classifierId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (classifierId == null) throw new ArgumentNullException(nameof(classifierId));

            return classifierId switch
            {
                WhiteGloveSealingClassifier.ClassifierId => state.ClassifierOutcomes.WhiteGloveSealing.LastClassifierVerdictId,
                _ => null,
            };
        }
    }
}
