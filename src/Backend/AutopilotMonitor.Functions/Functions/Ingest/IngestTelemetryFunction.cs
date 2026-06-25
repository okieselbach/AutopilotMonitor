using System.IO;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Functions.Services.Indexing;
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
    /// V2 ingest endpoint. Consumes a heterogeneous batch of <see cref="TelemetryItemDto"/>s
    /// (Events + Signals + DecisionTransitions in a single JSON array, gzip-compressed on
    /// the wire — the <c>UseRequestDecompression</c> middleware decompresses before we
    /// parse). Plan §2.7a / §M5 / M4.6.ε.
    /// <para>
    /// <b>Routing by <see cref="TelemetryItemDto.Kind"/>:</b>
    /// <list type="bullet">
    ///   <item><c>Event</c> → <see cref="EventIngestProcessor"/> (full pipeline parity with
    ///   /api/agent/ingest: rule engine, app-install aggregation, SignalR, vulnerability
    ///   correlation, webhooks, SLA breach, AdminAction detection, ServerAction delivery).
    ///   The processor is a deliberate copy of the legacy pipeline — see M5.b.2 rationale.</item>
    ///   <item><c>Signal</c> → <see cref="ISignalRepository.StoreBatchAsync"/></item>
    ///   <item><c>DecisionTransition</c> → <see cref="IDecisionTransitionRepository.StoreBatchAsync"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Response (M4.6.ε):</b> the agent parses <c>DeviceBlocked</c>/<c>UnblockAt</c>/
    /// <c>DeviceKillSignal</c>/<c>AdminAction</c>/<c>Actions</c> from the 2xx body and routes
    /// kill-switches through its <c>ServerActionDispatcher</c>. Populated from the same
    /// services the legacy /api/agent/ingest endpoint uses so behaviour is at parity.
    /// </para>
    /// </summary>
    public sealed class IngestTelemetryFunction
    {
        private readonly ILogger<IngestTelemetryFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly ISignalRepository _signalRepo;
        private readonly IDecisionTransitionRepository _transitionRepo;
        private readonly IIndexReconcileProducer _indexReconcileProducer;
        private readonly EventIngestProcessor _eventProcessor;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly BlockedVersionService _blockedVersionService;
        private readonly SessionDeletionGuard _deletionGuard;

        public IngestTelemetryFunction(
            ILogger<IngestTelemetryFunction> logger,
            ISessionRepository sessionRepo,
            ISignalRepository signalRepo,
            IDecisionTransitionRepository transitionRepo,
            IIndexReconcileProducer indexReconcileProducer,
            EventIngestProcessor eventProcessor,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            BootstrapSessionService bootstrapSessionService,
            BlockedDeviceService blockedDeviceService,
            BlockedVersionService blockedVersionService,
            SessionDeletionGuard deletionGuard)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _signalRepo = signalRepo;
            _transitionRepo = transitionRepo;
            _indexReconcileProducer = indexReconcileProducer;
            _eventProcessor = eventProcessor;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _blockedDeviceService = blockedDeviceService;
            _blockedVersionService = blockedVersionService;
            _deletionGuard = deletionGuard;
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
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator);

                if (errorResponse != null) return AsOutput(errorResponse);

                // Device + version kill-switches: short-circuit before parsing the body.
                var serialNumberHeader = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                    : null;
                var agentVersionHeader = req.Headers.Contains("X-Agent-Version")
                    ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault()
                    : null;

                var killResponse = await CheckKillSwitchesAsync(
                    req, tenantIdHeader, serialNumberHeader, agentVersionHeader);
                if (killResponse != null) return AsOutput(killResponse);

                // Defense in depth: cap decompressed body size before buffering it all into
                // memory + deserialising. Mirrors the legacy /api/agent/ingest NDJSON guard
                // using the same tenant-config knob. Request body here is already gzip-
                // decompressed by UseRequestDecompression — so the cap is on the actual JSON
                // bytes we'll feed to the parser.
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

                // Reuse the guard's full-row read: its Status feeds the stall-heal check inside
                // EventIngestProcessor, saving one Sessions point-read per batch.
                var preFetchedStatus = TryReadSessionStatus(guardSessionRow);

                // Partition + persist. Events are routed through EventIngestProcessor which runs the
                // full pipeline (rule engine / app-install aggregation / SignalR / webhooks / ...);
                // Signal + Transition go straight to their repositories.
                var outcome = await PersistItemsAsync(items, bodyTenantId, sessionId, validation, preFetchedStatus);

                _logger.LogInformation(
                    "IngestTelemetry: tenant={Tenant} session={Session} events={E} signals={S} transitions={T} unknown={U}",
                    bodyTenantId, sessionId, outcome.EventCount, outcome.SignalCount, outcome.TransitionCount, outcome.UnknownCount);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new IngestEventsResponse
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "IngestTelemetry: unhandled exception");
                return AsOutput(await WriteErrorAsync(req, HttpStatusCode.InternalServerError, "Internal server error"));
            }
        }

        private static IngestEventsOutput AsOutput(HttpResponseData response)
            => new IngestEventsOutput
            {
                HttpResponse = response,
                SignalRMessages = Array.Empty<SignalRMessageAction>(),
            };

        /// <summary>
        /// Runs device-serial and agent-version kill-switch checks. Returns a 200 response with
        /// <c>DeviceBlocked=true</c> (and optional <c>DeviceKillSignal=true</c>) if the caller
        /// should stop — or null if the request may proceed.
        /// </summary>
        private async Task<HttpResponseData?> CheckKillSwitchesAsync(
            HttpRequestData req, string tenantId, string? serialNumber, string? agentVersion)
        {
            if (!string.IsNullOrEmpty(serialNumber))
            {
                // Session-aware block: without body-parse we can't discriminate on SessionId,
                // so we use the tenant/serial blanket check. Session-scoped blocks still require
                // the agent to upload; the response just carries DeviceBlocked=true regardless of
                // session — tighter scoping lands once the body is parsed (M5.b.2 pipeline share).
                var (isBlocked, unblockAt, blockAction, _) =
                    await _blockedDeviceService.IsBlockedAsync(tenantId, serialNumber);
                if (isBlocked)
                {
                    var isKill = string.Equals(blockAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    _logger.LogWarning(
                        "IngestTelemetry: {Action} device tenant={Tenant} serial={Serial} unblockAt={UnblockAt}",
                        isKill ? "KILL" : "Block", tenantId, serialNumber, unblockAt);
                    return await WriteDeviceBlockedAsync(req, isKill, unblockAt,
                        isKill ? "Device has been issued a remote kill signal."
                               : "Device is temporarily blocked by an administrator.");
                }
            }

            if (!string.IsNullOrEmpty(agentVersion))
            {
                var (isVersionBlocked, versionAction, matchedPattern) =
                    await _blockedVersionService.IsVersionBlockedAsync(agentVersion);
                if (isVersionBlocked)
                {
                    var isKill = string.Equals(versionAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    _logger.LogWarning(
                        "IngestTelemetry: version {Action} tenant={Tenant} agentVersion={AgentVersion} pattern={Pattern}",
                        isKill ? "KILL" : "block", tenantId, agentVersion, matchedPattern);
                    return await WriteDeviceBlockedAsync(req, isKill, null,
                        isKill ? $"Agent version {agentVersion} has been issued a remote kill signal (pattern: {matchedPattern})."
                               : $"Agent version {agentVersion} is blocked by administrator (pattern: {matchedPattern}).");
                }
            }

            return null;
        }

        private async Task<HttpResponseData> WriteDeviceBlockedAsync(
            HttpRequestData req, bool isKill, DateTime? unblockAt, string message)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new IngestEventsResponse
            {
                Success = false,
                DeviceBlocked = true,
                DeviceKillSignal = isKill,
                UnblockAt = unblockAt,
                Message = message,
                ProcessedAt = DateTime.UtcNow,
            });
            return response;
        }

        /// <summary>
        /// Responds 410 Gone when the V2 cascade-delete guard refuses the batch. Per plan §5 PR3
        /// wiring table, telemetry ingest of a locked session is a terminal condition for the
        /// agent: the session is being torn down server-side and any further writes would create
        /// orphan rows. 410 is the documented status; the body shape mirrors the device-blocked
        /// response so existing agent code paths (which already short-circuit on Success=false)
        /// drop the batch without retry.
        /// </summary>
        private static async Task<HttpResponseData> WriteSessionLockedAsync(HttpRequestData req, SessionDeletionLockedException locked)
        {
            var response = req.CreateResponse(HttpStatusCode.Gone);
            await response.WriteAsJsonAsync(new IngestEventsResponse
            {
                Success = false,
                Message = $"Session is being deleted by an administrator (state={locked.CurrentState}); further telemetry will be rejected.",
                ProcessedAt = DateTime.UtcNow,
            });
            return response;
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
        /// Partitions the incoming batch by <see cref="TelemetryItemDto.Kind"/> and persists each
        /// kind through its destination path. Events go through <see cref="EventIngestProcessor"/>
        /// for full pipeline parity with legacy /api/agent/ingest; Signals + Transitions land
        /// directly in their new primary tables. The returned <see cref="IngestOutcome"/> carries
        /// both the per-kind counts and the control-signal / SignalR payload for the response.
        /// </summary>
        private async Task<IngestOutcome> PersistItemsAsync(
            IReadOnlyList<TelemetryItemDto> items,
            string tenantId,
            string sessionId,
            SecurityValidationResult validation,
            SessionStatus? preFetchedStatus)
        {
            var events      = new List<EnrollmentEvent>();
            var signals     = new List<SignalRecord>();
            var transitions = new List<DecisionTransitionRecord>();
            var unknown     = 0;

            foreach (var item in items)
            {
                switch (item.Kind)
                {
                    case "Event":
                        var evt = TelemetryPayloadParser.ParseEvent(item, tenantId, sessionId);
                        if (evt != null) events.Add(evt);
                        break;
                    case "Signal":
                        var sig = TelemetryPayloadParser.ParseSignal(item, tenantId, sessionId);
                        if (sig != null) signals.Add(sig);
                        break;
                    case "DecisionTransition":
                        var tr = TelemetryPayloadParser.ParseTransition(item, tenantId, sessionId);
                        if (tr != null) transitions.Add(tr);
                        break;
                    default:
                        unknown++;
                        _logger.LogWarning("IngestTelemetry: unknown Kind '{Kind}' (TelemetryItemId={Id})", item.Kind, item.TelemetryItemId);
                        break;
                }
            }

            // Signals + Transitions write directly; they don't feed into the event pipeline.
            var signalCount     = await _signalRepo.StoreBatchAsync(signals);
            var transitionCount = await _transitionRepo.StoreBatchAsync(transitions);

            // M5.d.2: after the primary commit, fan out one envelope per row onto the
            // telemetry-index-reconcile queue for the index-table writer (M5.d.3).
            // The producer is feature-flag-gated (AdminConfiguration.EnableIndexDualWrite,
            // default off) and never rethrows — see IIndexReconcileProducer contract.
            if (signals.Count > 0 || transitions.Count > 0)
            {
                var envelopes = IndexReconcileEnvelopeFactory.BuildBatch(signals, transitions);
                await _indexReconcileProducer.EnqueueBatchAsync(envelopes);
            }

            int eventCount;
            string? adminAction;
            List<ServerAction>? pendingActions;
            SignalRMessageAction[] signalRMessages;

            if (events.Count > 0)
            {
                // Full legacy-parity pipeline (rule engine, app-install aggregation, SignalR,
                // vulnerability correlation, webhooks, SLA breach, AdminAction detection,
                // ServerAction delivery). See EventIngestProcessor for the M5.b.2 copy-rationale.
                var eventRequest = new IngestEventsRequest
                {
                    SessionId = sessionId,
                    TenantId  = tenantId,
                    Events    = events,
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
                UnknownCount    = unknown,
                AdminAction     = adminAction,
                PendingActions  = pendingActions,
                SignalRMessages = signalRMessages,
            };
        }

        /// <summary>
        /// Fetches <c>AdminAction</c> (session marked terminal out-of-band) and pending
        /// <c>ServerAction</c>s for Signal/Transition-only batches (no <see cref="EventIngestProcessor"/>
        /// invocation). Mirror of the legacy inline logic.
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
            public int UnknownCount;
            public string? AdminAction;
            public List<ServerAction>? PendingActions;
            public SignalRMessageAction[] SignalRMessages = Array.Empty<SignalRMessageAction>();
        }

        /// <summary>
        /// Buffers the request body under a strict byte cap, then deserialises the telemetry batch
        /// directly off the buffered bytes. Returns <c>(true, null)</c> as soon as the cap would be
        /// exceeded — the stream isn't fully drained, which bounds memory use even for a malicious
        /// sender (strict greater-than, so a payload equal to the cap is accepted; matches the
        /// legacy NDJSON parser's guard semantics).
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

        private static async Task<HttpResponseData> WriteErrorAsync(
            HttpRequestData req, HttpStatusCode status, string message)
        {
            var response = req.CreateResponse(status);
            await response.WriteAsJsonAsync(new IngestEventsResponse
            {
                Success = false,
                EventsReceived = 0,
                EventsProcessed = 0,
                Message = message,
                ProcessedAt = DateTime.UtcNow,
            });
            return response;
        }
    }
}
