using System;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Container for the reducer-side classifier verdict + anti-loop state. Codex follow-up #5 —
    /// replaces the legacy <c>WhiteGloveSealing</c> / <c>DeviceOnlyDeployment</c>
    /// <see cref="Hypothesis"/> fields on <c>DecisionState</c> with a single aggregate. Scoring
    /// logic, weights and thresholds live in the classifier implementations
    /// (<c>WhiteGloveSealingClassifier</c>) and are not touched by this refactor — only the
    /// verdict storage moved.
    /// <para>
    /// <b>Invariants</b>:
    /// <list type="bullet">
    ///   <item>Both <see cref="Hypothesis"/> values are non-null; <see cref="Empty"/> uses <see cref="Hypothesis.UnknownInstance"/>.</item>
    ///   <item>Immutable; the <c>With…</c> methods return new instances.</item>
    ///   <item><see cref="DeviceOnlyDeployment"/> has no dedicated classifier — it's updated by the reducer (<c>DeviceSetupProvisioningComplete</c> handler + <c>DeviceOnlyEspDetection</c> deadline) but is modeled here for symmetry, because the WhiteGlove classifier reads it as an input.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class ClassifierOutcomes
    {
        public static readonly ClassifierOutcomes Empty = new ClassifierOutcomes(
            whiteGloveSealing: Hypothesis.UnknownInstance,
            deviceOnlyDeployment: Hypothesis.UnknownInstance);

        public ClassifierOutcomes(
            Hypothesis whiteGloveSealing,
            Hypothesis deviceOnlyDeployment)
        {
            WhiteGloveSealing = whiteGloveSealing ?? throw new ArgumentNullException(nameof(whiteGloveSealing));
            DeviceOnlyDeployment = deviceOnlyDeployment ?? throw new ArgumentNullException(nameof(deviceOnlyDeployment));
        }

        /// <summary>Verdict from <c>WhiteGloveSealingClassifier</c>. Plan §2.4.</summary>
        public Hypothesis WhiteGloveSealing { get; }

        /// <summary>
        /// Reducer-maintained (no dedicated classifier) device-only hypothesis. Inputs into the
        /// WhiteGlove sealing snapshot as <c>IsDeviceOnlyDeploymentHypothesis</c>.
        /// </summary>
        public Hypothesis DeviceOnlyDeployment { get; }

        public ClassifierOutcomes WithWhiteGloveSealing(Hypothesis value) =>
            new ClassifierOutcomes(
                value ?? throw new ArgumentNullException(nameof(value)),
                DeviceOnlyDeployment);

        public ClassifierOutcomes WithDeviceOnlyDeployment(Hypothesis value) =>
            new ClassifierOutcomes(
                WhiteGloveSealing,
                value ?? throw new ArgumentNullException(nameof(value)));
    }
}
