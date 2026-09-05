using System.IO;
using System.Net;
using Azure;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Agent ingest endpoint. Consumes a heterogeneous batch of <see cref="TelemetryItemDto"/>s
    /// (Events + Signals + DecisionTransitions in a single JSON array, gzip-compressed on
    /// the wire — the <c>UseRequestDecompression</c> middleware decompresses before we
    /// parse). Plan §2.7a / §M5 / M4.6.ε.
    /// <para>
    /// <b>Routing by <see cref="TelemetryItemDto.Kind"/>:</b>
    /// <list type="bullet">
    ///   <item><c>Event</c> → <see cref="EventIngestProcessor"/> (rule engine, app-install
    ///   aggregation, SignalR, vulnerability correlation, webhooks, SLA breach, AdminAction
    ///   detection, ServerAction delivery)</item>
    ///   <item><c>Signal</c> → <see cref="ISignalRepository.StoreBatchAsync"/></item>
    ///   <item><c>DecisionTransition</c> → <see cref="IDecisionTransitionRepository.StoreBatchAsync"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Response (M4.6.ε):</b> the agent parses <c>DeviceBlocked</c>/<c>UnblockAt</c>/
    /// <c>DeviceKillSignal</c>/<c>AdminAction</c>/<c>Actions</c> from the 2xx body and routes
    /// kill-switches through its <c>ServerActionDispatcher</c>.
    /// </para>
    /// </summary>
    public sealed class IngestTelemetryFunction
    {
        private readonly ILogger<IngestTelemetryFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly ISignalRepository _signalRepo;
        private readonly IDecisionTransitionRepository _transitionRepo;
        private readonly EventIngestProcessor _eventProcessor;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly CloudPcDeviceValidator _cloudPcDeviceValidator;
        private readonly IntuneDeviceBindingValidator _intuneDeviceBindingValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly KillSwitchEvaluator _killSwitchEvaluator;
        private readonly SessionDeletionGuard _deletionGuard;
        private readonly SessionOwnerBindingObserver _ownerBinding;
        private readonly OpsEventService _opsEventService;

        public IngestTelemetryFunction(
            ILogger<IngestTelemetryFunction> logger,
            ISessionRepository sessionRepo,
            ISignalRepository signalRepo,
            IDecisionTransitionRepository transitionRepo,
            EventIngestProcessor eventProcessor,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            CloudPcDeviceValidator cloudPcDeviceValidator,
            IntuneDeviceBindingValidator intuneDeviceBindingValidator,
            BootstrapSessionService bootstrapSessionService,
            KillSwitchEvaluator killSwitchEvaluator,
            SessionDeletionGuard deletionGuard,
            SessionOwnerBindingObserver ownerBinding,
            OpsEventService opsEventService)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _signalRepo = signalRepo;
            _transitionRepo = transitionRepo;
            _eventProcessor = eventProcessor;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _cloudPcDeviceValidator = cloudPcDeviceValidator;
            _intuneDeviceBindingValidator = intuneDeviceBindingValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _killSwitchEvaluator = killSwitchEvaluator;
            _deletionGuard = deletionGuard;
            _ownerBinding = ownerBinding;
            _opsEventService = opsEventService;
        }

        [Function("IngestTelemetry")]
        public async Task<IngestEventsOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/telemetry")] HttpRequestData req)
        {
            try
            {
                var tenantIdHeader = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantIdHeader))
                {
                    return AsOutput(await WriteErrorAsync(req, HttpStatusCode.BadRequest, "X-Tenant-Id header is required"));
                }

                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantIdHeader,
                    _configService,
                    _adminConfigService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator,
                    cloudPcDeviceValidator: _cloudPcDeviceValidator,
                    intuneDeviceBindingValidator: _intuneDeviceBindingValidator);

                if (errorResponse != null) return AsOutput(errorResponse);

                // Device + version kill-switches: short-circuit before parsing the body. Serial
                // leg (caller-declared header) AND certificate-identity leg (cert Subject CN — the
                // one an agent that omits or forges its serial cannot dodge). A session-scoped
                // block (watchdog auto-block) cannot be decided yet — the session id lives in the
                // body — so it is re-evaluated below once parsed. Evaluation + kill ops-event live
                // in KillSwitchEvaluator, shared with the agent-config channel.
                var serialNumberHeader = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                    : null;
                var agentVersionHeader = req.Headers.Contains("X-Agent-Version")
                    ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault()
                    : null;

                var preBodyVerdict = await _killSwitchEvaluator.EvaluateAsync(
                    tenantIdHeader, serialNumberHeader, agentVersionHeader, channel: "telemetry",
                    intuneDeviceId: validation.IntuneDeviceId);
                DeviceIdentityBinding.Stamp(req, preBodyVerdict.IdentityBinding);
                if (preBodyVerdict.IsBlocked && !preBodyVerdict.IsSessionScoped)
                    return AsOutput(await WriteDeviceBlockedAsync(req, preBodyVerdict));

                // Defense in depth: cap decompressed body size before buffering it all into
                // memory + deserialising (same tenant-config knob the removed V1 NDJSON path
                // used). Request body here is already gzip-decompressed by
                // UseRequestDecompression — so the cap is on the actual JSON bytes we'll feed
                // to the parser.
                var tenantConfig = await _configService.GetConfigurationAsync(tenantIdHeader);
                var maxPayloadBytes = (tenantConfig?.MaxNdjsonPayloadSizeMB ?? 5) * 1024 * 1024;

                bool exceeded;
                List<TelemetryItemDto>? items;
                try
                {
                    (exceeded, items) = await ReadBodyWithSizeCapAsync(req.Body, maxPayloadBytes);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "IngestTelemetry: malformed JSON body");
                    return AsOutput(await WriteErrorAsync(req, HttpStatusCode.BadRequest, "Malformed JSON body"));
                }

                if (exceeded)
                {
                    _logger.LogWarning(
                        "IngestTelemetry: payload exceeds {Max}MB cap for tenant {Tenant}",
                        maxPayloadBytes / (1024 * 1024), tenantIdHeader);
                    return AsOutput(await WriteErrorAsync(
                        req, HttpStatusCode.RequestEntityTooLarge,
                        $"Payload exceeds {maxPayloadBytes / (1024 * 1024)} MB cap"));
                }

                if (items == null || items.Count == 0)
                {
                    return AsOutput(await WriteErrorAsync(req, HttpStatusCode.BadRequest, "No telemetry items provided"));
                }

                // Extract tenant+session from the first item's PartitionKey for body-vs-header tenant check
                // and for AdminAction / ServerAction lookups. All items in a batch must belong to one
                // session — anything else is a client bug and gets the whole batch rejected (see
                // the uniformity check below).
                if (!TryParsePartitionKey(items[0].PartitionKey, out var bodyTenantId, out var sessionId))
                {
                    return AsOutput(await WriteErrorAsync(req, HttpStatusCode.BadRequest, "Malformed PartitionKey"));
                }

                if (!string.Equals(bodyTenantId, tenantIdHeader, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "IngestTelemetry: TenantId mismatch — header={Header}, body={Body}",
                        tenantIdHeader, bodyTenantId);
                    return AsOutput(await WriteErrorAsync(req, HttpStatusCode.Forbidden, "TenantId mismatch between header and payload"));
                }

                // Defense-in-depth: every item must carry the same PartitionKey as the first. Without
                // this check a mixed-session batch would stamp every item with session A's identity
                // inside PersistItemsAsync, silently relocating session B's signals/transitions into
                // session A's primary rows.
                if (FindMismatchingPartitionKey(items, out var mismatchIndex, out var mismatchedValue))
                {
                    _logger.LogWarning(
                        "IngestTelemetry: PartitionKey mismatch at item[{Index}] — expected={Expected}, got={Got}",
                        mismatchIndex, items[0].PartitionKey, mismatchedValue);
                    return AsOutput(await WriteErrorAsync(
                        req, HttpStatusCode.BadRequest,
                        "All telemetry items in a batch must share the same PartitionKey"));
                }

                // Session-scoped block, session now known: either this is one of the blocked
                // sessions (stop) or a new enrollment on the same device (auto-unblock — the
                // watchdog blocked a runaway session, not the device). The version leg runs
                // again on purpose: the device leg short-circuited it before the body was parsed.
                if (preBodyVerdict.IsSessionScoped)
                {
                    var sessionVerdict = await _killSwitchEvaluator.EvaluateAsync(
                        bodyTenantId, serialNumberHeader, agentVersionHeader, channel: "telemetry",
                        intuneDeviceId: validation.IntuneDeviceId, sessionId: sessionId);
                    if (sessionVerdict.IsBlocked)
                        return AsOutput(await WriteDeviceBlockedAsync(req, sessionVerdict));
                }

                // Cascade-delete guard: refuse the batch with 410 Gone when a V2 cascade owns the
                // Sessions row (states Preparing/Queued/Running/Poisoned). Without this check, the
                // hot-path writers below (StoreEventsBatchAsync, signal/transition StoreBatchAsync,
                // UpdateSessionImeAgentVersionAsync upsert, …) would land rows past the lock and
                // leave orphan data the manifest cannot describe. One read per batch; absent
                // Sessions row → silent pass (caller handles session-not-found in its own write).
                Azure.Data.Tables.TableEntity? guardSessionRow;
                try
                {
                    guardSessionRow = await _deletionGuard.EnsureWritableAndGetRowAsync(bodyTenantId, sessionId, "V2.IngestTelemetry");
                }
                catch (SessionDeletionLockedException locked)
                {
                    _logger.LogInformation(
                        "IngestTelemetry: refused batch — cascade in flight tenant={Tenant} session={Session} state={State} manifestId={ManifestId}",
                        bodyTenantId, sessionId, locked.CurrentState, locked.ManifestId);
                    return AsOutput(await WriteSessionLockedAsync(req, locked));
                }

                // The header serial is caller-declared; the Sessions row carries the serial the
                // session was registered under. When they differ, the ROW's serial is held against
                // the block list too — a mid-session header change must not dodge a block placed
                // on the registered serial. Cached like every other lookup (negative entries for
                // healthy devices), so the common case costs nothing.
                var rowSerial = guardSessionRow?.GetString("SerialNumber");
                if (!string.IsNullOrWhiteSpace(rowSerial)
                    && !string.Equals(rowSerial.Trim(), serialNumberHeader?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    var rowSerialVerdict = await _killSwitchEvaluator.EvaluateAsync(
                        bodyTenantId, rowSerial, agentVersion: null, channel: "telemetry", sessionId: sessionId);
                    if (rowSerialVerdict.IsBlocked)
                        return AsOutput(await WriteDeviceBlockedAsync(req, rowSerialVerdict));
                }

                // Reuse the guard's full-row read: its Status feeds the stall-heal check inside
                // EventIngestProcessor, saving one Sessions point-read per batch.
                var preFetchedStatus = TryReadSessionStatus(guardSessionRow);

                // SESSION-OWNER-BINDING-SHADOW: is the device behind this certificate/token the
                // device this session belongs to? Same guard row, zero extra reads. Stage 1 records
                // the outcome (request-row dimension + Warning + throttled ops event) and stamps
                // legacy claims / rebinds; it never refuses the batch. Covers the Signal-only path
                // too — that is where pending ServerActions get fetched-and-cleared.
                var ownerDecision = _ownerBinding.Observe(req, bodyTenantId, sessionId, guardSessionRow, validation, "agent/telemetry");
                if (guardSessionRow != null)
                    await _ownerBinding.StampAsync(bodyTenantId, sessionId, ownerDecision);

                // P14: device-clock send time of this upload (one value per request). Separates
                // spool delay from device-vs-server clock offset downstream; absent/garbage → null.
                var sentAtHeader = req.Headers.Contains("X-Send-Time-Utc")
                    ? req.Headers.GetValues("X-Send-Time-Utc").FirstOrDefault()
                    : null;
                var sentAt = ParseSendTimeHeader(sentAtHeader);

                // Partition by Kind BEFORE anything is written. An item the ingest cannot route
                // (unknown Kind) or parse (unusable payload) is a permanent per-item failure: the
                // batch is answered with 422 + the poison body naming those RowKeys, and nothing
                // of it is stored — the agent drops exactly the named items and re-uploads the
                // rest. Persisting the survivors here would be idempotent but pointless (the agent
                // re-sends them), and a 200 would make the agent clear its spool with the items lost.
                var batch = PartitionBatch(items, bodyTenantId, sessionId);
                if (batch.Rejected.Count > 0)
                    return AsOutput(await WriteItemsRejectedAsync(req, batch, bodyTenantId, sessionId, agentVersionHeader));

                // Persist. Events are routed through EventIngestProcessor which runs the full
                // pipeline (rule engine / app-install aggregation / SignalR / webhooks / ...);
                // Signal + Transition go straight to their repositories.
                var outcome = await PersistItemsAsync(batch, bodyTenantId, sessionId, validation, preFetchedStatus, sentAt);

                _logger.LogInformation(
                    "IngestTelemetry: tenant={Tenant} session={Session} events={E} signals={S} transitions={T}",
                    bodyTenantId, sessionId, outcome.EventCount, outcome.SignalCount, outcome.TransitionCount);

                var response = await req.OkAsync(new IngestEventsResponse
                {
                    Success = true,
                    EventsReceived = items.Count,
                    EventsProcessed = outcome.EventCount + outcome.SignalCount + outcome.TransitionCount,
                    Message = $"Stored {outcome.EventCount} events, {outcome.SignalCount} signals, {outcome.TransitionCount} transitions",
                    ProcessedAt = DateTime.UtcNow,
                    AdminAction = outcome.AdminAction,
                    Actions = outcome.PendingActions,
                });

                return new IngestEventsOutput
                {
                    HttpResponse = response,
                    SignalRMessages = outcome.SignalRMessages,
                };
            }
            catch (TableEntityTooLargeException ex)
            {
                // One item can never fit a table row. 413 makes the agent halve the batch and,
                // once the item is alone, quarantine it — a 500 would be replayed forever.
                _logger.LogWarning(ex, "IngestTelemetry: telemetry item {Partition}/{Row} exceeds the entity limit ({Bytes} bytes)",
                    ex.PartitionKey, ex.RowKey, ex.EstimatedBytes);
                return AsOutput(await WriteErrorAsync(req, HttpStatusCode.RequestEntityTooLarge,
                    "A telemetry item exceeds the storage entity limit"));
            }
            catch (RequestFailedException ex)
            {
                var (status, message, retryAfter) = ClassifyStorageFailure(ex);
                if (status == HttpStatusCode.InternalServerError)
                    _logger.LogError(ex, "IngestTelemetry: storage request failed with status {Status} ({Code})", ex.Status, ex.ErrorCode);
                else
                    _logger.LogWarning(ex, "IngestTelemetry: storage request failed with status {Status} ({Code}) — answering {Http}", ex.Status, ex.ErrorCode, (int)status);
                return AsOutput(await WriteErrorAsync(req, status, message, retryAfter));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IngestTelemetry: unhandled exception");
                return AsOutput(await WriteErrorAsync(req, HttpStatusCode.InternalServerError, "Internal server error"));
            }
        }

        /// <summary>Seconds the agent should wait before replaying a batch after a storage outage.</summary>
        internal const int StorageRetryAfterSeconds = 5;

        /// <summary>
        /// Maps a storage failure onto the status the agent's uploader understands: 413 makes it
        /// halve the batch, 503 (with <c>Retry-After</c>) makes it replay later; the agent treats
        /// 500 as transient too but never shrinks on it, so an oversize batch surfacing as 500
        /// would wedge the device (audit 2026-09-05 F07).
        /// </summary>
        internal static (HttpStatusCode Status, string Message, int? RetryAfterSeconds) ClassifyStorageFailure(RequestFailedException ex)
        {
            if (StorageErrors.IsPayloadTooLarge(ex))
                return (HttpStatusCode.RequestEntityTooLarge, "Telemetry batch exceeds the storage transaction limit; retry with a smaller batch", null);
            if (StorageErrors.IsTransient(ex))
                return (HttpStatusCode.ServiceUnavailable, "Storage temporarily unavailable; retry later", StorageRetryAfterSeconds);
            return (HttpStatusCode.InternalServerError, "Internal server error", null);
        }

        private static IngestEventsOutput AsOutput(HttpResponseData response)
            => new IngestEventsOutput
            {
                HttpResponse = response,
                SignalRMessages = Array.Empty<SignalRMessageAction>(),
            };

        /// <summary>
        /// 200 with <c>DeviceBlocked=true</c> (and <c>DeviceKillSignal=true</c> for a kill): the
        /// agent pauses its upload loop until <c>UnblockAt</c>, or self-destructs on a kill.
        /// </summary>
        private static Task<HttpResponseData> WriteDeviceBlockedAsync(HttpRequestData req, KillSwitchVerdict verdict)
            => req.OkAsync(new IngestEventsResponse
            {
                Success = false,
                DeviceBlocked = true,
                DeviceKillSignal = verdict.IsKill,
                UnblockAt = verdict.UnblockAt,
                Message = verdict.Message,
                ProcessedAt = DateTime.UtcNow,
            });

        /// <summary>
        /// Responds 410 Gone when the V2 cascade-delete guard refuses the batch. Per plan §5 PR3
        /// wiring table, telemetry ingest of a locked session is a terminal condition for the
        /// agent: the session is being torn down server-side and any further writes would create
        /// orphan rows. 410 is the documented status; the body shape mirrors the device-blocked
        /// response (RegisterSession answers its 410 the same way).
        /// </summary>
        private static Task<HttpResponseData> WriteSessionLockedAsync(HttpRequestData req, SessionDeletionLockedException locked)
            => req.JsonAsync(HttpStatusCode.Gone, new IngestEventsResponse
            {
                Success = false,
                Message = $"Session is being deleted by an administrator (state={locked.CurrentState}); further telemetry will be rejected.",
                ProcessedAt = DateTime.UtcNow,
            });

        /// <summary>
        /// 422 + poison body: the backend end of the agent's item-level poison protocol
        /// (<c>BackendTelemetryUploader.TryReadPoisonSignalAsync</c>). Logged as a Warning with
        /// the first rejected item and recorded as an ops event, because a rejected batch is
        /// either an agent-side serialisation regression or a Kind the deployed backend does not
        /// know yet — both are contract drift an operator must see, not a per-session detail.
        /// </summary>
        private async Task<HttpResponseData> WriteItemsRejectedAsync(
            HttpRequestData req, PartitionedBatch batch, string tenantId, string sessionId, string? agentVersion)
        {
            var unknownKind = batch.Rejected.Count(r => r.Cause == RejectionCause.UnknownKind);
            var unparseable = batch.Rejected.Count - unknownKind;
            var reason = $"unknown_kind={unknownKind};unparseable={unparseable}";
            var first = batch.Rejected[0];

            _logger.LogWarning(
                "IngestTelemetry: refused {Rejected} of {Received} item(s) for session {SessionId} with 422 poison " +
                "({Reason}; first: TelemetryItemId={FirstItemId} Kind='{FirstKind}' RowKey={FirstRowKey})",
                batch.Rejected.Count, batch.Received, sessionId, reason, first.TelemetryItemId, first.Kind, first.RowKey);
            await _opsEventService.RecordTelemetryItemsRejectedAsync(
                tenantId, sessionId, agentVersion, batch.Received, batch.Rejected.Count, unknownKind, unparseable,
                first.TelemetryItemId, reason);

            return await req.ErrorAsync(HttpStatusCode.UnprocessableEntity, new TelemetryItemsRejectedResponse
            {
                Error = $"{batch.Rejected.Count} of {batch.Received} telemetry item(s) cannot be ingested (unknown Kind or unusable payload); they were not stored.",
                Poison = true,
                RejectedRowKeys = batch.Rejected.Select(r => r.RowKey).ToList(),
                Reason = reason,
                Received = batch.Received,
                Rejected = batch.Rejected.Count,
            });
        }

        /// <summary>
        /// Extracts <c>Status</c> from a Sessions row the deletion guard already loaded. Sessions
        /// writes Status as a STRING (<c>status.ToString()</c> in UpdateSessionStatusAsync — never
        /// an int), so this mirrors the canonical mapper's parse: <c>Enum.TryParse</c>,
        /// case-insensitive. Returns null for missing/unparseable values (incl. a defensive int
        /// fallback for any legacy numeric shape) — callers then fall back to their own read.
        /// </summary>
        internal static SessionStatus? TryReadSessionStatus(Azure.Data.Tables.TableEntity? sessionRow)
        {
            if (sessionRow == null || !sessionRow.TryGetValue("Status", out var statusValue))
                return null;

            return statusValue switch
            {
                string s when Enum.TryParse<SessionStatus>(s, ignoreCase: true, out var parsed) => parsed,
                int i when Enum.IsDefined(typeof(SessionStatus), i) => (SessionStatus)i,
                _ => null,
            };
        }

        /// <summary>
        /// Routes every item of the batch by <see cref="TelemetryItemDto.Kind"/> into its storage
        /// shape. Pure and total: an item whose Kind is not a <see cref="TelemetryItemKind"/> name
        /// or whose payload the parser cannot use lands in <see cref="PartitionedBatch.Rejected"/>
        /// instead of being skipped — the caller turns a non-empty rejection list into the 422
        /// poison answer and stores nothing. Every defined kind has an arm (pinned by
        /// <c>TelemetryWireFixtureTests</c>); a kind added to the enum without one is rejected as
        /// <see cref="RejectionCause.UnknownKind"/>, never silently accepted.
        /// </summary>
        internal static PartitionedBatch PartitionBatch(IReadOnlyList<TelemetryItemDto> items, string tenantId, string sessionId)
        {
            var batch = new PartitionedBatch(items.Count);

            foreach (var item in items)
            {
                if (!TelemetryItemKinds.TryParse(item.Kind, out var kind))
                {
                    batch.Rejected.Add(new RejectedItem(item, RejectionCause.UnknownKind));
                    continue;
                }

                bool parsed;
                switch (kind)
                {
                    case TelemetryItemKind.Event:
                        parsed = Add(batch.Events, TelemetryPayloadParser.ParseEvent(item, tenantId, sessionId));
                        break;
                    case TelemetryItemKind.Signal:
                        parsed = Add(batch.Signals, TelemetryPayloadParser.ParseSignal(item, tenantId, sessionId));
                        break;
                    case TelemetryItemKind.DecisionTransition:
                        parsed = Add(batch.Transitions, TelemetryPayloadParser.ParseTransition(item, tenantId, sessionId));
                        break;
                    default:
                        batch.Rejected.Add(new RejectedItem(item, RejectionCause.UnknownKind));
                        continue;
                }

                if (!parsed)
                    batch.Rejected.Add(new RejectedItem(item, RejectionCause.UnparseablePayload));
            }

            return batch;

            static bool Add<T>(List<T> target, T? record) where T : class
            {
                if (record == null) return false;
                target.Add(record);
                return true;
            }
        }

        /// <summary>
        /// Persists a fully routed batch. Events go through <see cref="EventIngestProcessor"/>
        /// (the full event pipeline); Signals + Transitions land directly in their primary tables.
        /// The returned <see cref="IngestOutcome"/> carries both the per-kind counts and the
        /// control-signal / SignalR payload for the response.
        /// </summary>
        private async Task<IngestOutcome> PersistItemsAsync(
            PartitionedBatch batch,
            string tenantId,
            string sessionId,
            SecurityValidationResult validation,
            SessionStatus? preFetchedStatus,
            DateTime? sentAt)
        {
            var events = batch.Events;

            // Signals + Transitions write directly; they don't feed into the event pipeline.
            var signalCount     = await _signalRepo.StoreBatchAsync(batch.Signals);
            var transitionCount = await _transitionRepo.StoreBatchAsync(batch.Transitions);

            int eventCount;
            string? adminAction;
            List<ServerAction>? pendingActions;
            SignalRMessageAction[] signalRMessages;

            if (events.Count > 0)
            {
                // Full event pipeline (rule engine, app-install aggregation, SignalR,
                // vulnerability correlation, webhooks, SLA breach, AdminAction detection,
                // ServerAction delivery).
                var eventRequest = new IngestEventsRequest
                {
                    SessionId = sessionId,
                    TenantId  = tenantId,
                    Events    = events,
                    SentAt    = sentAt,
                };
                var processed = await _eventProcessor.ProcessEventsAsync(eventRequest, validation, preFetchedStatus);

                eventCount      = processed.EventsProcessed;
                adminAction     = processed.AdminAction;
                pendingActions  = processed.PendingActions;
                signalRMessages = processed.SignalRMessages;
            }
            else
            {
                // Signal/Transition-only batch: no events means no event pipeline. We still honour
                // the control-signal contract (AdminAction + pending ServerActions) because the
                // agent reads them from every 2xx response regardless of which items it sent.
                eventCount = 0;
                var (aa, pa) = await ReadControlSignalsAsync(tenantId, sessionId);
                adminAction     = aa;
                pendingActions  = pa;
                signalRMessages = Array.Empty<SignalRMessageAction>();
            }

            return new IngestOutcome
            {
                EventCount      = eventCount,
                SignalCount     = signalCount,
                TransitionCount = transitionCount,
                AdminAction     = adminAction,
                PendingActions  = pendingActions,
                SignalRMessages = signalRMessages,
            };
        }

        /// <summary>
        /// Fetches <c>AdminAction</c> (session marked terminal out-of-band) and pending
        /// <c>ServerAction</c>s for Signal/Transition-only batches (no <see cref="EventIngestProcessor"/>
        /// invocation).
        /// </summary>
        private async Task<(string? adminAction, List<ServerAction>? pendingActions)>
            ReadControlSignalsAsync(string tenantId, string sessionId)
        {
            var session = await _sessionRepo.GetSessionAsync(tenantId, sessionId);
            if (session == null) return (null, null);

            // AdminAction carries the portal-button signal only. The old logic (Status ==
            // Succeeded/Failed → AdminAction) also fired for agent-reported completion and made
            // every follow-up signal/transition upload look like an out-of-band admin override.
            string? adminAction = session.AdminMarkedAction;

            List<ServerAction>? pendingActions = null;
            if (!string.IsNullOrEmpty(session.PendingActionsJson))
            {
                var fetched = await _sessionRepo.FetchAndClearPendingActionsAsync(tenantId, sessionId);
                if (fetched.Count > 0) pendingActions = fetched;
            }

            return (adminAction, pendingActions);
        }

        private sealed class IngestOutcome
        {
            public int EventCount;
            public int SignalCount;
            public int TransitionCount;
            public string? AdminAction;
            public List<ServerAction>? PendingActions;
            public SignalRMessageAction[] SignalRMessages = Array.Empty<SignalRMessageAction>();
        }

        /// <summary>Why an item was refused — the split the ops event and the poison reason report.</summary>
        internal enum RejectionCause
        {
            /// <summary><see cref="TelemetryItemDto.Kind"/> is not a <see cref="TelemetryItemKind"/> name (or has no ingest arm).</summary>
            UnknownKind,
            /// <summary>The Kind is known but <see cref="TelemetryPayloadParser"/> could not use the payload.</summary>
            UnparseablePayload,
        }

        internal readonly struct RejectedItem
        {
            public RejectedItem(TelemetryItemDto item, RejectionCause cause)
            {
                RowKey = item.RowKey;
                TelemetryItemId = item.TelemetryItemId;
                Kind = item.Kind;
                Cause = cause;
            }

            public string RowKey { get; }
            public long TelemetryItemId { get; }
            public string Kind { get; }
            public RejectionCause Cause { get; }
        }

        /// <summary>Output of <see cref="PartitionBatch"/>: the routed records plus the items it refused.</summary>
        internal sealed class PartitionedBatch
        {
            public PartitionedBatch(int received) { Received = received; }

            public int Received { get; }
            public List<EnrollmentEvent> Events { get; } = new();
            public List<SignalRecord> Signals { get; } = new();
            public List<DecisionTransitionRecord> Transitions { get; } = new();
            public List<RejectedItem> Rejected { get; } = new();
        }

        /// <summary>
        /// Buffers the request body under a strict byte cap, then deserialises the telemetry batch
        /// directly off the buffered bytes. Returns <c>(true, null)</c> as soon as the cap would be
        /// exceeded — the stream isn't fully drained, which bounds memory use even for a malicious
        /// sender (strict greater-than, so a payload equal to the cap is accepted).
        /// <para>
        /// Hot-path note: this deserialises through a <see cref="JsonTextReader"/> over the buffered
        /// <see cref="MemoryStream"/> rather than materialising a full UTF-16 <c>string</c> + a
        /// <c>ToArray()</c> copy first. That removes ~3× the body size in transient allocations per
        /// request on the highest-volume backend endpoint — the reader decodes UTF-8 incrementally
        /// in 8&#160;KB chunks. The cap is still applied <i>before</i> parsing, so an over-cap body
        /// never reaches the deserialiser.
        /// </para>
        /// Throws <see cref="JsonException"/> on malformed JSON (caller maps to 400). The
        /// deserialiser uses the same default settings as <c>JsonConvert.DeserializeObject</c>, so
        /// the parse semantics are unchanged — only the intermediate allocations are gone.
        /// </summary>
        internal static async Task<(bool exceeded, List<TelemetryItemDto>? items)> ReadBodyWithSizeCapAsync(
            Stream source, int maxBytes)
        {
            using var buffered = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            long total = 0;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes) return (true, null);
                await buffered.WriteAsync(buffer, 0, read);
            }

            buffered.Position = 0;
            using var textReader = new StreamReader(buffered, System.Text.Encoding.UTF8);
            using var jsonReader = new JsonTextReader(textReader);
            var items = JsonSerializer.CreateDefault().Deserialize<List<TelemetryItemDto>>(jsonReader);
            return (false, items);
        }

        /// <summary>
        /// Scans the batch for any item whose <see cref="TelemetryItemDto.PartitionKey"/> differs
        /// from the first item's. Returns true + the offending index/value on mismatch; false if
        /// every item shares the same PartitionKey (or the batch has &lt; 2 items). Pure so the
        /// guard can be unit-tested without a live HTTP trigger.
        /// </summary>
        internal static bool FindMismatchingPartitionKey(
            IReadOnlyList<TelemetryItemDto> items,
            out int mismatchIndex,
            out string? mismatchedValue)
        {
            mismatchIndex = -1;
            mismatchedValue = null;
            if (items is null || items.Count < 2) return false;

            var expected = items[0].PartitionKey;
            for (var i = 1; i < items.Count; i++)
            {
                if (!string.Equals(items[i].PartitionKey, expected, StringComparison.Ordinal))
                {
                    mismatchIndex = i;
                    mismatchedValue = items[i].PartitionKey;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Parses the <c>X-Send-Time-Utc</c> header (agent device-clock send time, ISO-8601
        /// round-trip). Returns null for absent/unparseable values and for anything before
        /// year 2000 — Table Storage rejects pre-1601 DateTimes outright, and a sub-2000
        /// value can only be garbage, never a measurable clock offset. Deliberately NO upper
        /// clamp: a future-dated send time IS the device-clock-error measurement this field
        /// exists to capture.
        /// </summary>
        internal static DateTime? ParseSendTimeHeader(string? headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return null;

            if (!DateTimeOffset.TryParse(
                    headerValue,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
                return null;

            var utc = parsed.UtcDateTime;
            return utc.Year >= 2000 ? utc : (DateTime?)null;
        }

        /// <summary>
        /// PartitionKey convention is <c>{tenantId}_{sessionId}</c> — both GUIDs with dashes, so
        /// splitting on the single underscore between them is unambiguous. Returns false for any
        /// shape that doesn't match (malformed, extra parts, empty halves).
        /// </summary>
        internal static bool TryParsePartitionKey(string partitionKey, out string tenantId, out string sessionId)
        {
            tenantId = string.Empty;
            sessionId = string.Empty;
            if (string.IsNullOrEmpty(partitionKey)) return false;

            var parts = partitionKey.Split('_');
            if (parts.Length != 2) return false;
            if (string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1])) return false;

            tenantId = parts[0];
            sessionId = parts[1];
            return true;
        }

        /// <summary>
        /// The generic error envelope for every refusal the agent only needs the status of (it
        /// reads the first 200 characters of the body as the reason text and nothing else).
        /// </summary>
        private static Task<HttpResponseData> WriteErrorAsync(
            HttpRequestData req, HttpStatusCode status, string message, int? retryAfterSeconds = null)
            => req.ErrorAsync(status, ApiErrorWriter.DefaultCode(status), message, retryAfterSeconds: retryAfterSeconds);
    }

    /// <summary>
    /// Multi-binding output shape of the agent ingest endpoint: the HTTP response plus the
    /// SignalR messages for the live UI push.
    /// </summary>
    public class IngestEventsOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = SignalRGroupHelper.HubName)]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }
}
