using System;
using System.Collections.Generic;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Aggregated facts about app-install terminal outcomes observed during a session.
    /// Codex follow-up #4 — <see cref="Signals.DecisionSignalKind.AppInstallCompleted"/> and
    /// <see cref="Signals.DecisionSignalKind.AppInstallFailed"/> are now first-class
    /// observation signals; this value holds their rolled-up counts so the reducer, the
    /// journal (via <see cref="DecisionState"/>) and any future classifier have structured
    /// evidence about which apps finished and which blocked.
    /// <para>
    /// <b>Invariants</b>:
    /// <list type="bullet">
    ///   <item><see cref="CompletedCount"/> == <see cref="InstalledCount"/> + <see cref="SkippedCount"/> + <see cref="PostponedCount"/>.</item>
    ///   <item><see cref="FailedAppIds"/> is capped at <see cref="MaxFailedAppIds"/>; overflow is discarded so DecisionState cannot grow unbounded.</item>
    ///   <item>Instances are immutable; <see cref="WithCompleted"/> / <see cref="WithFailed"/> return new instances.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class AppInstallFacts
    {
        /// <summary>Cap on <see cref="FailedAppIds"/> so pathological sessions cannot bloat state.</summary>
        public const int MaxFailedAppIds = 50;

        public static readonly AppInstallFacts Empty = new AppInstallFacts(
            completedCount: 0,
            installedCount: 0,
            skippedCount: 0,
            postponedCount: 0,
            failedCount: 0,
            failedAppIds: Array.Empty<string>());

        public AppInstallFacts(
            int completedCount,
            int installedCount,
            int skippedCount,
            int postponedCount,
            int failedCount,
            IReadOnlyList<string> failedAppIds)
        {
            if (completedCount < 0) throw new ArgumentOutOfRangeException(nameof(completedCount));
            if (installedCount < 0) throw new ArgumentOutOfRangeException(nameof(installedCount));
            if (skippedCount < 0) throw new ArgumentOutOfRangeException(nameof(skippedCount));
            if (postponedCount < 0) throw new ArgumentOutOfRangeException(nameof(postponedCount));
            if (failedCount < 0) throw new ArgumentOutOfRangeException(nameof(failedCount));

            CompletedCount = completedCount;
            InstalledCount = installedCount;
            SkippedCount = skippedCount;
            PostponedCount = postponedCount;
            FailedCount = failedCount;
            FailedAppIds = failedAppIds ?? Array.Empty<string>();
        }

        /// <summary>Installed + Skipped + Postponed (everything the adapter maps to <c>AppInstallCompleted</c>).</summary>
        public int CompletedCount { get; }

        /// <summary>Apps with terminal state == <c>Installed</c>.</summary>
        public int InstalledCount { get; }

        /// <summary>Apps with terminal state == <c>Skipped</c> (policy-skipped, not failed).</summary>
        public int SkippedCount { get; }

        /// <summary>Apps with terminal state == <c>Postponed</c>.</summary>
        public int PostponedCount { get; }

        /// <summary>Apps with terminal state == <c>Error</c> (i.e. <c>AppInstallFailed</c>).</summary>
        public int FailedCount { get; }

        /// <summary>
        /// Identifiers of failed apps, first <see cref="MaxFailedAppIds"/> in order of
        /// observation. Duplicate <c>appId</c>s are ignored (terminal is posted once per app,
        /// but the guard is defensive).
        /// </summary>
        public IReadOnlyList<string> FailedAppIds { get; }

        /// <summary>
        /// Produce a new facts value with the completed sub-category incremented.
        /// <paramref name="newStatePayload"/> is the adapter's payload value — one of
        /// <c>"Installed"</c>, <c>"Skipped"</c>, <c>"Postponed"</c>. Unknown values still
        /// increment <see cref="CompletedCount"/> but don't contribute to any breakdown.
        /// </summary>
        public AppInstallFacts WithCompleted(string? newStatePayload)
        {
            var installed = InstalledCount;
            var skipped = SkippedCount;
            var postponed = PostponedCount;

            if (string.Equals(newStatePayload, "Installed", StringComparison.OrdinalIgnoreCase)) installed++;
            else if (string.Equals(newStatePayload, "Skipped", StringComparison.OrdinalIgnoreCase)) skipped++;
            else if (string.Equals(newStatePayload, "Postponed", StringComparison.OrdinalIgnoreCase)) postponed++;

            return new AppInstallFacts(
                completedCount: CompletedCount + 1,
                installedCount: installed,
                skippedCount: skipped,
                postponedCount: postponed,
                failedCount: FailedCount,
                failedAppIds: FailedAppIds);
        }

        /// <summary>
        /// Produce a new facts value with <see cref="FailedCount"/> incremented and
        /// <paramref name="appId"/> appended to <see cref="FailedAppIds"/> (dedup + cap).
        /// </summary>
        public AppInstallFacts WithFailed(string? appId)
        {
            var newIds = FailedAppIds;
            if (!string.IsNullOrEmpty(appId))
            {
                appId = FactStringBounds.Bound(appId); // before the scan — bounded compare key
                var alreadyTracked = false;
                for (var i = 0; i < FailedAppIds.Count; i++)
                {
                    if (string.Equals(FailedAppIds[i], appId, StringComparison.Ordinal))
                    {
                        alreadyTracked = true;
                        break;
                    }
                }

                if (!alreadyTracked && FailedAppIds.Count < MaxFailedAppIds)
                {
                    var copy = new List<string>(FailedAppIds.Count + 1);
                    copy.AddRange(FailedAppIds);
                    copy.Add(appId!);
                    newIds = copy;
                }
            }

            return new AppInstallFacts(
                completedCount: CompletedCount,
                installedCount: InstalledCount,
                skippedCount: SkippedCount,
                postponedCount: PostponedCount,
                failedCount: FailedCount + 1,
                failedAppIds: newIds);
        }
    }
}
