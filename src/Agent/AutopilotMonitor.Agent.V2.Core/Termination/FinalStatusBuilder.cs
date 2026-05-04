#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Composes a <see cref="FinalStatus"/> snapshot from the kernel <see cref="DecisionState"/>
    /// plus the IME tracker's live <see cref="AppPackageStateList"/>. Plan §4.x M4.6.β.
    /// <para>
    /// Pure function: no I/O, no side effects. The writer (<see cref="SummaryDialogLauncher"/>)
    /// takes the built DTO and serialises it to JSON at the path the dialog reads. Splitting
    /// builder from writer keeps this testable without touching the file system.
    /// </para>
    /// </summary>
    public static class FinalStatusBuilder
    {
        /// <summary>
        /// Constructs a <see cref="FinalStatus"/> from the orchestrator outcome plus a live snapshot
        /// of the IME package state list (may be <c>null</c> when the IME host was not started —
        /// the summary then reports an empty app section).
        /// </summary>
        public static FinalStatus Build(
            DecisionState state,
            EnrollmentTerminatedEventArgs terminated,
            IReadOnlyList<AppPackageState>? packageStates,
            DateTime agentStartTimeUtc,
            IReadOnlyDictionary<string, AppInstallTiming>? appTimings = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (terminated == null) throw new ArgumentNullException(nameof(terminated));

            var uptimeSeconds = Math.Max(0, (terminated.TerminatedAtUtc - agentStartTimeUtc).TotalSeconds);
            var timings = appTimings ?? new Dictionary<string, AppInstallTiming>();

            var status = new FinalStatus
            {
                SchemaVersion = 2,
                Timestamp = terminated.TerminatedAtUtc.ToString("O"),
                Outcome = MapOutcome(terminated.Outcome, state.Stage),
                CompletionSource = terminated.Reason.ToString(),
                HelloOutcome = state.HelloOutcome?.Value ?? "unknown",
                EnrollmentType = DescribeEnrollmentType(state.ScenarioProfile),
                AgentUptimeSeconds = uptimeSeconds,
                SignalsSeen = BuildSignalsSeen(state),
                FailureReason = BuildFailureReason(terminated, state),
                SignalTimestamps = BuildSignalTimestamps(state),
                AppSummary = BuildAppSummary(packageStates),
                PackageStatesByPhase = BuildPackageStatesByPhase(packageStates, timings),
            };

            return status;
        }

        /// <summary>
        /// Schema 2 — derives a human-readable explanation rendered as the dialog's failure
        /// banner. Returns <c>null</c> for successful terminal outcomes (succeeded /
        /// whiteglove_part1) so the dialog skips the banner. Part-2 reaches succeeded
        /// through the Classic flow after Archive-and-Reset (PR-A).
        /// <para>
        /// Source priority:
        /// </para>
        /// <list type="number">
        ///   <item><see cref="EnrollmentTerminatedEventArgs.Details"/> — if the orchestrator
        ///     supplied a specific message (e.g. ESP failure code from the failure event).</item>
        ///   <item><see cref="EnrollmentTerminationReason.MaxLifetimeExceeded"/> — generic timeout.</item>
        ///   <item>Hello outcome (<c>"timeout"</c> / <c>"failed"</c>) — the most user-facing of
        ///     the terminal failure modes when ESP itself didn't pre-empt with a code.</item>
        ///   <item>Generic fallback for terminal failures — preserves the dialog's failure
        ///     rendering even when no specific cause is identifiable.</item>
        /// </list>
        /// </summary>
        private static string? BuildFailureReason(EnrollmentTerminatedEventArgs terminated, DecisionState state)
        {
            // Successful outcomes — no banner.
            if (terminated.Outcome == EnrollmentTerminationOutcome.Succeeded) return null;

            if (!string.IsNullOrEmpty(terminated.Details)) return terminated.Details;

            if (terminated.Reason == EnrollmentTerminationReason.MaxLifetimeExceeded)
            {
                return "Enrollment exceeded the maximum allowed runtime and was stopped before reaching a terminal stage.";
            }

            var hello = state.HelloOutcome?.Value;
            if (string.Equals(hello, "timeout", StringComparison.OrdinalIgnoreCase))
                return "Windows Hello provisioning timed out.";
            if (string.Equals(hello, "failed", StringComparison.OrdinalIgnoreCase))
                return "Windows Hello provisioning failed.";

            return "Enrollment did not complete successfully. Check the diagnostics bundle for details.";
        }

        /// <summary>
        /// Schema 2 — milestone signal timestamps (ISO-8601). Mirrors what the V1 agent wrote
        /// to <c>signalTimestamps</c>; V2 lost this in the rewrite. The dialog does not render
        /// it, but field engineers reading <c>final-status.json</c> post-mortem rely on it.
        /// </summary>
        private static Dictionary<string, string>? BuildSignalTimestamps(DecisionState state)
        {
            var ts = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string key, SignalFact<DateTime>? fact)
            {
                if (fact != null) ts[key] = fact.Value.ToString("o");
            }

            Add("espFinalExit", state.EspFinalExitUtc);
            Add("helloResolved", state.HelloResolvedUtc);
            Add("desktopArrived", state.DesktopArrivedUtc);
            Add("systemReboot", state.SystemRebootUtc);

            return ts.Count == 0 ? null : ts;
        }

        private static string MapOutcome(EnrollmentTerminationOutcome outcome, SessionStage stage)
        {
            if (stage == SessionStage.WhiteGloveSealed) return "whiteglove_part1";
            switch (outcome)
            {
                case EnrollmentTerminationOutcome.Succeeded: return "succeeded";
                case EnrollmentTerminationOutcome.Failed: return "failed";
                case EnrollmentTerminationOutcome.TimedOut: return "timed_out";
                default: return "unknown";
            }
        }

        private static List<string> BuildSignalsSeen(DecisionState state)
        {
            var signals = new List<string>();
            if (state.EspFinalExitUtc != null) signals.Add("esp_final_exit");
            if (state.HelloResolvedUtc != null) signals.Add("hello_resolved");
            if (state.DesktopArrivedUtc != null) signals.Add("desktop_arrived");
            if (state.SystemRebootUtc != null) signals.Add("system_reboot");
            var obs = state.ScenarioObservations;
            if (obs.AadUserJoinWithUserObserved != null && obs.AadUserJoinWithUserObserved.Value) signals.Add("aad_user_joined");
            if (obs.ShellCoreWhiteGloveSuccessSeen != null && obs.ShellCoreWhiteGloveSuccessSeen.Value)
                signals.Add("whiteglove_shellcore_success");
            if (obs.WhiteGloveSealingPatternSeen != null && obs.WhiteGloveSealingPatternSeen.Value)
                signals.Add("whiteglove_sealing_pattern");
            if (state.ImeMatchedPatternId != null) signals.Add($"ime_pattern:{state.ImeMatchedPatternId.Value}");
            return signals;
        }

        private static FinalStatusAppSummary BuildAppSummary(IReadOnlyList<AppPackageState>? packageStates)
        {
            var summary = new FinalStatusAppSummary();
            if (packageStates == null || packageStates.Count == 0) return summary;

            foreach (var pkg in packageStates)
            {
                summary.TotalApps++;
                if (IsCompleted(pkg.InstallationState)) summary.CompletedApps++;
                if (pkg.InstallationState == AppInstallationState.Error)
                {
                    summary.ErrorCount++;
                    if (pkg.Targeted == AppTargeted.Device) summary.DeviceErrors++;
                    else if (pkg.Targeted == AppTargeted.User) summary.UserErrors++;
                }

                var phaseKey = pkg.Targeted.ToString();
                if (!summary.AppsByPhase.TryGetValue(phaseKey, out var cnt)) cnt = 0;
                summary.AppsByPhase[phaseKey] = cnt + 1;
            }

            return summary;
        }

        private static Dictionary<string, List<FinalStatusPackageInfo>> BuildPackageStatesByPhase(
            IReadOnlyList<AppPackageState>? packageStates,
            IReadOnlyDictionary<string, AppInstallTiming> timings)
        {
            var result = new Dictionary<string, List<FinalStatusPackageInfo>>();
            if (packageStates == null) return result;

            foreach (var pkg in packageStates)
            {
                var phaseKey = pkg.Targeted.ToString();
                if (!result.TryGetValue(phaseKey, out var bucket))
                {
                    bucket = new List<FinalStatusPackageInfo>();
                    result[phaseKey] = bucket;
                }

                timings.TryGetValue(pkg.Id, out var timing);

                var isError = pkg.InstallationState == AppInstallationState.Error;
                bucket.Add(new FinalStatusPackageInfo
                {
                    AppName = string.IsNullOrEmpty(pkg.Name) ? pkg.Id : pkg.Name,
                    State = pkg.InstallationState.ToString(),
                    IsError = isError,
                    IsCompleted = IsCompleted(pkg.InstallationState),
                    Targeted = pkg.Targeted.ToString(),
                    StartedAt = timing?.StartedAtUtc?.ToString("o"),
                    CompletedAt = timing?.CompletedAtUtc?.ToString("o"),
                    DurationSeconds = timing?.DurationSeconds,
                    // Schema 2 — surface per-app error detail only when the app actually
                    // failed; healthy entries stay clean (NullValueHandling.Ignore).
                    ErrorPatternId = isError ? NullIfEmpty(pkg.ErrorPatternId) : null,
                    ErrorDetail = isError ? NullIfEmpty(pkg.ErrorDetail) : null,
                    ErrorCode = isError ? NullIfEmpty(pkg.ErrorCode) : null,
                });
            }

            return result;
        }

        private static bool IsCompleted(AppInstallationState s) =>
            s == AppInstallationState.Installed
            || s == AppInstallationState.Skipped
            || s == AppInstallationState.Postponed
            || s == AppInstallationState.Error;

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>
        /// Codex follow-up #5 — the final-status summary exposed the legacy
        /// <c>Hypothesis.Reason ?? Level</c> string. The equivalent derivable from
        /// <see cref="EnrollmentScenarioProfile"/> is the <c>Mode</c> name when known,
        /// falling back to the current <see cref="EnrollmentScenarioProfile.Reason"/>
        /// token, and finally to <c>"unknown"</c>.
        /// </summary>
        private static string DescribeEnrollmentType(EnrollmentScenarioProfile profile)
        {
            if (profile.Mode != EnrollmentMode.Unknown) return profile.Mode.ToString();
            if (!string.IsNullOrEmpty(profile.Reason)) return profile.Reason!;
            return "unknown";
        }
    }
}
