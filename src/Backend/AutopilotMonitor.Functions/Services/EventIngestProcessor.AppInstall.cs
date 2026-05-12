using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Per-app install summary aggregation — folds a batch of <c>app_install_*</c> +
    /// <c>download_progress</c> + <c>do_telemetry</c> events into an
    /// <see cref="AppInstallSummary"/> keyed by app name. Verbatim copy of the legacy helper;
    /// see <see cref="EventIngestProcessor"/> for the duplication rationale.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        private void AggregateAppInstallEvent(EnrollmentEvent evt, string tenantId, string sessionId, Dictionary<string, AppInstallAggregationState> summaries)
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
                    break;

                case "app_install_failed":
                    summary.Status = "Failed";
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
                    break;

                case "download_progress":
                    var bytesKey = evt.Data?.ContainsKey("bytesDownloaded") == true ? "bytesDownloaded"
                        : evt.Data?.ContainsKey("bytes_downloaded") == true ? "bytes_downloaded" : null;
                    if (bytesKey != null && long.TryParse(evt.Data![bytesKey]?.ToString(), out var bytes))
                        summary.DownloadBytes = Math.Max(summary.DownloadBytes, bytes);
                    break;

                case "do_telemetry":
                    if (evt.Data != null)
                    {
                        if (evt.Data.ContainsKey("doFileSize") && long.TryParse(evt.Data["doFileSize"]?.ToString(), out var doFs))
                        {
                            summary.DownloadBytes = Math.Max(summary.DownloadBytes, doFs);
                            summary.DoFileSize = doFs;
                        }
                        if (evt.Data.ContainsKey("doTotalBytesDownloaded") && long.TryParse(evt.Data["doTotalBytesDownloaded"]?.ToString(), out var doTotalDl))
                            summary.DoTotalBytesDownloaded = doTotalDl;
                        if (evt.Data.ContainsKey("doBytesFromPeers") && long.TryParse(evt.Data["doBytesFromPeers"]?.ToString(), out var doPeers))
                            summary.DoBytesFromPeers = doPeers;
                        if (evt.Data.ContainsKey("doBytesFromHttp") && long.TryParse(evt.Data["doBytesFromHttp"]?.ToString(), out var doHttp))
                            summary.DoBytesFromHttp = doHttp;
                        if (evt.Data.ContainsKey("doPercentPeerCaching") && int.TryParse(evt.Data["doPercentPeerCaching"]?.ToString(), out var doPct))
                            summary.DoPercentPeerCaching = doPct;
                        if (evt.Data.ContainsKey("doDownloadMode") && int.TryParse(evt.Data["doDownloadMode"]?.ToString(), out var doMode))
                            summary.DoDownloadMode = doMode;
                        if (evt.Data.ContainsKey("doDownloadDuration"))
                        {
                            var doDurStr = evt.Data["doDownloadDuration"]?.ToString() ?? string.Empty;
                            summary.DoDownloadDuration = doDurStr;
                            if (TimeSpan.TryParse(doDurStr, out var doDurTs) && doDurTs.TotalSeconds >= 1)
                                summary.DownloadDurationSeconds = Math.Max(summary.DownloadDurationSeconds, (int)doDurTs.TotalSeconds);
                        }
                        if (evt.Data.ContainsKey("doBytesFromLanPeers") && long.TryParse(evt.Data["doBytesFromLanPeers"]?.ToString(), out var doLan))
                            summary.DoBytesFromLanPeers = doLan;
                        if (evt.Data.ContainsKey("doBytesFromGroupPeers") && long.TryParse(evt.Data["doBytesFromGroupPeers"]?.ToString(), out var doGroup))
                            summary.DoBytesFromGroupPeers = doGroup;
                        if (evt.Data.ContainsKey("doBytesFromInternetPeers") && long.TryParse(evt.Data["doBytesFromInternetPeers"]?.ToString(), out var doInet))
                            summary.DoBytesFromInternetPeers = doInet;
                        if (evt.Data.ContainsKey("doBytesFromLinkLocalPeers") && long.TryParse(evt.Data["doBytesFromLinkLocalPeers"]?.ToString(), out var doLinkLocal))
                            summary.DoBytesFromLinkLocalPeers = doLinkLocal;
                        if (evt.Data.ContainsKey("doBytesFromCacheServer") && long.TryParse(evt.Data["doBytesFromCacheServer"]?.ToString(), out var doCache))
                            summary.DoBytesFromCacheServer = doCache;
                        if (evt.Data.ContainsKey("doCacheHost"))
                            summary.DoCacheHost = evt.Data["doCacheHost"]?.ToString() ?? string.Empty;
                    }
                    break;
            }

            IngestEventsFunction.RecalculateAppDurations(state);
        }
    }
}
