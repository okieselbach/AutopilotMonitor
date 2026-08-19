using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Per-app install summary aggregation — folds a batch of <c>app_install_*</c> +
    /// <c>download_progress</c> + <c>do_telemetry</c> events into an
    /// <see cref="AppInstallSummary"/> keyed by app name.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        // internal static (was private instance): pure function over its parameters — exposed as a
        // test seam so the TerminalState / status-fold contract is pinned by unit tests (PR0).
        internal static void AggregateAppInstallEvent(EnrollmentEvent evt, string tenantId, string sessionId, Dictionary<string, AppInstallAggregationState> summaries)
        {
            bool isRelevant =
                evt.EventType == "app_install_started" || evt.EventType == "app_install_start" ||
                evt.EventType == "app_install_completed" || evt.EventType == "app_install_complete" ||
                evt.EventType == "app_install_failed" ||
                evt.EventType == "app_download_started" ||
                evt.EventType == "app_install_skipped" ||
                evt.EventType == "download_progress" ||
                evt.EventType == "do_telemetry";

            if (!isRelevant) return;

            // Legacy-agent stale-replay guard (session eaf3d8c4): app events replayed from a
            // previous enrollment's IME log carry a rejectedSourceTimestamp > 24 h older than
            // the event stamp. They would create/overwrite this session's AppInstallSummaries
            // rows with week-old runs. The fixed agent suppresses them at the source; this
            // covers agents not yet rolled out.
            if (IsHistoricImeReplay(evt)) return;

            var appName = evt.Data?.ContainsKey("appName") == true ? evt.Data["appName"]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(appName)) return;

            if (!summaries.TryGetValue(appName, out var state))
            {
                state = new AppInstallAggregationState
                {
                    Summary = new AppInstallSummary
                    {
                        AppName = appName,
                        SessionId = sessionId,
                        TenantId = tenantId,
                        StartedAt = evt.Timestamp
                    }
                };
                summaries[appName] = state;
            }

            var summary = state.Summary;

            if (evt.Data != null)
            {
                // F1 PR1 (audit Q3): the summaries dict — like the RowKey — is keyed by app NAME,
                // but events carry the Intune app identity in `appId`. Adopt the first observed
                // appId; if the same name later shows a DIFFERENT appId (device- + user-scope
                // assignment, duplicate display names across rings), flag the row as a collision —
                // its folded status/duration mixes two real apps and per-app fleet aggregates
                // must exclude it. First-seen appId wins so the row stays deterministic.
                if (evt.Data.TryGetValue("appId", out var appIdObj))
                {
                    var appId = appIdObj?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(appId))
                    {
                        if (string.IsNullOrEmpty(summary.AppId))
                            summary.AppId = appId!;
                        else if (!string.Equals(summary.AppId, appId, StringComparison.OrdinalIgnoreCase))
                            summary.AppIdCollision = true;
                    }
                }
                if (evt.Data.TryGetValue("appVersion", out var appVersionObj))
                {
                    var appVersion = appVersionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(appVersion))
                        summary.AppVersion = appVersion.Trim();
                }
                if (evt.Data.TryGetValue("appType", out var appTypeObj))
                {
                    var appType = appTypeObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(appType))
                        summary.AppType = appType.Trim();
                }
                if (evt.Data.TryGetValue("attemptNumber", out var attemptObj) &&
                    int.TryParse(attemptObj?.ToString(), out var attempt) && attempt > 0)
                {
                    summary.AttemptNumber = Math.Max(summary.AttemptNumber, attempt);
                }
                if (evt.Data.TryGetValue("installerPhase", out var phaseObj))
                {
                    var phase = phaseObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(phase))
                        summary.InstallerPhase = phase.Trim();
                }
                if (evt.Data.TryGetValue("exitCode", out var exitCodeObj) &&
                    int.TryParse(exitCodeObj?.ToString(), out var exitCode))
                {
                    summary.ExitCode = exitCode;
                }
                if (evt.Data.TryGetValue("detectionResult", out var detectionObj))
                {
                    var detection = detectionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(detection))
                        summary.DetectionResult = detection.Trim();
                }
            }

            switch (evt.EventType)
            {
                case "app_install_started":
                case "app_install_start":
                    if (!state.InstallStartedAt.HasValue || evt.Timestamp < state.InstallStartedAt.Value)
                        state.InstallStartedAt = evt.Timestamp;
                    if (summary.Status == "InProgress" || summary.Status == string.Empty)
                        summary.Status = "InProgress";
                    break;

                case "app_download_started":
                    if (!state.DownloadStartedAt.HasValue || evt.Timestamp < state.DownloadStartedAt.Value)
                        state.DownloadStartedAt = evt.Timestamp;
                    if (summary.Status == "InProgress" || summary.Status == string.Empty)
                        summary.Status = "InProgress";
                    break;

                case "app_install_completed":
                case "app_install_complete":
                    summary.Status = "Succeeded";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, EventTimestampValidator.SafeDurationSeconds(summary.StartedAt, evt.Timestamp));
                    // PR0 (2026-07-26): the agent emits app_install_completed for EVERY terminal
                    // transition — including Skipped/Postponed (V1 wire parity). The payload's
                    // `state` field is the only way to tell a real install apart from a no-op
                    // (e.g. WinGet "Update for X" policies that were not applicable). Persist it
                    // so metrics can exclude skips from durations and rates. Unknown/absent state
                    // leaves the sentinel empty — never guessed.
                    var terminalStateRaw = evt.Data?.ContainsKey("state") == true
                        ? evt.Data["state"]?.ToString() : null;
                    if (terminalStateRaw == "Installed" || terminalStateRaw == "Skipped" || terminalStateRaw == "Postponed")
                        summary.TerminalState = terminalStateRaw;
                    break;

                case "app_install_failed":
                    summary.Status = "Failed";
                    summary.TerminalState = "Error";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, EventTimestampValidator.SafeDurationSeconds(summary.StartedAt, evt.Timestamp));
                    // FailureCode preference: canonical `failureType` > raw `errorCode` > empty.
                    // c117946b debrief (2026-05-12): the V2 termination handler tags promoted
                    // "likely stuck" apps with failureType=esp_apps_timeout so the UI can
                    // distinguish confirmed failures from ESP-timeout-induced presumptions.
                    var failureType = evt.Data?.ContainsKey("failureType") == true
                        ? evt.Data["failureType"]?.ToString() : null;
                    var errorCodeRaw = evt.Data?.ContainsKey("errorCode") == true
                        ? evt.Data["errorCode"]?.ToString() : null;
                    summary.FailureCode = !string.IsNullOrWhiteSpace(failureType)
                        ? failureType!
                        : (errorCodeRaw ?? string.Empty);
                    // FailureMessage preference: explicit `errorMessage` > `errorDetail` > evt.Message.
                    var errorMessage = evt.Data?.ContainsKey("errorMessage") == true
                        ? evt.Data["errorMessage"]?.ToString() : null;
                    var errorDetail = evt.Data?.ContainsKey("errorDetail") == true
                        ? evt.Data["errorDetail"]?.ToString() : null;
                    summary.FailureMessage = !string.IsNullOrWhiteSpace(errorMessage)
                        ? errorMessage!
                        : (!string.IsNullOrWhiteSpace(errorDetail) ? errorDetail! : (evt.Message ?? string.Empty));
                    break;

                case "app_install_skipped":
                    // Skipped is treated as terminal-success unless we already have a real terminal.
                    // Empty (sentinel: no observation yet) and "InProgress" both flip to Succeeded.
                    if (summary.Status == "InProgress" || summary.Status == string.Empty)
                        summary.Status = "Succeeded";
                    // Same guard for the terminal state: a dedicated skipped event never overrides
                    // a stronger terminal (Installed/Error) already seen in this batch.
                    if (string.IsNullOrEmpty(summary.TerminalState))
                        summary.TerminalState = "Skipped";
                    break;

                case "download_progress":
                    var bytesKey = evt.Data?.ContainsKey("bytesDownloaded") == true ? "bytesDownloaded"
                        : evt.Data?.ContainsKey("bytes_downloaded") == true ? "bytes_downloaded" : null;
                    if (bytesKey != null && long.TryParse(evt.Data![bytesKey]?.ToString(), out var bytes))
                        summary.DownloadBytes = Math.Max(summary.DownloadBytes, bytes);
                    // DO fallback: IME >= 1.104 removed the [DO TEL] log line, and the agent's
                    // DO collector only emits do_telemetry for downloads whose completion a poll
                    // itself observed — fast, interrupted or between-poll-completed downloads
                    // never get one. The progress events carry the full do* set on every poll,
                    // so fold them in monotonically; a do_telemetry (below) stays authoritative.
                    if (evt.Data != null)
                        ApplyDoFields(summary, evt.Data, isAuthoritative: false);
                    break;

                case "do_telemetry":
                    if (evt.Data != null)
                        ApplyDoFields(summary, evt.Data, isAuthoritative: true);
                    break;
            }

            RecalculateAppDurations(state);
        }

        /// <summary>
        /// Folds the <c>do*</c> Delivery Optimization fields of one event into the summary.
        /// Two writers share this so they cannot drift:
        /// <list type="bullet">
        /// <item><c>do_telemetry</c> (<paramref name="isAuthoritative"/> = true) — the collector's
        /// final per-app read: last-write-wins per present key (pre-existing semantics), except
        /// <c>DoDownloadMode</c> never regresses to the -1 "unset" sentinel — that would re-hide
        /// the row from <c>DoAggregator</c> after a progress event already established a mode.</item>
        /// <item><c>download_progress</c> (false) — per-poll observations: byte counters and file
        /// size fold via <c>Math.Max</c> (monotonic per download, so replays and out-of-order
        /// batches stay idempotent); mode/percent/cacheHost are latest-observation writes.</item>
        /// </list>
        /// <c>DownloadBytes</c> prefers actually-transferred bytes (<c>doTotalBytesDownloaded</c>)
        /// over <c>doFileSize</c> — the file size is only the fallback when no transfer total exists
        /// (it used to unconditionally inflate the transfer measure).
        /// </summary>
        internal static void ApplyDoFields(AppInstallSummary summary, Dictionary<string, object> data, bool isAuthoritative)
        {
            static bool TryGetLong(Dictionary<string, object> d, string key, out long value)
            {
                value = 0;
                return d.TryGetValue(key, out var raw) && long.TryParse(raw?.ToString(), out value);
            }
            static bool TryGetInt(Dictionary<string, object> d, string key, out int value)
            {
                value = 0;
                return d.TryGetValue(key, out var raw) && int.TryParse(raw?.ToString(), out value);
            }
            static long Fold(bool authoritative, long current, long incoming)
                => authoritative ? incoming : Math.Max(current, incoming);

            var hasFileSize = TryGetLong(data, "doFileSize", out var doFs);
            var hasTotalDl = TryGetLong(data, "doTotalBytesDownloaded", out var doTotalDl);

            if (hasFileSize)
                summary.DoFileSize = Fold(isAuthoritative, summary.DoFileSize, doFs);
            if (hasTotalDl)
                summary.DoTotalBytesDownloaded = Fold(isAuthoritative, summary.DoTotalBytesDownloaded, doTotalDl);

            // Transfer measure: real transferred bytes when known, file size only as fallback.
            // Progress events already fed DownloadBytes via their bytesDownloaded field, so only
            // the authoritative telemetry contributes here.
            if (isAuthoritative && (hasFileSize || hasTotalDl))
                summary.DownloadBytes = Math.Max(summary.DownloadBytes, hasTotalDl && doTotalDl > 0 ? doTotalDl : doFs);

            if (TryGetLong(data, "doBytesFromPeers", out var doPeers))
                summary.DoBytesFromPeers = Fold(isAuthoritative, summary.DoBytesFromPeers, doPeers);
            if (TryGetLong(data, "doBytesFromHttp", out var doHttp))
                summary.DoBytesFromHttp = Fold(isAuthoritative, summary.DoBytesFromHttp, doHttp);
            if (TryGetLong(data, "doBytesFromLanPeers", out var doLan))
                summary.DoBytesFromLanPeers = Fold(isAuthoritative, summary.DoBytesFromLanPeers, doLan);
            if (TryGetLong(data, "doBytesFromGroupPeers", out var doGroup))
                summary.DoBytesFromGroupPeers = Fold(isAuthoritative, summary.DoBytesFromGroupPeers, doGroup);
            if (TryGetLong(data, "doBytesFromInternetPeers", out var doInet))
                summary.DoBytesFromInternetPeers = Fold(isAuthoritative, summary.DoBytesFromInternetPeers, doInet);
            if (TryGetLong(data, "doBytesFromLinkLocalPeers", out var doLinkLocal))
                summary.DoBytesFromLinkLocalPeers = Fold(isAuthoritative, summary.DoBytesFromLinkLocalPeers, doLinkLocal);
            if (TryGetLong(data, "doBytesFromCacheServer", out var doCache))
                summary.DoBytesFromCacheServer = Fold(isAuthoritative, summary.DoBytesFromCacheServer, doCache);

            if (TryGetInt(data, "doPercentPeerCaching", out var doPct))
                summary.DoPercentPeerCaching = doPct;

            // -1 is the "unset" sentinel DoAggregator filters on — never let it clobber a
            // known mode (a telemetry event without the DownloadMode property parses as -1).
            if (TryGetInt(data, "doDownloadMode", out var doMode) && doMode >= 0)
                summary.DoDownloadMode = doMode;

            if (data.TryGetValue("doCacheHost", out var cacheHostRaw))
            {
                var cacheHost = cacheHostRaw?.ToString();
                if (isAuthoritative || !string.IsNullOrEmpty(cacheHost))
                    summary.DoCacheHost = cacheHost ?? string.Empty;
            }

            // Only do_telemetry carries a duration; progress events never have the key.
            if (data.ContainsKey("doDownloadDuration"))
            {
                var doDurStr = data["doDownloadDuration"]?.ToString() ?? string.Empty;
                summary.DoDownloadDuration = doDurStr;
                if (TimeSpan.TryParse(doDurStr, out var doDurTs) && doDurTs.TotalSeconds >= 1)
                    summary.DownloadDurationSeconds = Math.Max(summary.DownloadDurationSeconds, (int)doDurTs.TotalSeconds);
            }
        }

        internal static void RecalculateAppDurations(AppInstallAggregationState state)
        {
            var summary = state.Summary;

            // Effective start for full app duration: earliest known install/download start.
            var effectiveStart = summary.StartedAt;
            if (state.DownloadStartedAt.HasValue &&
                (effectiveStart == DateTime.MinValue || state.DownloadStartedAt.Value < effectiveStart))
            {
                effectiveStart = state.DownloadStartedAt.Value;
            }

            if (state.InstallStartedAt.HasValue &&
                (effectiveStart == DateTime.MinValue || state.InstallStartedAt.Value < effectiveStart))
            {
                effectiveStart = state.InstallStartedAt.Value;
            }

            if (effectiveStart != DateTime.MinValue)
            {
                summary.StartedAt = effectiveStart;
            }

            // Download duration: from first download start to first install start.
            if (state.DownloadStartedAt.HasValue && state.InstallStartedAt.HasValue &&
                state.InstallStartedAt.Value >= state.DownloadStartedAt.Value)
            {
                summary.DownloadDurationSeconds = EventTimestampValidator.SafeDurationSeconds(
                    state.DownloadStartedAt.Value, state.InstallStartedAt.Value);
            }

            // Full duration: from effective start to completion/failure.
            if (summary.CompletedAt.HasValue && summary.StartedAt != DateTime.MinValue &&
                summary.CompletedAt.Value >= summary.StartedAt)
            {
                summary.DurationSeconds = EventTimestampValidator.SafeDurationSeconds(
                    summary.StartedAt, summary.CompletedAt.Value);
            }
        }
    }

    internal class AppInstallAggregationState
    {
        public AppInstallSummary Summary { get; set; } = new();
        public DateTime? DownloadStartedAt { get; set; }
        public DateTime? InstallStartedAt { get; set; }
    }
}
