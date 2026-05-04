using System;

namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// Kernel classifier service. Plan §2.4 / L.8.
    /// <para>
    /// Implementations are pure: <c>Classify(snapshot) → ClassifierVerdict</c>.
    /// Anti-loop via <see cref="ClassifierVerdict.InputHash"/> — the reducer does not re-invoke
    /// a classifier whose last verdict matches the current snapshot hash.
    /// </para>
    /// <para>
    /// First implementation shipped in M3: <c>WhiteGloveSealingClassifier</c>.
    /// Future (v11.1+): <c>EnrollmentFailureCauseClassifier</c>, <c>AppInstallOutcomeClassifier</c>.
    /// </para>
    /// </summary>
    public interface IClassifier
    {
        /// <summary>Stable identifier used in verdict records and journal references.</summary>
        string Id { get; }

        /// <summary>Concrete snapshot type the classifier expects. Enforced by the reducer.</summary>
        Type SnapshotType { get; }

        /// <summary>Run the classifier against a snapshot.</summary>
        ClassifierVerdict Classify(object snapshot);
    }
}
