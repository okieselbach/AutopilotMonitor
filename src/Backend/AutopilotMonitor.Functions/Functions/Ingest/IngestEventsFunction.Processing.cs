using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Partial: Core ingest processing logic — event classification, session updates,
    /// rule analysis, Teams notifications, and SignalR message construction.
    /// </summary>
    public partial class IngestEventsFunction
    {
        /// <summary>
        /// Core ingest logic: device block check, NDJSON parsing, event storage, rule engine, SignalR.
        /// Called by both the cert-auth Run() method and the bootstrap wrapper.
        /// </summary>
        internal async Task<IngestEventsOutput> ProcessIngestAsync(HttpRequestData req, string tenantId, SecurityValidationResult validation)
        {
                // --- Device block check (after security, before body decompression) ---
                // Check if this device has been administratively blocked (e.g. rogue device sending excessive data).
                // We read the serial number from the header (same header used by AutopilotDeviceValidator).
                // Using HTTP 200 with DeviceBlocked=true so the agent does not trigger its auth-failure circuit breaker.
                //
                // Session-aware blocking: maintenance auto-blocks store BlockedSessionIds. If the current
                // ingest is from a DIFFERENT session (new enrollment), the device is auto-unblocked.
                // Kill actions and manual (whole-device) blocks are never auto-unblocked.
                var serialNumberHeader = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                    : null;

                IngestEventsRequest? earlyParsedRequest = null;

                if (!string.IsNullOrEmpty(serialNumberHeader))
                {
                    var (isBlocked, unblockAt, blockAction, blockedSessionIds) = await _blockedDeviceService.IsBlockedAsync(tenantId, serialNumberHeader);
                    if (isBlocked)
                    {
                        // Session-aware block: parse body to check if this is a different (new) session
                        if (!string.IsNullOrEmpty(blockedSessionIds))
                        {
                            earlyParsedRequest = await ParseNdjsonRequest(req.Body, tenantId);
                            if (earlyParsedRequest != null && !string.IsNullOrEmpty(earlyParsedRequest.SessionId))
                            {
                                var (stillBlocked, _, _, _) = await _blockedDeviceService.IsBlockedAsync(
                                    tenantId, serialNumberHeader, earlyParsedRequest.SessionId);

                                if (!stillBlocked)
                                {
                                    // Auto-unblocked — fall through to normal processing with already-parsed request
                                    isBlocked = false;
                                }
                            }
                        }

                        if (isBlocked)
                        {
                            var isKill = string.Equals(blockAction, "Kill", StringComparison.OrdinalIgnoreCase);

                            _logger.LogWarning(
                                "{Action} ingest from device: TenantId={TenantId}, SerialNumber={SerialNumber}, UnblockAt={UnblockAt}",
                                isKill ? "KILL signal for" : "Rejected", tenantId, serialNumberHeader, unblockAt);

                            var blockedHttpResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
                            await blockedHttpResponse.WriteAsJsonAsync(new IngestEventsResponse
                            {
                                Success = false,
                                DeviceBlocked = true,
                                DeviceKillSignal = isKill,
                                UnblockAt = unblockAt,
                                Message = isKill
                                    ? "Device has been issued a remote kill signal."
                                    : "Device is temporarily blocked by an administrator.",
                                ProcessedAt = DateTime.UtcNow
                            });
                            return new IngestEventsOutput
                            {
                                HttpResponse = blockedHttpResponse,
                                SignalRMessages = Array.Empty<SignalRMessageAction>()
                            };
                        }
                    }
                }

                // --- Version block check (global, applies to all tenants) ---
                var agentVersionHeader = req.Headers.Contains("X-Agent-Version")
                    ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault()
                    : null;

                if (!string.IsNullOrEmpty(agentVersionHeader))
                {
                    var (isVersionBlocked, versionAction, matchedPattern) = await _blockedVersionService.IsVersionBlockedAsync(agentVersionHeader);
                    if (isVersionBlocked)
                    {
                        var isVersionKill = string.Equals(versionAction, "Kill", StringComparison.OrdinalIgnoreCase);

                        _logger.LogWarning(
                            "Version {Action} for agent: TenantId={TenantId}, AgentVersion={AgentVersion}, MatchedPattern={Pattern}",
                            isVersionKill ? "KILL" : "BLOCK", tenantId, agentVersionHeader, matchedPattern);

                        var versionBlockedResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
                        await versionBlockedResponse.WriteAsJsonAsync(new IngestEventsResponse
                        {
                            Success = false,
                            DeviceBlocked = true,
                            DeviceKillSignal = isVersionKill,
                            Message = isVersionKill
                                ? $"Agent version {agentVersionHeader} has been issued a remote kill signal (pattern: {matchedPattern})."
                                : $"Agent version {agentVersionHeader} is blocked by administrator (pattern: {matchedPattern}).",
                            ProcessedAt = DateTime.UtcNow
                        });
                        return new IngestEventsOutput
                        {
                            HttpResponse = versionBlockedResponse,
                            SignalRMessages = Array.Empty<SignalRMessageAction>()
                        };
                    }
                }

                // --- Parse NDJSON+gzip request body (only after security is cleared) ---
                // If session-aware block check already parsed the body, reuse that result (stream is consumed)
                var request = earlyParsedRequest ?? await ParseNdjsonRequest(req.Body, tenantId);

                if (request?.Events == null || request.Events.Count == 0)
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "No events provided");
                }

                if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.TenantId))
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "SessionId and TenantId are required");
                }

                // Ensure body TenantId matches the validated header TenantId (prevent body spoofing)
                if (!string.Equals(request.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("TenantId mismatch: header={HeaderTenantId}, body={BodyTenantId}", tenantId, request.TenantId);
                    return await CreateErrorResponse(req, HttpStatusCode.Forbidden, "TenantId mismatch between header and payload");
                }

                var sessionPrefix = $"[Session: {request.SessionId.Substring(0, Math.Min(8, request.SessionId.Length))}]";
                _logger.LogInformation($"{sessionPrefix} IngestEvents: Processing {request.Events.Count} events (Device: {validation.CertificateThumbprint}, Hardware: {validation.Manufacturer} {validation.Model}, Rate: {validation.RateLimitResult?.RequestsInWindow}/{validation.RateLimitResult?.MaxRequests})");

                // Stamp server-side receive time, and authoritative TenantId/SessionId on all events.
                // TenantId/SessionId come from the validated request metadata — never trust per-event agent values.
                var receivedAt = DateTime.UtcNow;
                StampServerFields(request.Events, request.TenantId, request.SessionId, receivedAt);

                // Sanitize agent-side timestamps: clamp out-of-range values to prevent storage/sorting issues.
                // Original timestamps are preserved in OriginalTimestamp for troubleshooting.
                SanitizeEventTimestamps(request.Events, receivedAt, _logger);

                // Store events in Azure Table Storage (batch write for efficiency)
                var storedEvents = await _sessionRepo.StoreEventsBatchAsync(request.Events);
                int processedCount = storedEvents.Count;

                // Update EventTypeIndex and DeviceSnapshot indexes (fire-and-forget, non-blocking)
                var indexTenantId = request.TenantId;
                var indexSessionId = request.SessionId;
                var indexEvents = storedEvents.ToList();
                _ = Task.WhenAll(
                    _sessionRepo.UpsertEventTypeIndexBatchAsync(
                        indexTenantId, indexSessionId, indexEvents),
                    _sessionRepo.UpsertDeviceSnapshotAsync(
                        indexTenantId, indexSessionId, indexEvents)
                ).ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                    "Index update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);

                // Store IME agent version on session if detected in this batch
                var imeVersionEvent = request.Events.FirstOrDefault(e =>
                    e.EventType == "ime_agent_version" && e.Data?.ContainsKey("agentVersion") == true);
                if (imeVersionEvent != null)
                {
                    var imeVersion = imeVersionEvent.Data["agentVersion"]?.ToString();
                    if (!string.IsNullOrEmpty(imeVersion))
                    {
                        _ = _sessionRepo.UpdateSessionImeAgentVersionAsync(request.TenantId, request.SessionId, imeVersion)
                            .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                                "ImeAgentVersion update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);

                        // Track IME version in permanent history (fire-and-forget, non-blocking)
                        _ = _sessionRepo.RecordImeVersionAsync(imeVersion, request.TenantId, request.SessionId)
                            .ContinueWith(async t =>
                            {
                                if (t.IsFaulted)
                                {
                                    _logger.LogWarning(t.Exception?.InnerException,
                                        "ImeVersionHistory update failed (non-fatal)");
                                }
                                else if (t.Result)
                                {
                                    // New version discovered — log OpsEvent
                                    await _opsEventService.RecordNewImeVersionDetectedAsync(
                                        imeVersion, request.TenantId, request.SessionId);
                                }
                            }, TaskScheduler.Default);
                    }
                }

                // Classify events for downstream processing
                var classification = ClassifyEvents(storedEvents);

                // Store app install summaries
                foreach (var summary in classification.AppInstallUpdates.Values)
                {
                    await _metricsRepo.StoreAppInstallSummaryAsync(summary.Summary);
                }

                // Extract geo-location data and merge into session row (fire-and-forget —
                // subsequent event batches arrive frequently so the UI sees it almost immediately)
                if (classification.DeviceLocationEvent?.Data != null)
                {
                    var geoData = classification.DeviceLocationEvent.Data;
                    var geoTenantId = request.TenantId;
                    var geoSessionId = request.SessionId;
                    _ = _sessionRepo.UpdateSessionGeoAsync(
                        geoTenantId,
                        geoSessionId,
                        geoData.ContainsKey("country") ? geoData["country"]?.ToString() : null,
                        geoData.ContainsKey("region") ? geoData["region"]?.ToString() : null,
                        geoData.ContainsKey("city") ? geoData["city"]?.ToString() : null,
                        geoData.ContainsKey("loc") ? geoData["loc"]?.ToString() : null
                    ).ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget UpdateSessionGeoAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
                }

                // Update session status based on events
                var (statusTransitioned, whiteGloveStatusTransitioned, failureReason) = await UpdateSessionStatusAsync(
                    request, sessionPrefix, classification);

                // A terminal batch (one that drives Succeeded/Failed) takes its RebootCount from the
                // authoritative reconcile below, NOT the per-batch increment — otherwise the reboot
                // events would be added by the increment AND counted by the reconcile (double-count).
                // Non-terminal batches keep incrementing for a live in-flight value.
                var isTerminalBatch = classification.CompletionEvent != null
                    || classification.FailureEvent != null
                    || classification.EspFailureEvent != null
                    || classification.GatherCompletionEvent != null;

                // Always increment event count when events were stored
                if (processedCount > 0)
                {
                    await _sessionRepo.IncrementSessionEventCountAsync(
                        request.TenantId,
                        request.SessionId,
                        processedCount,
                        classification.EarliestEventTimestamp,
                        classification.LatestEventTimestamp,
                        currentPhase: classification.LastPhaseChangeEvent?.Phase,
                        platformScriptIncrement: classification.PlatformScriptCount,
                        remediationScriptIncrement: classification.RemediationScriptCount,
                        rebootIncrement: isTerminalBatch ? 0 : classification.RebootCount
                    );
                }

                // Authoritative reboot reconcile: the LAST reboot write on terminal batches. Overwrites
                // the live incremental value (self-correcting any at-least-once double-count) and runs
                // even on already-terminal batch replays where UpdateSessionStatusAsync no-ops.
                // Idempotent (no-ops when already correct) and fail-soft.
                if (isTerminalBatch)
                    await _sessionRepo.ReconcileSessionRebootCountAsync(request.TenantId, request.SessionId);

                // Auto-analyze fan-out: enqueue a queue message instead of running fire-and-forget
                // Task.Run inside the function. The previous in-function approach could be killed
                // mid-flight by Functions scale-in (HTTP 200 returned → worker unloaded → rules
                // never persisted → user had to click "Analyze Now"). The queue worker runs the
                // RuleEngine in a separate invocation with retry + poison-queue semantics.
                // Manual "Analyze Now" remains as the user-side fallback if the enqueue itself
                // fails (producer is fail-soft and never throws on send errors).
                //
                // newRuleResults stays empty here — the rule engine now runs asynchronously and
                // results are not available before SendWebhookNotificationsAsync below. Webhooks
                // never received auto-analyze results in the previous fire-and-forget design either.
                var newRuleResults = new List<AutopilotMonitor.Shared.Models.RuleResult>();
                if (classification.CompletionEvent != null || classification.FailureEvent != null)
                {
                    await _analyzeProducer.EnqueueAsync(new AutopilotMonitor.Shared.Models.AnalyzeOnEnrollmentEndEnvelope
                    {
                        TenantId = request.TenantId,
                        SessionId = request.SessionId,
                        Reason = classification.CompletionEvent != null
                            ? AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndHandler.ReasonEnrollmentComplete
                            : AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndHandler.ReasonEnrollmentFailed,
                        EnqueuedAt = DateTime.UtcNow,
                    });
                }

                // Vulnerability Correlation — fire-and-forget when shutdown inventory arrives.
                // Runs asynchronously so it never blocks the HTTP response to the agent.
                // The report is stored in the VulnerabilityCache table (not Events) because
                // it is a server-side analysis result, not an agent event.
                var shutdownInventoryDetected = storedEvents.Any(e =>
                    e.EventType == AutopilotMonitor.Shared.Constants.EventTypes.SoftwareInventoryAnalysis &&
                    e.Data != null &&
                    e.Data.ContainsKey("triggered_at") &&
                    e.Data["triggered_at"]?.ToString() == "shutdown" &&
                    e.Data.ContainsKey("chunk_index") &&
                    Convert.ToInt32(e.Data["chunk_index"]) == 0);

                if (shutdownInventoryDetected)
                {
                    // Capture everything we need before going async (request object may be disposed)
                    var capturedSessionId = request.SessionId;
                    var capturedTenantId = request.TenantId;
                    var capturedPrefix = sessionPrefix;

                    // Merge all inventory chunks from this batch into a self-contained list
                    var allInventoryItems = new List<Dictionary<string, object>>();
                    var inventoryChunks = storedEvents
                        .Where(e => e.EventType == AutopilotMonitor.Shared.Constants.EventTypes.SoftwareInventoryAnalysis &&
                            e.Data != null &&
                            e.Data.ContainsKey("triggered_at") &&
                            e.Data["triggered_at"]?.ToString() == "shutdown" &&
                            e.Data.ContainsKey("inventory"))
                        .OrderBy(e => Convert.ToInt32(e.Data.GetValueOrDefault("chunk_index", 0)))
                        .ToList();

                    foreach (var chunk in inventoryChunks)
                    {
                        if (chunk.Data["inventory"] is System.Collections.IEnumerable items)
                        {
                            foreach (var item in items)
                            {
                                if (item is Dictionary<string, object> dict)
                                    allInventoryItems.Add(dict);
                            }
                        }
                    }

                    // Detect White Glove part tag from inventory events (1 = pre-provisioning, 2 = user enrollment)
                    int? whiteGlovePart = null;
                    var firstShutdownChunk = inventoryChunks.FirstOrDefault();
                    if (firstShutdownChunk?.Data != null &&
                        firstShutdownChunk.Data.TryGetValue("whiteglove_part", out var wgPartObj))
                    {
                        whiteGlovePart = Convert.ToInt32(wgPartObj);
                    }

                    if (allInventoryItems.Count > 0)
                    {
                        var capturedWhiteGlovePart = whiteGlovePart;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                                if (adminConfig?.VulnerabilityCorrelationEnabled != true)
                                    return;

                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                                var reportData = await _vulnerabilityCorrelation.CorrelateAsync(
                                    capturedSessionId, capturedTenantId, allInventoryItems, cts.Token);

                                if (reportData != null)
                                {
                                    // Tag findings with phase label for White Glove scenarios
                                    var phaseLabel = capturedWhiteGlovePart == 1 ? "device_setup"
                                        : capturedWhiteGlovePart == 2 ? "user_enrollment"
                                        : (string?)null;

                                    if (phaseLabel != null && reportData.ContainsKey("findings")
                                        && reportData["findings"] is List<Dictionary<string, object>> tagFindings)
                                    {
                                        foreach (var f in tagFindings)
                                            f.TryAdd("phase", phaseLabel);
                                    }

                                    // White Glove Part 2: merge with existing Part 1 report
                                    if (capturedWhiteGlovePart == 2)
                                    {
                                        try
                                        {
                                            var existingReport = await _vulnRepo.GetVulnerabilityReportAsync(
                                                capturedTenantId, capturedSessionId);
                                            if (existingReport != null)
                                            {
                                                reportData = VulnerabilityCorrelationService.MergeReports(
                                                    existingReport, reportData,
                                                    existingPhaseLabel: "device_setup",
                                                    newPhaseLabel: "user_enrollment");
                                                _logger.LogInformation(
                                                    "{Prefix} WhiteGlove Part 2: merged vulnerability report with Part 1 findings",
                                                    capturedPrefix);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning(ex,
                                                "{Prefix} Failed to load Part 1 report for merge (storing Part 2 standalone)",
                                                capturedPrefix);
                                        }
                                    }

                                    await _vulnRepo.StoreVulnerabilityReportAsync(
                                        capturedTenantId, capturedSessionId, reportData);
                                    _logger.LogInformation("{Prefix} Vulnerability correlation complete (async, whiteGlovePart={Part})",
                                        capturedPrefix, capturedWhiteGlovePart?.ToString() ?? "none");

                                    // Update CveIndex for searchable CVE queries
                                    var findings = reportData.ContainsKey("findings")
                                        ? reportData["findings"] as List<Dictionary<string, object>>
                                        : null;
                                    if (findings != null && findings.Count > 0)
                                    {
                                        _ = _sessionRepo.UpsertCveIndexEntriesAsync(capturedTenantId, capturedSessionId, findings)
                                            .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                                                "CveIndex update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);
                                    }

                                    // Push SignalR notification so the UI fetches the report immediately
                                    var overallRisk = reportData.ContainsKey("scan_summary")
                                        && reportData["scan_summary"] is Dictionary<string, object> summary
                                        && summary.ContainsKey("overall_risk")
                                        ? summary["overall_risk"]?.ToString() ?? "unknown"
                                        : "unknown";
                                    await _signalRNotification.NotifyVulnerabilityReportAvailableAsync(
                                        capturedTenantId, capturedSessionId, overallRisk);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "{Prefix} Vulnerability correlation failed (async, non-fatal)", capturedPrefix);
                            }
                        });
                    }
                }

                // Increment platform stats (fire-and-forget, non-blocking)
                // Note: IssuesDetected is now incremented inside the async rule engine task above.
                _ = _metricsRepo.IncrementPlatformStatAsync("TotalEventsProcessed", processedCount)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
                if (classification.CompletionEvent != null)
                    _ = _metricsRepo.IncrementPlatformStatAsync("SuccessfulEnrollments")
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

                // Record gather rule telemetry for events that carry ruleId in their data
                _ = RecordGatherRuleStatsAsync(request.TenantId, storedEvents)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget RecordGatherRuleStatsAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

                // Store diagnostics blob name + destination on session (if agent uploaded a diagnostics package)
                if (classification.DiagnosticsUploadedEvent != null)
                {
                    var data = classification.DiagnosticsUploadedEvent.Data;
                    var blobName = data?.ContainsKey("blobName") == true
                        ? data["blobName"]?.ToString()
                        : null;
                    // Older agents don't send `destination` — pass null, repo leaves the
                    // column unchanged (legacy-row default at read-time is CustomerSas).
                    var destination = data?.ContainsKey("destination") == true
                        ? data["destination"]?.ToString()
                        : null;
                    if (!string.IsNullOrEmpty(blobName))
                    {
                        await _sessionRepo.UpdateSessionDiagnosticsBlobAsync(
                            request.TenantId, request.SessionId, blobName,
                            string.IsNullOrEmpty(destination) ? null : destination);
                    }
                }

                // Retrieve updated session data to include in SignalR messages
                var updatedSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);

                // NOTE: stuck/long-running InProgress sessions are handled authoritatively by
                // MaintenanceService.MarkStalledSessionsAsTimedOutAsync (Stalled at 2h, Failed at
                // SessionTimeoutHours + SessionTimeouts OpsEvent). The former per-batch >4h warning
                // here was redundant observability noise and was removed.

                // Log warning if WhiteGlove status update was not persisted despite retries and fallback.
                if (classification.WhiteGloveEvent != null && updatedSession?.IsPreProvisioned != true)
                {
                    _logger.LogError(
                        "{SessionPrefix} WhiteGlove status update not persisted after retries and fallback. " +
                        "IsPreProvisioned={IsPreProvisioned}, Status={Status}. " +
                        "Proceeding with 200 to allow agent spool drain.",
                        sessionPrefix, updatedSession?.IsPreProvisioned, updatedSession?.Status);
                }

                // Send webhook notifications (fire-and-forget, non-fatal)
                await SendWebhookNotificationsAsync(
                    request, sessionPrefix, classification, updatedSession,
                    statusTransitioned, whiteGloveStatusTransitioned, failureReason, newRuleResults);

                // SLA consecutive failure check (fire-and-forget, configurable threshold)
                if (statusTransitioned && updatedSession?.Status == SessionStatus.Failed)
                {
                    _ = _slaBreachService.EvaluateSessionCompletionAsync(request.TenantId, updatedSession);
                }

                // AdminAction is the authoritative portal-button signal. Read
                // SessionSummary.AdminMarkedAction (set exclusively by MarkSessionSucceededFunction
                // / MarkSessionFailedFunction). The previous "status final + no completion marker
                // in this batch" heuristic fired falsely on post-completion agent events.
                string? adminAction = updatedSession?.AdminMarkedAction;
                if (!string.IsNullOrEmpty(adminAction))
                {
                    _logger.LogInformation("{SessionPrefix} Admin override detected (AdminMarkedAction) — signaling agent: AdminAction={AdminAction}",
                        sessionPrefix, adminAction);
                }

                // Server→Agent actions: fetch+clear from the session row only when the session we
                // just loaded indicates there are pending actions. Zero extra I/O in the common case.
                List<ServerAction>? pendingActions = null;
                if (updatedSession != null && !string.IsNullOrEmpty(updatedSession.PendingActionsJson))
                {
                    var fetched = await _sessionRepo.FetchAndClearPendingActionsAsync(request.TenantId, request.SessionId);
                    if (fetched.Count > 0)
                    {
                        pendingActions = fetched;
                        foreach (var a in fetched)
                        {
                            _telemetryClient.TrackEvent("ServerActionDelivered", new Dictionary<string, string>
                            {
                                { "tenantId", request.TenantId },
                                { "sessionId", request.SessionId },
                                { "actionType", a.Type ?? string.Empty },
                                { "reason", a.Reason ?? string.Empty },
                                { "ruleId", a.RuleId ?? string.Empty },
                                { "queuedAt", a.QueuedAt.ToString("O") },
                                { "ageSeconds", ((int)(DateTime.UtcNow - a.QueuedAt).TotalSeconds).ToString() }
                            });
                        }
                        _logger.LogInformation("{SessionPrefix} Delivering {Count} server action(s): [{Types}]",
                            sessionPrefix, fetched.Count, string.Join(",", fetched.Select(a => a.Type)));
                    }
                }

                // Build HTTP response + SignalR messages
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new IngestEventsResponse
                {
                    Success = true,
                    EventsReceived = request.Events.Count,
                    EventsProcessed = processedCount,
                    Message = $"Successfully stored {processedCount} of {request.Events.Count} events",
                    ProcessedAt = DateTime.UtcNow,
                    AdminAction = adminAction,
                    Actions = pendingActions
                });

                var signalRMessages = BuildSignalRMessages(request, updatedSession, processedCount, newRuleResults);

                return new IngestEventsOutput
                {
                    HttpResponse = response,
                    SignalRMessages = signalRMessages
                };
        }

        /// <summary>
        /// Classifies stored events by type for downstream processing.
        /// </summary>
        private EventClassification ClassifyEvents(List<EnrollmentEvent> storedEvents)
        {
            var classification = new EventClassification();

            foreach (var evt in storedEvents)
            {
                if (!classification.EarliestEventTimestamp.HasValue || evt.Timestamp < classification.EarliestEventTimestamp.Value)
                    classification.EarliestEventTimestamp = evt.Timestamp;
                if (!classification.LatestEventTimestamp.HasValue || evt.Timestamp > classification.LatestEventTimestamp.Value)
                    classification.LatestEventTimestamp = evt.Timestamp;

                switch (evt.EventType)
                {
                    case "phase_changed":
                    case "esp_phase_changed":
                    // V2 DecisionEngine emits AppsDevice/AppsUser/FinalizingSetup as
                    // `phase_transition` (one of the few event types allowed to carry
                    // Phase != Unknown per feedback_phase_strategy). Without classifying
                    // these as phase updates the session row's CurrentPhase stayed at -1
                    // throughout the App phases.
                    case "phase_transition":
                        classification.LastPhaseChangeEvent = evt;
                        break;
                    case "enrollment_complete":
                        classification.CompletionEvent = evt;
                        break;
                    case "gather_rules_collection_completed":
                        classification.GatherCompletionEvent = evt;
                        break;
                    case "enrollment_failed":
                        classification.FailureEvent = evt;
                        break;
                    case "diagnostics_uploaded":
                        classification.DiagnosticsUploadedEvent = evt;
                        break;
                    case "whiteglove_complete":
                        classification.WhiteGloveEvent = evt;
                        break;
                    case "whiteglove_resumed":
                        classification.WhiteGloveResumedEvent = evt;
                        break;
                    case "whiteglove_started":
                        classification.WhiteGloveStartedEvent = evt;
                        break;
                    case "esp_failure":
                        classification.EspFailureEvent = evt;
                        break;
                    case "device_location":
                        classification.DeviceLocationEvent = evt;
                        break;
                    case "session_stalled":
                        classification.SessionStalledEvent = evt;
                        break;
                    case "agent_shutting_down":
                        if (IsMaxLifetimeAgentShutdown(evt))
                            classification.AgentMaxLifetimeShutdownEvent = evt;
                        break;
                    case "system_reboot_detected":
                        // Per-batch incremental reboot count (live value during enrollment).
                        // Overwritten with an authoritative distinct count at the terminal
                        // transition (UpdateSessionStatusAsync), so a re-sent batch can't inflate it.
                        classification.RebootCount++;
                        break;
                    case "script_completed":
                    case "script_failed":
                        var scriptType = evt.Data?.ContainsKey("scriptType") == true
                            ? evt.Data["scriptType"]?.ToString() : null;
                        if (string.Equals(scriptType, "platform", StringComparison.OrdinalIgnoreCase))
                            classification.PlatformScriptCount++;
                        else if (string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase))
                            classification.RemediationScriptCount++;
                        break;
                }

                // Track whether this batch contains any non-periodic real event (used for
                // Stalled→InProgress healing). Exclude telemetry snapshots and stall-probe
                // self-reports which should not count as "agent is productively working".
                if (!IsPeriodicOrStallEvent(evt.EventType))
                    classification.HasNonPeriodicRealEvent = true;

                AggregateAppInstallEvent(evt, storedEvents[0].TenantId!, storedEvents[0].SessionId!, classification.AppInstallUpdates);
            }

            return classification;
        }

        /// <summary>
        /// True for an <c>agent_shutting_down</c> event whose <c>Data.reason</c> is
        /// <c>max_lifetime</c> — the V2 watchdog shutdown (session 8bc1180f). By design that
        /// path is a "notbremse, not a session verdict": the agent stops permanently WITHOUT
        /// emitting <c>enrollment_failed</c>, so this is the last event the session ever
        /// sends and the backend must map it to a terminal status itself. Other shutdown
        /// reasons (decision_terminal, ctrl_c, process_exit, unhandled_exception, ...) either
        /// follow a real terminal event or imply nothing terminal. Shared by both
        /// classification copies (legacy ingest + <see cref="Services.EventIngestProcessor"/>).
        /// </summary>
        internal static bool IsMaxLifetimeAgentShutdown(EnrollmentEvent? evt)
        {
            if (evt == null || !string.Equals(evt.EventType, "agent_shutting_down", StringComparison.Ordinal))
                return false;
            var reason = evt.Data != null && evt.Data.ContainsKey("reason")
                ? evt.Data["reason"]?.ToString()
                : null;
            return string.Equals(reason, "max_lifetime", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Classifies whether an event type should count as "real enrollment activity" for
        /// Stalled→InProgress healing. Periodic telemetry and stall-probe self-reports are
        /// excluded so that a silent agent that only emits heartbeats cannot unmark itself
        /// as stalled without real progress.
        /// </summary>
        private static bool IsPeriodicOrStallEvent(string? eventType) => eventType switch
        {
            "performance_snapshot" => true,
            "agent_metrics_snapshot" => true,
            "performance_collector_stopped" => true,
            "agent_metrics_collector_stopped" => true,
            "stall_probe_check" => true,
            "stall_probe_result" => true,
            "session_stalled" => true,
            "modern_deployment_log" => true,
            _ => false
        };

        /// <summary>
        /// Updates session status based on classified events.
        /// Returns (statusTransitioned, whiteGloveStatusTransitioned, failureReason).
        /// </summary>
        private async Task<(bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason)> UpdateSessionStatusAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c)
        {
            bool statusTransitioned = false;
            bool whiteGloveStatusTransitioned = false;
            string? failureReason = null;

            if (c.CompletionEvent != null)
            {
                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded, c.CompletionEvent.Phase,
                    completedAt: c.CompletionEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogInformation("{SessionPrefix} Status: Succeeded (transitioned={Transitioned})", sessionPrefix, statusTransitioned);
            }
            else if (c.FailureEvent != null)
            {
                failureReason = c.FailureEvent.Data?.ContainsKey("errorCode") == true
                    ? $"{c.FailureEvent.Message} ({c.FailureEvent.Data["errorCode"]})"
                    : c.FailureEvent.Message;

                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.FailureEvent.Phase, failureReason,
                    completedAt: c.FailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed - {FailureReason} (transitioned={Transitioned})", sessionPrefix, failureReason, statusTransitioned);
            }
            else if (c.EspFailureEvent != null)
            {
                failureReason = c.EspFailureEvent.Message ?? "ESP failure (backend fallback)";
                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.EspFailureEvent.Phase, failureReason,
                    completedAt: c.EspFailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed via esp_failure fallback - {FailureReason} (transitioned={Transitioned})",
                    sessionPrefix, failureReason, statusTransitioned);
            }
            else if (c.GatherCompletionEvent != null)
            {
                await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded, c.GatherCompletionEvent.Phase,
                    completedAt: c.GatherCompletionEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogInformation("{SessionPrefix} Status: Succeeded (gather_rules)", sessionPrefix);
            }
            else if (c.WhiteGloveEvent != null)
            {
                // The whiteglove_complete event carries Phase=Complete as a convention
                // (agent signalling "terminal reached"). Persisting that literally as Session.CurrentPhase
                // contradicts Status=Pending and tricks the UI timeline into rendering user-enrollment
                // phases as completed. Cap to AppsDevice (last pre-provisioning phase actually reached).
                whiteGloveStatusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Pending, EnrollmentPhase.AppsDevice,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                    isPreProvisioned: true, isUserDriven: false);

                if (!whiteGloveStatusTransitioned)
                {
                    _logger.LogWarning("{SessionPrefix} WhiteGlove UpdateSessionStatusAsync failed, attempting unconditional fallback for IsPreProvisioned + Status", sessionPrefix);
                    try
                    {
                        await _sessionRepo.SetSessionPreProvisionedAsync(request.TenantId, request.SessionId, true, SessionStatus.Pending, isUserDriven: false);
                        whiteGloveStatusTransitioned = true;
                        _logger.LogInformation("{SessionPrefix} WhiteGlove fallback succeeded: IsPreProvisioned + Status=Pending set via unconditional merge", sessionPrefix);
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "{SessionPrefix} WhiteGlove fallback SetSessionPreProvisionedAsync also failed", sessionPrefix);
                    }
                }

                _logger.LogInformation("{SessionPrefix} Status: Pending (WhiteGlove pre-provisioning complete, transitioned={Transitioned})", sessionPrefix, whiteGloveStatusTransitioned);
            }
            else if (c.WhiteGloveResumedEvent != null)
            {
                var currentSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Pending)
                {
                    await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.InProgress, c.WhiteGloveResumedEvent.Phase,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        isUserDriven: true, resumedAt: c.WhiteGloveResumedEvent.Timestamp);
                    _logger.LogInformation("{SessionPrefix} Status: InProgress (WhiteGlove Part 2 resumed, IsUserDriven=true)", sessionPrefix);
                }
                else
                {
                    _logger.LogInformation("{SessionPrefix} WhiteGlove resumed skipped, session already {Status}", sessionPrefix, currentSession?.Status);
                }
            }
            else if (c.AgentMaxLifetimeShutdownEvent != null)
            {
                // Lowest-priority status writer (session 8bc1180f): the V2 max-lifetime
                // watchdog stops the agent permanently without a session verdict — this
                // shutdown event is the last one the session ever sends, so without this
                // mapping the session stays InProgress forever. Any genuine terminal event
                // in the same batch wins via the else-if chain above; an already-terminal
                // session is protected by the repository's idempotency guard. Pending
                // (WhiteGlove) sessions are skipped — they are deliberately long-lived and
                // resume via re-registration.
                var currentSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Pending)
                {
                    _logger.LogInformation(
                        "{SessionPrefix} max_lifetime shutdown ignored — session is Pending (WhiteGlove)", sessionPrefix);
                }
                else
                {
                    var uptimeMinutes = c.AgentMaxLifetimeShutdownEvent.Data?.ContainsKey("uptimeMinutes") == true
                        ? c.AgentMaxLifetimeShutdownEvent.Data["uptimeMinutes"]?.ToString() : null;
                    failureReason = string.IsNullOrEmpty(uptimeMinutes)
                        ? "Agent reached its maximum lifetime without a terminal enrollment verdict (max_lifetime watchdog shutdown)."
                        : $"Agent reached its maximum lifetime ({uptimeMinutes} min) without a terminal enrollment verdict (max_lifetime watchdog shutdown).";

                    statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.Failed, c.AgentMaxLifetimeShutdownEvent.Phase, failureReason,
                        completedAt: c.AgentMaxLifetimeShutdownEvent.Timestamp,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        failureSource: "max_lifetime_watchdog");
                    _logger.LogWarning("{SessionPrefix} Status: Failed via max_lifetime shutdown mapping (transitioned={Transitioned})",
                        sessionPrefix, statusTransitioned);
                }
            }

            // whiteglove_started (EventID 509) is a soft signal only — it fires on hybrid-join
            // devices too. Do NOT set IsPreProvisioned here. Only whiteglove_complete (confirmed by
            // SaveWhiteGloveSuccessResult=succeeded in ESP registry) should mark IsPreProvisioned.
            if (c.WhiteGloveStartedEvent != null)
            {
                _logger.LogInformation("{SessionPrefix} whiteglove_started detected (soft signal — not setting IsPreProvisioned, awaiting whiteglove_complete)", sessionPrefix);
            }

            // Stalled-status transitions are independent of Succeeded/Failed/Pending/WhiteGlove paths.
            // They can coexist with normal processing: if a batch contains session_stalled, mark Stalled;
            // if a batch contains any real (non-periodic) event while the session is currently Stalled,
            // heal it back to InProgress.
            if (c.SessionStalledEvent != null)
            {
                var stalledReason = "Agent reported stall after 60min without progress (stall_probe)";
                var stalledTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Stalled,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                    stalledAt: c.SessionStalledEvent.Timestamp, failureReason: stalledReason);
                if (stalledTransitioned)
                    _logger.LogWarning("{SessionPrefix} Status: Stalled (agent-reported via session_stalled event)", sessionPrefix);
            }
            else if (c.HasNonPeriodicRealEvent && !statusTransitioned && !whiteGloveStatusTransitioned)
            {
                // Only attempt healing if the current session is actually Stalled (one read).
                // The repository guard will no-op this call when the session is in any other state.
                var currentSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Stalled)
                {
                    var healed = await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.InProgress,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        clearStalledAt: true, clearFailureReason: true);
                    if (healed)
                        _logger.LogInformation("{SessionPrefix} Status: InProgress (healed from Stalled by new real event)", sessionPrefix);
                }
            }

            return (statusTransitioned, whiteGloveStatusTransitioned, failureReason);
        }

        /// <summary>
        /// Sends webhook notifications for enrollment completion, WhiteGlove, and ESP failure events.
        /// Uses the channel-agnostic notification system with provider-specific renderers.
        /// </summary>
        private async Task SendWebhookNotificationsAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c,
            SessionSummary? updatedSession, bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason,
            List<AutopilotMonitor.Shared.Models.RuleResult> ruleResults)
        {
            // Read config once (was 3 separate reads before)
            var tenantConfig = await _configService.GetConfigurationAsync(request.TenantId);
            var (webhookUrl, providerTypeInt) = tenantConfig.GetEffectiveWebhookConfig();

            if (string.IsNullOrEmpty(webhookUrl) || providerTypeInt == 0)
                return;

            var providerType = (WebhookProviderType)providerTypeInt;
            var customHeaders = tenantConfig.GetGenericWebhookHeaders();
            var sessionUrl = updatedSession != null
                ? $"https://portal.autopilotmonitor.com/sessions/{request.SessionId}"
                : null;

            // Enrollment completion/failure notification
            // Only send when statusTransitioned=true to prevent duplicates on retry/double-upload
            if (statusTransitioned && (c.CompletionEvent != null || c.FailureEvent != null))
            {
                var notifySuccess = c.CompletionEvent != null && tenantConfig.GetEffectiveNotifyOnSuccess();
                var notifyFailure = c.FailureEvent != null && tenantConfig.GetEffectiveNotifyOnFailure();
                if (notifySuccess || notifyFailure)
                {
                    var duration = updatedSession?.DurationSeconds != null
                        ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                        : (TimeSpan?)null;

                    // For WhiteGlove sessions: show user enrollment duration only (Duration 2)
                    if (updatedSession?.IsPreProvisioned == true && updatedSession?.ResumedAt != null)
                    {
                        var completionTime = c.CompletionEvent?.Timestamp ?? c.FailureEvent?.Timestamp;
                        if (completionTime.HasValue)
                            duration = completionTime.Value - updatedSession.ResumedAt.Value;
                    }

                    var alert = NotificationAlertBuilder.BuildEnrollmentAlert(
                        updatedSession?.DeviceName,
                        updatedSession?.SerialNumber,
                        updatedSession?.Manufacturer,
                        updatedSession?.Model,
                        success: c.CompletionEvent != null,
                        failureReason: failureReason,
                        duration: duration,
                        sessionUrl: sessionUrl);
                    NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                    _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert, customHeaders)
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                            "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            // WhiteGlove pre-provisioning completion
            if (whiteGloveStatusTransitioned && c.WhiteGloveEvent != null && tenantConfig.GetEffectiveNotifyOnSuccess())
            {
                var duration = updatedSession?.DurationSeconds != null
                    ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                    : (TimeSpan?)null;

                var alert = NotificationAlertBuilder.BuildWhiteGloveAlert(
                    updatedSession?.DeviceName,
                    updatedSession?.SerialNumber,
                    updatedSession?.Manufacturer,
                    updatedSession?.Model,
                    success: true,
                    duration: duration,
                    sessionUrl: sessionUrl);
                NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert, customHeaders)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

            // WhiteGlove pre-provisioning failure via esp_failure
            if (c.EspFailureEvent != null && updatedSession?.IsPreProvisioned == true && tenantConfig.GetEffectiveNotifyOnFailure())
            {
                var duration = updatedSession?.DurationSeconds != null
                    ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                    : (TimeSpan?)null;

                var alert = NotificationAlertBuilder.BuildWhiteGloveAlert(
                    updatedSession?.DeviceName,
                    updatedSession?.SerialNumber,
                    updatedSession?.Manufacturer,
                    updatedSession?.Model,
                    success: false,
                    duration: duration,
                    sessionUrl: sessionUrl);
                NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert, customHeaders)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        /// <summary>
        /// Builds SignalR messages for tenant-level and session-level real-time updates.
        /// </summary>
        private SignalRMessageAction[] BuildSignalRMessages(
            IngestEventsRequest request, SessionSummary? updatedSession, int processedCount,
            List<AutopilotMonitor.Shared.Models.RuleResult> newRuleResults)
        {
            // 1. Summary notification for session list updates (tenant-specific only)
            // Send only mutable fields as delta update
            object? sessionDelta = updatedSession != null ? new {
                updatedSession.CurrentPhase,
                updatedSession.CurrentPhaseDetail,
                updatedSession.Status,
                updatedSession.FailureReason,
                updatedSession.EventCount,
                updatedSession.DurationSeconds,
                updatedSession.CompletedAt,
                updatedSession.DiagnosticsBlobName,
                updatedSession.IsPreProvisioned
            } : null;

            var summaryMessage = new SignalRMessageAction("newevents")
            {
                GroupName = $"tenant-{request.TenantId}",
                Arguments = new object[] { new {
                    sessionId = request.SessionId,
                    tenantId = request.TenantId,
                    eventCount = processedCount,
                    sessionUpdate = sessionDelta
                } }
            };

            // 2. Signal for real-time event streaming on detail pages (session-specific)
            var slimRuleResults = newRuleResults.Count > 0
                ? newRuleResults.Select(r => new {
                    r.ResultId,
                    r.RuleId,
                    r.RuleTitle,
                    r.Severity,
                    r.Category,
                    r.ConfidenceScore,
                    r.Explanation,
                    r.Remediation,
                    r.RelatedDocs,
                    r.MatchedConditions,
                    r.DetectedAt
                }).ToList<object>()
                : null;

            var eventsMessage = new SignalRMessageAction("eventStream")
            {
                GroupName = $"session-{request.TenantId}-{request.SessionId}",
                Arguments = new object[] { new {
                    sessionId = request.SessionId,
                    tenantId = request.TenantId,
                    newEventCount = processedCount,
                    newRuleResults = slimRuleResults
                } }
            };

            return new[] { summaryMessage, eventsMessage };
        }

        /// <summary>
        /// Stamps authoritative server-side fields onto all events before storage.
        /// TenantId and SessionId always come from the validated request metadata,
        /// overriding any values the agent may have sent per-event.
        /// Exposed as internal for unit testing.
        /// </summary>
        internal static void StampServerFields(
            List<EnrollmentEvent> events, string tenantId, string sessionId, DateTime receivedAt)
        {
            foreach (var evt in events)
            {
                evt.ReceivedAt = receivedAt;
                evt.TenantId = tenantId;
                evt.SessionId = sessionId;
            }
        }

        /// <summary>
        /// Sanitizes agent-side timestamps on all events by clamping out-of-range values.
        /// When a timestamp is clamped, the original value is preserved in OriginalTimestamp
        /// and TimestampClamped is set to true — keeping the raw data available for
        /// troubleshooting and root-cause analysis of clock issues on devices.
        ///
        /// Emits structured logs for observability:
        /// - Debug level per clamped event (TenantId/SessionId/EventType/drift) — opt-in via log level
        /// - One Warning per ingest batch that had any clamping, with aggregate counts and max drifts.
        ///   This is what to query in App Insights to find bad-clock devices:
        ///     traces | where message startswith "Agent clock skew"
        ///
        /// Exposed as internal for unit testing.
        /// </summary>
        internal static void SanitizeEventTimestamps(List<EnrollmentEvent> events, DateTime utcNow, ILogger? logger = null)
        {
            int clampedPast = 0;
            int clampedFuture = 0;
            double maxPastDriftHours = 0;
            double maxFutureDriftHours = 0;

            foreach (var evt in events)
            {
                var original = evt.Timestamp;
                var sanitized = EventTimestampValidator.SanitizeTimestamp(original, utcNow);
                if (sanitized == original)
                    continue;

                evt.OriginalTimestamp = original;
                evt.TimestampClamped = true;
                evt.Timestamp = sanitized;

                // Classify the clamping direction (for aggregate stats) and track max drift.
                // Compare in UTC so Local/Unspecified Kinds don't skew the direction check.
                // Note: catastrophic values like DateTime.MinValue fall into the "past" bucket
                // with a very large drift — this is intentional and makes them easy to spot in logs.
                var originalUtc = EventTimestampValidator.EnsureUtc(original);
                if (originalUtc > utcNow)
                {
                    clampedFuture++;
                    var drift = (originalUtc - utcNow).TotalHours;
                    if (drift > maxFutureDriftHours) maxFutureDriftHours = drift;
                }
                else
                {
                    clampedPast++;
                    var drift = (utcNow - originalUtc).TotalHours;
                    if (drift > maxPastDriftHours) maxPastDriftHours = drift;
                }

                logger?.LogDebug(
                    "Event timestamp clamped: TenantId={TenantId}, SessionId={SessionId}, EventType={EventType}, Original={Original:O}, Sanitized={Sanitized:O}",
                    evt.TenantId, evt.SessionId, evt.EventType, original, sanitized);
            }

            if (clampedPast + clampedFuture > 0 && logger != null)
            {
                // Pull tenant/session from the first clamped event (all events in a batch share the same context).
                var firstClamped = events.Find(e => e.TimestampClamped);
                logger.LogWarning(
                    "Agent clock skew detected: TenantId={TenantId}, SessionId={SessionId}, TotalEvents={TotalEvents}, ClampedPast={ClampedPast}, ClampedFuture={ClampedFuture}, MaxPastDriftHours={MaxPastDriftHours:F1}, MaxFutureDriftHours={MaxFutureDriftHours:F1}",
                    firstClamped?.TenantId,
                    firstClamped?.SessionId,
                    events.Count,
                    clampedPast,
                    clampedFuture,
                    maxPastDriftHours,
                    maxFutureDriftHours);
            }
        }

        /// <summary>
        /// Records gather rule telemetry for events that carry a ruleId in their data.
        /// Fire-and-forget — failures are logged but never propagated.
        /// Gather rules have no evaluation count (they run on the agent); we only track fires.
        /// </summary>
        private async Task RecordGatherRuleStatsAsync(string tenantId, List<EnrollmentEvent> events)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

                // Gather rule results carry ruleId in their event data
                var gatherEvents = events.Where(e =>
                    e.Data != null &&
                    e.Data.ContainsKey("ruleId") &&
                    e.Source == "GatherRuleExecutor").ToList();

                if (gatherEvents.Count == 0) return;

                // Deduplicate: count each unique ruleId once per ingest batch
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var evt in gatherEvents)
                {
                    var ruleId = evt.Data!["ruleId"]?.ToString();
                    if (string.IsNullOrEmpty(ruleId) || !seen.Add(ruleId)) continue;

                    var ruleTitle = evt.Data.ContainsKey("ruleTitle") ? evt.Data["ruleTitle"]?.ToString() ?? "" : "";

                    // Tenant-specific row
                    await _metricsRepo.IncrementRuleStatAsync(
                        today, tenantId, ruleId, "gather",
                        ruleTitle, "", "",
                        fired: true, confidenceScore: null);

                    // Global aggregate row
                    await _metricsRepo.IncrementRuleStatAsync(
                        today, "global", ruleId, "gather",
                        ruleTitle, "", "",
                        fired: true, confidenceScore: null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record gather rule stats (non-fatal)");
            }
        }

    }

    /// <summary>
    /// Holds classified events from an ingest batch for downstream processing.
    /// </summary>
    internal class EventClassification
    {
        public EnrollmentEvent? LastPhaseChangeEvent { get; set; }
        public EnrollmentEvent? CompletionEvent { get; set; }
        public EnrollmentEvent? FailureEvent { get; set; }
        public EnrollmentEvent? GatherCompletionEvent { get; set; }
        public EnrollmentEvent? DiagnosticsUploadedEvent { get; set; }
        public EnrollmentEvent? WhiteGloveEvent { get; set; }
        public EnrollmentEvent? WhiteGloveStartedEvent { get; set; }
        public EnrollmentEvent? WhiteGloveResumedEvent { get; set; }
        public EnrollmentEvent? EspFailureEvent { get; set; }
        public EnrollmentEvent? SessionStalledEvent { get; set; }

        /// <summary>
        /// <c>agent_shutting_down</c> with <c>Data.reason == "max_lifetime"</c> — the V2
        /// watchdog shutdown that deliberately carries no enrollment verdict. Mapped to a
        /// terminal Failed status as the lowest-priority status writer so the session does
        /// not stay InProgress forever (session 8bc1180f).
        /// </summary>
        public EnrollmentEvent? AgentMaxLifetimeShutdownEvent { get; set; }
        public bool HasNonPeriodicRealEvent { get; set; }
        public EnrollmentEvent? DeviceLocationEvent { get; set; }
        public DateTime? EarliestEventTimestamp { get; set; }
        public DateTime? LatestEventTimestamp { get; set; }
        public Dictionary<string, AppInstallAggregationState> AppInstallUpdates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PlatformScriptCount { get; set; }
        public int RemediationScriptCount { get; set; }

        /// <summary>
        /// Number of <c>system_reboot_detected</c> events seen in this ingest batch (V2 only).
        /// Drives the per-batch incremental RebootCount; the stored value is later overwritten
        /// with an authoritative distinct count at the terminal transition.
        /// </summary>
        public int RebootCount { get; set; }
    }
}
