using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// RealmJoin package rows of <c>AppInstallSummaries</c> (<see cref="AppInstallSources.RealmJoin"/>),
    /// folded from the watcher's <c>realmjoin_package_started</c> / <c>_completed</c> events and
    /// keyed by scope + package id. The watcher reports only packages that appear once RealmJoin
    /// owns its deployment (phase 100+); packages written to the RealmJoin registry earlier are
    /// Intune (IME) installs of RealmJoin-wrapped apps and already have their IME row — they must
    /// not get a second one.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        internal static bool IsRealmJoinPackageEvent(string? eventType) =>
            eventType == SharedConstants.EventTypes.RealmJoinPackageStarted ||
            eventType == SharedConstants.EventTypes.RealmJoinPackageCompleted;

        /// <summary>In-batch key of a RealmJoin row; never collides with an IME row's app-name key.</summary>
        internal static string RealmJoinAggregationKey(string scope, string packageId) =>
            $"{AppInstallSources.RealmJoin}|{scope}|{packageId}";

        internal static void AggregateRealmJoinPackageEvent(EnrollmentEvent evt, string tenantId, string sessionId, Dictionary<string, AppInstallAggregationState> summaries)
        {
            if (evt.Data == null) return;
            var packageId = ReadRealmJoinString(evt.Data, DecisionEngine.RealmJoinPayloadKeys.PackageId);
            if (string.IsNullOrEmpty(packageId)) return;
            var scope = RealmJoinPackageFact.NormalizeScope(ReadRealmJoinString(evt.Data, DecisionEngine.RealmJoinPayloadKeys.Scope));

            var key = RealmJoinAggregationKey(scope, packageId!);
            if (!summaries.TryGetValue(key, out var state))
            {
                state = new AppInstallAggregationState
                {
                    Summary = new AppInstallSummary
                    {
                        Source = AppInstallSources.RealmJoin,
                        InstallScope = scope,
                        AppId = packageId!,
                        AppName = packageId!,
                        SessionId = sessionId,
                        TenantId = tenantId,
                        StartedAt = evt.Timestamp
                    }
                };
                summaries[key] = state;
            }
            var summary = state.Summary;

            // RealmJoin does not always write DisplayName; the package id stands in until it does.
            var displayName = ReadRealmJoinString(evt.Data, DecisionEngine.RealmJoinPayloadKeys.DisplayName);
            if (!string.IsNullOrEmpty(displayName))
                summary.AppName = displayName!;
            var version = ReadRealmJoinString(evt.Data, DecisionEngine.RealmJoinPayloadKeys.Version);
            if (!string.IsNullOrEmpty(version))
                summary.AppVersion = version!;

            if (evt.EventType == SharedConstants.EventTypes.RealmJoinPackageStarted)
            {
                if (!state.InstallStartedAt.HasValue || evt.Timestamp < state.InstallStartedAt.Value)
                    state.InstallStartedAt = evt.Timestamp;
                if (!summary.LastAttemptStartedAt.HasValue || evt.Timestamp > summary.LastAttemptStartedAt.Value)
                    summary.LastAttemptStartedAt = evt.Timestamp;
                summary.InstallPassCount++;
                if (summary.Status == string.Empty || summary.Status == "InProgress")
                    summary.Status = "InProgress";
            }
            else
            {
                if (!bool.TryParse(ReadRealmJoinString(evt.Data, DecisionEngine.RealmJoinPayloadKeys.Success), out var success))
                    return;
                var exitCode = int.TryParse(ReadRealmJoinString(evt.Data, DecisionEngine.RealmJoinPayloadKeys.LastExitCode), out var code)
                    ? code : (int?)null;
                summary.Status = success ? "Succeeded" : "Failed";
                summary.TerminalState = success ? "Installed" : "Error";
                summary.ExitCode = exitCode;
                // RealmJoin reports no error text; the exit code is the failure code.
                summary.FailureCode = success ? string.Empty : (exitCode?.ToString() ?? string.Empty);
                summary.CompletedAt = evt.Timestamp;
            }

            // Duration = completion minus the observed start. A package the watcher first saw
            // already finished yields 0 s (same registry pass), which the metrics read as unmeasured.
            RecalculateAppDurations(state);
        }

        private static string? ReadRealmJoinString(Dictionary<string, object> data, string key) =>
            data.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;
    }
}
