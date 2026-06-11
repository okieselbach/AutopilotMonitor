using System;
using System.Collections.Generic;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Aggregated facts about a RealmJoin (RJ) deployment observed during a session.
    /// Drives the V2 completion-gate extension: when RJ is detected the AND-gate keeps
    /// the session non-terminal until either <see cref="DeploymentPhase.CompletedFirstDeployment"/>
    /// is observed or the 60-min hard timeout elapses.
    /// </summary>
    /// <remarks>
    /// <b>Invariants</b>:
    /// <list type="bullet">
    ///   <item>Immutable; <see cref="With"/>-methods return new instances.</item>
    ///   <item><see cref="Packages"/> is capped at <see cref="MaxPackages"/>; overflow is discarded.</item>
    ///   <item><see cref="ResolvedUtc"/> is set once on observing phase 110; later phase readings update <see cref="LastDeploymentPhase"/> only.</item>
    ///   <item><see cref="Outcome"/> is one of <c>"Resolved"</c> / <c>"Timeout"</c>; null while RJ is still running.</item>
    /// </list>
    /// </remarks>
    public sealed class RealmJoinFacts
    {
        /// <summary>Cap on <see cref="Packages"/> so pathological deployments cannot bloat state.</summary>
        public const int MaxPackages = 200;

        public const string OutcomeResolved = "Resolved";
        public const string OutcomeTimeout = "Timeout";

        public static readonly RealmJoinFacts Empty = new RealmJoinFacts(
            detectedUtc: null,
            resolvedUtc: null,
            lastDeploymentPhase: null,
            outcome: null,
            selfDeployingDeferredCompletion: null,
            productVersion: null,
            releaseChannel: null,
            packages: Array.Empty<RealmJoinPackageFact>());

        public RealmJoinFacts(
            SignalFact<DateTime>? detectedUtc,
            SignalFact<DateTime>? resolvedUtc,
            SignalFact<int>? lastDeploymentPhase,
            SignalFact<string>? outcome,
            SignalFact<bool>? selfDeployingDeferredCompletion,
            SignalFact<string>? productVersion,
            SignalFact<string>? releaseChannel,
            IReadOnlyList<RealmJoinPackageFact> packages)
        {
            DetectedUtc = detectedUtc;
            ResolvedUtc = resolvedUtc;
            LastDeploymentPhase = lastDeploymentPhase;
            Outcome = outcome;
            SelfDeployingDeferredCompletion = selfDeployingDeferredCompletion;
            ProductVersion = productVersion;
            ReleaseChannel = releaseChannel;
            Packages = packages ?? Array.Empty<RealmJoinPackageFact>();
        }

        /// <summary>First-observation timestamp of the <c>HKLM\SYSTEM\...\realmjoin\Parameters</c> key.</summary>
        public SignalFact<DateTime>? DetectedUtc { get; }

        /// <summary>Set once when <see cref="LastDeploymentPhase"/> first reaches 110.</summary>
        public SignalFact<DateTime>? ResolvedUtc { get; }

        /// <summary>Most recent <c>DeploymentPhase</c> DWORD observed in the Parameters key.</summary>
        public SignalFact<int>? LastDeploymentPhase { get; }

        /// <summary>Terminal outcome string: <see cref="OutcomeResolved"/> or <see cref="OutcomeTimeout"/>.</summary>
        public SignalFact<string>? Outcome { get; }

        /// <summary>
        /// Indicates the SelfDeploying terminal path (<see cref="Signals.DecisionSignalKind.DeviceSetupProvisioningComplete"/>)
        /// was observed but blocked by an active RealmJoin gate. The RealmJoin
        /// resolved/timeout handlers use this flag to complete the SelfDeploying path
        /// directly without re-entering the Classic Finalizing pipeline.
        /// </summary>
        public SignalFact<bool>? SelfDeployingDeferredCompletion { get; }

        /// <summary>
        /// Bare version parsed from <c>C:\Program Files\RealmJoin\RealmJoin.exe</c>'s
        /// file-version resource at detection time. Observability-only — never gates a
        /// decision. Null when the binary was missing or unreadable.
        /// </summary>
        public SignalFact<string>? ProductVersion { get; }

        /// <summary>
        /// RJ release channel parsed from the version string's SemVer prerelease tag at
        /// detection time: <c>"release"</c> (untagged stable), <c>"beta"</c> or <c>"canary"</c>.
        /// Observability-only — never gates a decision. Null when the version was unreadable.
        /// </summary>
        public SignalFact<string>? ReleaseChannel { get; }

        /// <summary>Tracked per-package install rows (machine + user scope combined).</summary>
        public IReadOnlyList<RealmJoinPackageFact> Packages { get; }

        public RealmJoinFacts WithDetected(DateTime utc, long sourceSignalOrdinal)
        {
            if (DetectedUtc != null) return this; // set-once
            return new RealmJoinFacts(
                detectedUtc: new SignalFact<DateTime>(utc, sourceSignalOrdinal),
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: Outcome,
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: Packages);
        }

        /// <summary>
        /// Set-once observability fact carrying the <c>RealmJoin.exe</c> ProductVersion. No-op
        /// when <paramref name="productVersion"/> is empty or already set. Pure metadata —
        /// never participates in a gate decision.
        /// </summary>
        public RealmJoinFacts WithProductVersion(string productVersion, long sourceSignalOrdinal)
        {
            if (ProductVersion != null) return this;
            if (string.IsNullOrEmpty(productVersion)) return this;
            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: Outcome,
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: new SignalFact<string>(productVersion, sourceSignalOrdinal),
                releaseChannel: ReleaseChannel,
                packages: Packages);
        }

        /// <summary>
        /// Set-once observability fact carrying the RJ release channel parsed from the
        /// <c>RealmJoin.exe</c> version string. No-op when <paramref name="releaseChannel"/>
        /// is empty or already set. Pure metadata — never participates in a gate decision.
        /// </summary>
        public RealmJoinFacts WithReleaseChannel(string releaseChannel, long sourceSignalOrdinal)
        {
            if (ReleaseChannel != null) return this;
            if (string.IsNullOrEmpty(releaseChannel)) return this;
            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: Outcome,
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: ProductVersion,
                releaseChannel: new SignalFact<string>(releaseChannel, sourceSignalOrdinal),
                packages: Packages);
        }

        public RealmJoinFacts WithResolved(DateTime utc, int phase, long sourceSignalOrdinal)
        {
            if (ResolvedUtc != null) return this; // set-once
            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: new SignalFact<DateTime>(utc, sourceSignalOrdinal),
                lastDeploymentPhase: new SignalFact<int>(phase, sourceSignalOrdinal),
                outcome: new SignalFact<string>(OutcomeResolved, sourceSignalOrdinal),
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: Packages);
        }

        public RealmJoinFacts WithLastPhase(int phase, long sourceSignalOrdinal)
        {
            if (LastDeploymentPhase != null && LastDeploymentPhase.Value == phase) return this;
            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: new SignalFact<int>(phase, sourceSignalOrdinal),
                outcome: Outcome,
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: Packages);
        }

        public RealmJoinFacts WithTimeoutOutcome(long sourceSignalOrdinal)
        {
            if (Outcome != null) return this; // resolved already wins
            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: new SignalFact<string>(OutcomeTimeout, sourceSignalOrdinal),
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: Packages);
        }

        public RealmJoinFacts WithSelfDeployingDeferred(long sourceSignalOrdinal)
        {
            if (SelfDeployingDeferredCompletion != null) return this;
            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: Outcome,
                selfDeployingDeferredCompletion: new SignalFact<bool>(true, sourceSignalOrdinal),
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: Packages);
        }

        /// <summary>
        /// Clear <see cref="SelfDeployingDeferredCompletion"/> back to null. Called by the RJ-deferred
        /// release re-check branch in <c>CompleteIfDeferredOrBookkeep</c> when post-deadline
        /// signals (AccountSetup entry, monotonic Mode conflict) demote the deferred SelfDeploying
        /// path back to Classic flow. Idempotent — returns <c>this</c> when already null.
        /// </summary>
        public RealmJoinFacts ClearSelfDeployingDeferred()
        {
            if (SelfDeployingDeferredCompletion == null) return this;
            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: Outcome,
                selfDeployingDeferredCompletion: null,
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: Packages);
        }

        /// <summary>
        /// Append a new package row when the package is first observed (DisplayName seen,
        /// no Success/LastExitCode yet). Idempotent on (<paramref name="packageId"/>,
        /// <paramref name="scope"/>) — a second start for the same pair is ignored.
        /// </summary>
        public RealmJoinFacts WithPackageStarted(
            string packageId,
            string displayName,
            string? version,
            string scope,
            DateTime startedUtc)
        {
            if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(scope)) return this;

            for (var i = 0; i < Packages.Count; i++)
            {
                var p = Packages[i];
                if (string.Equals(p.PackageId, packageId, StringComparison.Ordinal) &&
                    string.Equals(p.Scope, scope, StringComparison.Ordinal))
                {
                    return this; // already tracked
                }
            }

            if (Packages.Count >= MaxPackages) return this;

            var truncated = displayName ?? string.Empty;
            if (truncated.Length > RealmJoinPackageFact.MaxDisplayNameLength)
            {
                truncated = truncated.Substring(0, RealmJoinPackageFact.MaxDisplayNameLength);
            }

            var copy = new List<RealmJoinPackageFact>(Packages.Count + 1);
            copy.AddRange(Packages);
            copy.Add(new RealmJoinPackageFact(
                packageId: packageId,
                displayName: truncated,
                version: version,
                scope: scope,
                startedUtc: startedUtc,
                completedUtc: null,
                success: null,
                lastExitCode: null));

            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: Outcome,
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: copy);
        }

        /// <summary>
        /// Update an existing package row with its terminal outcome. If the package wasn't
        /// previously seen (rare race), a new row is created with Started = Completed and
        /// only the terminal fields populated.
        /// </summary>
        public RealmJoinFacts WithPackageCompleted(
            string packageId,
            string displayName,
            string? version,
            string scope,
            DateTime completedUtc,
            bool success,
            int lastExitCode)
        {
            if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(scope)) return this;

            var truncated = displayName ?? string.Empty;
            if (truncated.Length > RealmJoinPackageFact.MaxDisplayNameLength)
            {
                truncated = truncated.Substring(0, RealmJoinPackageFact.MaxDisplayNameLength);
            }

            var copy = new List<RealmJoinPackageFact>(Packages.Count);
            var updated = false;
            for (var i = 0; i < Packages.Count; i++)
            {
                var p = Packages[i];
                if (!updated &&
                    string.Equals(p.PackageId, packageId, StringComparison.Ordinal) &&
                    string.Equals(p.Scope, scope, StringComparison.Ordinal))
                {
                    if (p.CompletedUtc != null)
                    {
                        return this; // already completed; ignore duplicate
                    }
                    copy.Add(p.WithCompletion(completedUtc, success, lastExitCode));
                    updated = true;
                }
                else
                {
                    copy.Add(p);
                }
            }

            if (!updated)
            {
                if (Packages.Count >= MaxPackages) return this;
                copy.Add(new RealmJoinPackageFact(
                    packageId: packageId,
                    displayName: truncated,
                    version: version,
                    scope: scope,
                    startedUtc: completedUtc,
                    completedUtc: completedUtc,
                    success: success,
                    lastExitCode: lastExitCode));
            }

            return new RealmJoinFacts(
                detectedUtc: DetectedUtc,
                resolvedUtc: ResolvedUtc,
                lastDeploymentPhase: LastDeploymentPhase,
                outcome: Outcome,
                selfDeployingDeferredCompletion: SelfDeployingDeferredCompletion,
                productVersion: ProductVersion,
                releaseChannel: ReleaseChannel,
                packages: copy);
        }
    }
}
