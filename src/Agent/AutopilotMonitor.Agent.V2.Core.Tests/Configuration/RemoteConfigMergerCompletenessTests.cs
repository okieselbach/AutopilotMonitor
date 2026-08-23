using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Configuration
{
    /// <summary>
    /// Completeness tripwire for <see cref="RemoteConfigMerger"/>: the merger hand-maps
    /// <see cref="AgentConfigResponse"/> fields onto <c>AgentConfiguration</c>, and
    /// historically new tenant knobs were forgotten there and silently ignored. Every public
    /// property of <see cref="AgentConfigResponse"/> must be classified in exactly one of the
    /// two lists below. A new property fails this test until a human either wires it through
    /// <see cref="RemoteConfigMerger.Merge"/> or documents where else it is consumed.
    /// </summary>
    public sealed class RemoteConfigMergerCompletenessTests
    {
        /// <summary>
        /// Fields <see cref="RemoteConfigMerger.Merge"/> maps onto <c>AgentConfiguration</c>.
        /// Keep in sync with the Merge body — the test only proves classification coverage,
        /// the value-level mapping itself is covered by <see cref="RemoteConfigMergerTests"/>.
        /// </summary>
        private static readonly string[] MergedByMerger =
        {
            nameof(AgentConfigResponse.SelfDestructOnComplete),
            nameof(AgentConfigResponse.KeepLogFile),
            nameof(AgentConfigResponse.RebootOnComplete),
            nameof(AgentConfigResponse.EnableGeoLocation),
            nameof(AgentConfigResponse.LogLevel),
            nameof(AgentConfigResponse.RebootDelaySeconds),
            nameof(AgentConfigResponse.ShowEnrollmentSummary),
            nameof(AgentConfigResponse.EnrollmentSummaryTimeoutSeconds),
            nameof(AgentConfigResponse.EnrollmentSummaryBrandingImageUrl),
            nameof(AgentConfigResponse.EnrollmentSummaryLaunchRetrySeconds),
            nameof(AgentConfigResponse.NtpServer),
            nameof(AgentConfigResponse.EnableTimezoneAutoSet),
            nameof(AgentConfigResponse.EnableDoGroupIdAutoSet),
            nameof(AgentConfigResponse.DiagnosticsUploadEnabled),
            nameof(AgentConfigResponse.DiagnosticsUploadMode),
            nameof(AgentConfigResponse.DiagnosticsLogPaths),
            nameof(AgentConfigResponse.SendTraceEvents),
            nameof(AgentConfigResponse.EnableEspContinueAnywayObservation),
            nameof(AgentConfigResponse.UnrestrictedMode),
            nameof(AgentConfigResponse.EnableImeMatchLog),
            nameof(AgentConfigResponse.EnableGatherRuleDebugLog),
            nameof(AgentConfigResponse.UploadIntervalSeconds),
            nameof(AgentConfigResponse.MaxBatchSize),
            nameof(AgentConfigResponse.MaxAuthFailures),
            nameof(AgentConfigResponse.AuthFailureTimeoutMinutes),
            // Merge reads two Collectors knobs (AgentMaxLifetimeMinutes, HelloWaitTimeoutSeconds);
            // the remaining CollectorConfiguration flags flow untouched to DefaultComponentFactory.
            nameof(AgentConfigResponse.Collectors),
            // Merge reads one Analyzers knob (EnableRealmJoinWatcher → diagnostics RealmJoin sections);
            // the remaining flags flow untouched to DefaultComponentFactory + AgentAnalyzerManager.
            nameof(AgentConfigResponse.Analyzers),
        };

        /// <summary>
        /// Fields deliberately NOT mapped by the merger because another component consumes them
        /// directly from the remote config. One-line justification per entry — verify the
        /// consumer before classifying a new field here.
        /// </summary>
        private static readonly string[] IntentionallyNotMerged =
        {
            // Schema/version metadata — logged and cached by RemoteConfigService, not a runtime knob.
            nameof(AgentConfigResponse.ConfigVersion),
            // Consumed by DefaultComponentFactory → GatherRuleExecutorHost (rules run against the raw remote list).
            nameof(AgentConfigResponse.GatherRules),
            // Consumed by DefaultComponentFactory → ImeLogHost (pattern list handed to ImeLogTracker).
            nameof(AgentConfigResponse.ImeLogPatterns),
            // Passed by AgentRuntimeHost into IComponentFactory.CreateCollectorHosts → ImeLogHost sealing re-emit.
            nameof(AgentConfigResponse.WhiteGloveSealingPatternIds),
            // Self-update ZIP integrity second trust channel — BinaryIntegrityVerifier / Program update path.
            nameof(AgentConfigResponse.LatestAgentSha256),
            // Running-EXE integrity verification — BinaryIntegrityVerifier post-config check.
            nameof(AgentConfigResponse.LatestAgentExeSha256),
            // Self-updater force-update downgrade gate — BinaryIntegrityVerifier / Program update path.
            nameof(AgentConfigResponse.AllowAgentDowngrade),
            // Control-channel block signal — RemoteConfigService/ServerControlPlane; stripped from the disk cache.
            nameof(AgentConfigResponse.DeviceBlocked),
            // Control-channel kill signal — ServerControlPlane / Program.Guards; live fetch only.
            nameof(AgentConfigResponse.DeviceKillSignal),
            // Temporary-block expiry accompanying DeviceBlocked — ServerControlPlane.
            nameof(AgentConfigResponse.UnblockAt),
            // Endpoint migration — EndpointMigration/RemoteConfigService re-run the fetch, live fetch only.
            nameof(AgentConfigResponse.MigrateToApiBaseUrl),
        };

        [Fact]
        public void Every_AgentConfigResponse_property_is_classified_exactly_once()
        {
            var properties = typeof(AgentConfigResponse)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var merged = new HashSet<string>(MergedByMerger, StringComparer.Ordinal);
            var notMerged = new HashSet<string>(IntentionallyNotMerged, StringComparer.Ordinal);

            var inBoth = merged.Intersect(notMerged, StringComparer.Ordinal).ToArray();
            Assert.True(
                inBoth.Length == 0,
                $"Fields listed in BOTH classification lists: [{string.Join(", ", inBoth)}].");

            var unclassified = properties
                .Where(p => !merged.Contains(p) && !notMerged.Contains(p))
                .ToArray();
            Assert.True(
                unclassified.Length == 0,
                "New AgentConfigResponse field(s) without a classification: " +
                $"[{string.Join(", ", unclassified)}]. Either map them in RemoteConfigMerger.Merge " +
                "(+ MergedByMerger here) or verify the consuming component and add them to " +
                "IntentionallyNotMerged with a justification comment.");

            // Stale entries (renamed/removed properties) must be cleaned up too.
            var known = new HashSet<string>(properties, StringComparer.Ordinal);
            var stale = merged.Concat(notMerged).Where(n => !known.Contains(n)).ToArray();
            Assert.True(
                stale.Length == 0,
                $"Classification lists reference non-existent AgentConfigResponse properties: [{string.Join(", ", stale)}].");
        }
    }
}
