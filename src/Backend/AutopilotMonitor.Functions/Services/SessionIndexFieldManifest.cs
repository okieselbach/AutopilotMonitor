using System;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// The declared SessionsIndex column set — single source for what the FULL-MIRROR index
    /// row carries (see <c>TableStorageService.BuildSessionIndexEntity</c>, which is pinned
    /// against this manifest bidirectionally by <c>SessionIndexFieldManifestTests</c>).
    ///
    /// Maintenance flow for a NEW mirrored field:
    /// 1. add it to <see cref="AlwaysProjected"/> (written with a default on every rebuild)
    ///    or <see cref="ConditionallyProjected"/> (written only when present),
    /// 2. write it in <c>BuildSessionIndexEntity</c> with matching semantics,
    /// 3. read it in <c>MapToSessionSummary</c>.
    /// Skipping any step turns the manifest tests red. Merge sites are guarded fail-soft:
    /// <c>MergeSessionIndexAsync</c> logs a warning for keys outside <see cref="All"/> —
    /// a merged-but-not-projected field is exactly the recurring StartedAt-shift drift bug
    /// (e.g. ab90423b).
    /// </summary>
    internal static class SessionIndexFieldManifest
    {
        /// <summary>
        /// Written on every index rebuild with a default (never absent from an index row).
        /// Note: <c>CurrentPhaseDetail</c> is a dead column — no Sessions writer sets it, so
        /// it is always empty; kept for row-shape stability until deliberately retired.
        /// </summary>
        public static readonly string[] AlwaysProjected =
        {
            "SessionId",
            "SerialNumber",
            "DeviceName",
            "Manufacturer",
            "Model",
            "StartedAt",
            "Status",
            "CurrentPhase",
            "CurrentPhaseDetail",
            "EventCount",
            "EnrollmentType",
            "IsPreProvisioned",
            "IsHybridJoin",
            "IsUserDriven",
            "IsSelfDeployingProfile",
            "IsCloudPc",
            "AgentVersion",
            "ImeAgentVersion",
            "OsName",
            "OsBuild",
            "OsDisplayVersion",
            "OsEdition",
            "OsLanguage",
            "GeoCountry",
            "GeoRegion",
            "GeoCity",
            "GeoLoc",
            "PlatformScriptCount",
            "RemediationScriptCount",
            "RebootCount",
            "ExcessiveEventsAlerted",
            "ExcessiveEventsAutoActioned",
        };

        /// <summary>Written only when the Sessions row carries a value (absent otherwise).</summary>
        public static readonly string[] ConditionallyProjected =
        {
            "CompletedAt",
            "FailureReason",
            "FailureSource",
            "ReconcileReason",
            "EspSoftFailure",
            "CompletionSource",
            "ValidatedBy",
            "FailureSnapshotJson",
            "AdminMarkedAction",
            "DurationSeconds",
            "DiagnosticsBlobName",
            "DiagnosticsBlobDestination",
            "LastEventAt",
            "ResumedAt",
            "StalledAt",
            "AvgApiLatencyMs",
            "ApiRequestCount",
            "ConnectionType",
        };

        /// <summary>Every column an index row may carry (data columns; PK/RK excluded).</summary>
        public static readonly string[] All =
            AlwaysProjected.Concat(ConditionallyProjected).ToArray();

        /// <summary>
        /// Sessions-row fields owned by separate write subsystems that deliberately do NOT
        /// touch the index (ServerActions queue + deletion CAS). Sessions served from the
        /// index read these as defaults; routing them through the index sync is a tracked
        /// follow-up, not an accident.
        /// </summary>
        public static readonly string[] PrimaryOnly =
        {
            "PendingActionsJson",
            "PendingActionsQueuedAt",
            "DeletionState",
            "PendingDeletionManifestId",
        };

        private static readonly HashSet<string> AllSet = new(All, StringComparer.Ordinal);

        /// <summary>
        /// System keys a merge entity may legitimately carry besides data columns.
        /// </summary>
        private static readonly HashSet<string> SystemKeys = new(StringComparer.Ordinal)
        {
            "PartitionKey", "RowKey", "Timestamp", "odata.etag",
        };

        /// <summary>
        /// Returns the data keys of <paramref name="mergeEntity"/> that are NOT part of the
        /// manifest — i.e. fields a merge site writes that a StartedAt-shift full rebuild
        /// would silently drop. Callers log these as a warning (fail-soft).
        /// </summary>
        public static List<string> FindNonManifestKeys(Azure.Data.Tables.TableEntity mergeEntity)
        {
            List<string>? offenders = null;
            foreach (var kvp in mergeEntity)
            {
                if (SystemKeys.Contains(kvp.Key) || AllSet.Contains(kvp.Key))
                    continue;
                (offenders ??= new List<string>()).Add(kvp.Key);
            }
            return offenders ?? new List<string>();
        }
    }
}
