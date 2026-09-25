using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Graph;
using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Binds the agent's client certificate to a device object that actually exists in the tenant
    /// the request claims: the certificate's Subject CN carries the Intune managedDevice id, and
    /// this validator looks exactly that id up in the tenant's own
    /// <c>deviceManagement/managedDevices</c> inventory.
    /// <para>
    /// <see cref="CertTenantBinding"/> already proves the certificate was issued to this tenant.
    /// This adds the second half: that the specific device is still one of the tenant's enrolled
    /// devices. That is proof enough to admit a device without any pre-registration, which is
    /// what the Intune Enrollment Validation option does (<see cref="ValidateAsync"/>). For
    /// devices admitted by another validator the same lookup only observes
    /// (<see cref="ObserveInBackground"/>).
    /// </para>
    /// </summary>
    /// <remarks>
    /// Same cache/retry/budget contract as <see cref="DeviceAssociationValidator"/> (30 min
    /// positive, 5 min negative, 2 attempts inside the chain budget, transient never cached), with
    /// three deliberate differences:
    /// <list type="bullet">
    /// <item><description>A point-GET on <c>managedDevices/{id}</c> rather than an OData
    /// <c>$filter</c> (D-125): no filter literal to inject into, and a 404 is an unambiguous
    /// "not this tenant's device".</description></item>
    /// <item><description>A 404 for a certificate younger than <see cref="RecentEnrollmentWindow"/>
    /// is <see cref="IntuneDeviceBindingOutcome.NotFoundRecentEnrollment"/>: the device object may
    /// simply not be visible in Graph yet, so the caller answers 503 Retry-After instead of 403 and
    /// nothing is cached.</description></item>
    /// <item><description>Observation never runs without the Graph permission: whether the tenant
    /// granted it is read from the cached app token's roles claim, so a tenant without the add-on
    /// costs no Graph call at all.</description></item>
    /// </list>
    /// </remarks>
    public class IntuneDeviceBindingValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long after the certificate's NotBefore a 404 counts as "not visible yet" rather than
        /// "not this tenant's device". Generous on purpose: the window only turns a 403 into a 503
        /// and never admits anything, and whether Intune backdates NotBefore is unmeasured
        /// (rejected devices reported their first call at least ~12 min after NotBefore).
        /// </summary>
        internal static readonly TimeSpan RecentEnrollmentWindow = TimeSpan.FromMinutes(60);

        /// <summary>How long a tenant's permission verdict is reused before asking the detector again.</summary>
        private static readonly TimeSpan PermissionVerdictTtl = TimeSpan.FromMinutes(2);

        /// <summary>Upper bound for one background observation, so an in-flight marker cannot outlive a stuck call.</summary>
        private static readonly TimeSpan InFlightTtl = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Pause after an observation that ended transient (nothing was cached), so a device whose
        /// object is not visible yet costs one Graph call per interval, not one per request.
        /// </summary>
        private static readonly TimeSpan TransientObservationCooldown = TimeSpan.FromSeconds(30);

        /// <summary>
        /// managementState values of a device that is being removed from management. Such a device
        /// still has an object, but it is on its way out, so it must not be admitted. Every other
        /// state (managed, unhealthy, discovered, *Failed, *Canceled) is a device Intune still manages.
        /// </summary>
        private static readonly HashSet<string> LeavingManagementStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "retirePending", "retireIssued", "wipePending", "wipeIssued", "deletePending",
        };

        private readonly ILogger<IntuneDeviceBindingValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;
        private readonly IGraphFeatureDetector _graphFeatureDetector;

        public IntuneDeviceBindingValidator(
            ILogger<IntuneDeviceBindingValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService,
            IGraphFeatureDetector graphFeatureDetector)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
            _graphFeatureDetector = graphFeatureDetector;
        }

        /// <summary>
        /// Resolves the Intune device id (from the client certificate's Subject CN) against the
        /// tenant's managedDevices inventory. Runs regardless of whether the permission was
        /// detected: an admitting call must be able to pick up a freshly granted permission through
        /// the 401/403 token refresh (<see cref="GraphAuthFailure"/>). Never throws.
        /// </summary>
        public virtual async Task<IntuneDeviceBindingResult> ValidateAsync(
            string tenantId,
            string? intuneDeviceId,
            DateTimeOffset? certNotBefore,
            IntuneDeviceBindingRole role,
            string? sessionId = null,
            CancellationToken ct = default)
        {
            // The id becomes a URL path segment. The strict GUID gate keeps it from being anything
            // else - the same rule the rest of the security path applies.
            if (!SecurityValidator.IsValidGuid(intuneDeviceId))
            {
                return new IntuneDeviceBindingResult
                {
                    Outcome = IntuneDeviceBindingOutcome.NoDeviceIdInCert,
                    ErrorMessage = "Certificate subject does not carry a valid Intune device id"
                };
            }

            var normalizedId = intuneDeviceId!.Trim().ToLowerInvariant();
            var cacheKey = BuildCacheKey(tenantId, normalizedId);

            if (_cache.TryGetValue(cacheKey, out IntuneDeviceBindingResult? cached) && cached != null)
                return cached.AsCacheHit();

            const int maxAttempts = 2;
            IntuneDeviceBindingResult? lastTransient = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await TryValidateViaGraphAsync(tenantId, normalizedId, certNotBefore, cacheKey, attempt, ct);

                if (!result.IsTransient)
                {
                    LogLookup(result, tenantId, certNotBefore, role, sessionId);
                    return result;
                }

                lastTransient = result;
                // Budget spent (chain token cancelled): no second attempt on a request the agent
                // has abandoned - the transient result becomes the 503 Retry-After.
                if (attempt == maxAttempts || ct.IsCancellationRequested)
                    break;

                await DelayBeforeRetryAsync(ct);
            }

            LogLookup(lastTransient!, tenantId, certNotBefore, role, sessionId);
            return lastTransient!;
        }

        /// <summary>
        /// The cached result for this device, if a lookup already ran. Lets the caller stamp the
        /// observation on the request row without waiting for Graph.
        /// </summary>
        public virtual IntuneDeviceBindingResult? TryGetCached(string tenantId, string? intuneDeviceId)
        {
            if (!SecurityValidator.IsValidGuid(intuneDeviceId))
                return null;

            return _cache.TryGetValue(BuildCacheKey(tenantId, intuneDeviceId!.Trim().ToLowerInvariant()), out IntuneDeviceBindingResult? cached)
                ? cached?.AsCacheHit()
                : null;
        }

        /// <summary>
        /// Observation for a device another validator admitted: fills the cache in the background so
        /// later requests can stamp the outcome, without adding Graph latency to an admitted request.
        /// Does nothing when the tenant has not granted DeviceManagementManagedDevices.Read.All
        /// (read from the cached app token, no Graph call), when the result is already cached, or
        /// when a lookup for the device is already running. Never throws.
        /// </summary>
        public virtual void ObserveInBackground(string tenantId, string? intuneDeviceId, DateTimeOffset? certNotBefore, string? sessionId)
        {
            if (!SecurityValidator.IsValidGuid(intuneDeviceId))
                return;

            var normalizedId = intuneDeviceId!.Trim().ToLowerInvariant();
            var cacheKey = BuildCacheKey(tenantId, normalizedId);
            var permissionKey = BuildPermissionKey(tenantId);

            if (_cache.TryGetValue(cacheKey, out _))
                return;
            if (_cache.TryGetValue(permissionKey, out bool granted) && !granted)
                return;

            var inFlightKey = cacheKey + ":inflight";
            if (_cache.TryGetValue(inFlightKey, out _))
                return;
            _cache.Set(inFlightKey, true, InFlightTtl);

            _ = Task.Run(async () =>
            {
                var cooldown = false;
                try
                {
                    using var budget = DeviceValidationBudget.CreateChainCts();

                    if (!_cache.TryGetValue(permissionKey, out bool hasPermission))
                    {
                        hasPermission = await _graphFeatureDetector.HasPermissionAsync(
                            tenantId, GraphAppPermissions.DeviceManagementManagedDevicesReadAll, budget.Token);
                        _cache.Set(permissionKey, hasPermission, PermissionVerdictTtl);
                    }

                    if (hasPermission)
                    {
                        var result = await ValidateAsync(tenantId, normalizedId, certNotBefore, IntuneDeviceBindingRole.Observing, sessionId, budget.Token);
                        cooldown = result.IsTransient;
                    }
                }
                catch (Exception ex)
                {
                    cooldown = true;
                    _logger.LogWarning(ex, "AgentCertDeviceBinding observation failed for tenant {TenantId} - ignored.", tenantId);
                }
                finally
                {
                    // A transient outcome caches nothing, so the in-flight marker doubles as the
                    // retry cooldown; otherwise the cached result answers every later request.
                    if (cooldown)
                        _cache.Set(inFlightKey, true, TransientObservationCooldown);
                    else
                        _cache.Remove(inFlightKey);
                }
            });
        }

        /// <summary>Retry pause that yields early when the chain budget runs out (never throws).</summary>
        private static async Task DelayBeforeRetryAsync(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { }
        }

        private async Task<IntuneDeviceBindingResult> TryValidateViaGraphAsync(
            string tenantId, string normalizedId, DateTimeOffset? certNotBefore, string cacheKey, int attempt, CancellationToken chain)
        {
            using var attemptCts = DeviceValidationBudget.CreateAttemptCts(chain);
            var ct = attemptCts.Token;
            try
            {
                var tokenResult = await _graphTokenService.GetAccessTokenAsync(tenantId, ct);
                if (string.IsNullOrEmpty(tokenResult.AccessToken))
                {
                    return new IntuneDeviceBindingResult
                    {
                        Outcome = IntuneDeviceBindingOutcome.Transient,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = "Graph access token could not be acquired"
                    };
                }

                var graphClient = _httpClientFactory.CreateClient();
                graphClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);

                // Point-GET: normalizedId passed the GUID gate, so the path segment is safe.
                var graphUrl = $"{Constants.GraphBaseUrl}/v1.0/deviceManagement/managedDevices/{normalizedId}"
                               + "?$select=id,deviceName,enrolledDateTime,azureADDeviceId,managementState,"
                               + "serialNumber,managedDeviceOwnerType,deviceEnrollmentType,azureADRegistered";

                var response = await graphClient.GetAsync(graphUrl, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // A young certificate means the device may simply not be visible yet: say so and
                    // cache nothing, so the next attempt sees the object as soon as it appears.
                    if (IsRecentEnrollment(certNotBefore, DateTimeOffset.UtcNow))
                    {
                        return new IntuneDeviceBindingResult
                        {
                            Outcome = IntuneDeviceBindingOutcome.NotFoundRecentEnrollment,
                            IntuneDeviceId = normalizedId,
                            ErrorMessage = $"Device '{normalizedId}' is not visible in Intune yet (recent enrollment)"
                        };
                    }

                    return CacheAndReturn(cacheKey, new IntuneDeviceBindingResult
                    {
                        Outcome = IntuneDeviceBindingOutcome.NotFound,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = $"Device '{normalizedId}' is not an enrolled Intune device in this tenant"
                    }, isPositive: false);
                }

                // 401/403 on attempt 1: the cached token may predate the tenant's grant (see
                // GraphAuthFailure) - drop it and let the loop retry with a fresh one before the
                // definitive "not granted" verdict below is cached against a stale token.
                if (GraphAuthFailure.TryRecoverStaleToken(_graphTokenService, _logger, nameof(IntuneDeviceBindingValidator), tenantId, response.StatusCode, attempt))
                {
                    return new IntuneDeviceBindingResult
                    {
                        Outcome = IntuneDeviceBindingOutcome.Transient,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = $"Graph auth failure {(int)response.StatusCode}; token refreshed"
                    };
                }

                if (GraphAuthFailure.IsAuthFailure(response.StatusCode))
                {
                    GraphAuthFailure.LogPermissionMissing(_logger, nameof(IntuneDeviceBindingValidator), tenantId, response.StatusCode);
                    // Configuration state, not an outage: retrying cannot fix a missing grant.
                    // Cached briefly so a fresh grant is picked up quickly.
                    return CacheAndReturn(cacheKey, new IntuneDeviceBindingResult
                    {
                        Outcome = IntuneDeviceBindingOutcome.PermissionMissing,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = "Managed device lookup not permitted - grant the optional 'IntuneDeviceBinding' Graph feature (DeviceManagementManagedDevices.Read.All)"
                    }, isPositive: false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Intune device binding Graph query failed for tenant {TenantId} (attempt {Attempt}). Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId, attempt, (int)response.StatusCode, responseBody);

                    return new IntuneDeviceBindingResult
                    {
                        Outcome = IntuneDeviceBindingOutcome.Transient,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = $"Graph query failed with status {(int)response.StatusCode}"
                    };
                }

                var result = ParseManagedDeviceResponse(responseBody, normalizedId);
                return CacheAndReturn(cacheKey, result, isPositive: result.IsValid);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Budget exhausted (per-attempt or chain) - transient, never cached.
                return new IntuneDeviceBindingResult
                {
                    Outcome = IntuneDeviceBindingOutcome.Transient,
                    IntuneDeviceId = normalizedId,
                    ErrorMessage = "Managed device lookup timed out"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during Intune device binding for tenant {TenantId}, deviceId {IntuneDeviceId} (attempt {Attempt})",
                    tenantId, normalizedId, attempt);

                return new IntuneDeviceBindingResult
                {
                    Outcome = IntuneDeviceBindingOutcome.Transient,
                    IntuneDeviceId = normalizedId,
                    ErrorMessage = $"Error during Intune device binding: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// One Warning line per real Graph lookup (never for cache hits: one enrollment produced 136
        /// requests but a single Graph call). Carries the two ages that answer the enrollment-race
        /// question: how old the device object and the certificate were at lookup time.
        /// </summary>
        private void LogLookup(IntuneDeviceBindingResult result, string tenantId, DateTimeOffset? certNotBefore, IntuneDeviceBindingRole role, string? sessionId)
        {
            var now = DateTimeOffset.UtcNow;
            var enrolledAgeSeconds = result.EnrolledDateTime.HasValue
                ? (long)Math.Round((now - result.EnrolledDateTime.Value).TotalSeconds)
                : -1;
            var certAgeSeconds = certNotBefore.HasValue
                ? (long)Math.Round((now - certNotBefore.Value).TotalSeconds)
                : -1;

            _logger.LogWarning(
                "AgentCertDeviceBinding outcome={Outcome} role={Role} tenant={TenantId} "
                + "certDeviceId={CertDeviceId} device={DeviceName} enrolledAgeSeconds={EnrolledAgeSeconds} "
                + "certAgeSeconds={CertAgeSeconds} mgmtState={ManagementState} ownerType={OwnerType} "
                + "enrollmentType={EnrollmentType} session={SessionId} detail={Detail}",
                result.Outcome, role, tenantId,
                result.IntuneDeviceId ?? "n/a", result.DeviceName ?? "n/a", enrolledAgeSeconds,
                certAgeSeconds, result.ManagementState ?? "n/a", result.OwnerType ?? "n/a",
                result.EnrollmentType ?? "n/a", sessionId ?? "n/a", result.ErrorMessage ?? "n/a");
        }

        /// <summary>
        /// Pure-function: maps a <c>managedDevices/{id}</c> Graph response body to a result.
        /// Re-checks the returned <c>id</c> so a redirected or widened lookup can never produce a
        /// false positive, and turns a device on its way out of management into
        /// <see cref="IntuneDeviceBindingOutcome.NotManaged"/>.
        /// </summary>
        internal static IntuneDeviceBindingResult ParseManagedDeviceResponse(string responseBody, string normalizedId)
        {
            var notFound = new IntuneDeviceBindingResult
            {
                Outcome = IntuneDeviceBindingOutcome.NotFound,
                IntuneDeviceId = normalizedId,
                ErrorMessage = $"Device '{normalizedId}' is not an enrolled Intune device in this tenant"
            };

            JObject? device;
            try
            {
                device = JsonConvert.DeserializeObject<JObject>(responseBody);
            }
            catch (JsonException)
            {
                return notFound;
            }

            var returnedId = device?["id"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(returnedId) ||
                !string.Equals(returnedId, normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                return notFound;
            }

            var managementState = device?["managementState"]?.ToString();
            var leaving = managementState != null && LeavingManagementStates.Contains(managementState);

            return new IntuneDeviceBindingResult
            {
                Outcome = leaving ? IntuneDeviceBindingOutcome.NotManaged : IntuneDeviceBindingOutcome.Match,
                IntuneDeviceId = normalizedId,
                DeviceName = device?["deviceName"]?.ToString(),
                AzureAdDeviceId = device?["azureADDeviceId"]?.ToString(),
                ManagementState = managementState,
                SerialNumber = NullIfEmpty(device?["serialNumber"]?.ToString()),
                OwnerType = NullIfEmpty(device?["managedDeviceOwnerType"]?.ToString()),
                EnrollmentType = NullIfEmpty(device?["deviceEnrollmentType"]?.ToString()),
                AzureAdRegistered = device?["azureADRegistered"]?.Type == JTokenType.Boolean
                    ? device["azureADRegistered"]!.Value<bool>()
                    : null,
                EnrolledDateTime = ParseEnrolledDateTime(device?["enrolledDateTime"]?.ToString()),
                ErrorMessage = leaving
                    ? $"Device '{normalizedId}' is being removed from Intune management (state {managementState})"
                    : null
            };
        }

        /// <summary>
        /// True when the certificate was issued less than <see cref="RecentEnrollmentWindow"/> ago.
        /// An unknown NotBefore is never recent: without it there is no evidence of a race.
        /// </summary>
        internal static bool IsRecentEnrollment(DateTimeOffset? certNotBefore, DateTimeOffset now)
            => certNotBefore.HasValue && now - certNotBefore.Value < RecentEnrollmentWindow;

        /// <summary>
        /// Parses Graph's <c>enrolledDateTime</c>. Intune returns <c>0001-01-01T00:00:00Z</c> for
        /// devices it has no enrollment timestamp for; that is absence, not a date, so it maps to
        /// <c>null</c> rather than a bogus age of two thousand years.
        /// </summary>
        internal static DateTimeOffset? ParseEnrolledDateTime(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            if (!DateTimeOffset.TryParse(
                    raw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return null;
            }

            return parsed.Year <= 1 ? null : parsed;
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        internal static string BuildCacheKey(string tenantId, string intuneDeviceId)
            => $"intune-device-binding:{tenantId}:{intuneDeviceId}";

        internal static string BuildPermissionKey(string tenantId)
            => $"intune-device-binding-permission:{tenantId}";

        private IntuneDeviceBindingResult CacheAndReturn(
            string cacheKey, IntuneDeviceBindingResult result, bool isPositive)
        {
            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = isPositive ? PositiveCacheTtl : NegativeCacheTtl
            });
            return result;
        }
    }

    /// <summary>Why a lookup ran: to admit a device, or to observe one another validator admitted.</summary>
    public enum IntuneDeviceBindingRole
    {
        Admitting = 0,
        Observing = 1
    }

    /// <summary>
    /// Stable outcome codes for the device-binding check. Emitted verbatim in the
    /// <c>AgentCertDeviceBinding</c> log and as the <c>CertDeviceBinding</c> request dimension —
    /// operators match on them in KQL, so keep the names stable.
    /// </summary>
    public enum IntuneDeviceBindingOutcome
    {
        /// <summary>Device id from the certificate resolves to a managed device in this tenant.</summary>
        Match = 0,

        /// <summary>No managedDevice with that id in this tenant, and the certificate is not recent.</summary>
        NotFound = 1,

        /// <summary>Certificate subject carried no usable Intune device id.</summary>
        NoDeviceIdInCert = 2,

        /// <summary>DeviceManagementManagedDevices.Read.All not granted in this tenant.</summary>
        PermissionMissing = 3,

        /// <summary>Graph outage, token failure, timeout or exception — says nothing about the device.</summary>
        Transient = 4,

        /// <summary>The device exists but is being retired, wiped or deleted.</summary>
        NotManaged = 5,

        /// <summary>No managedDevice yet, but the certificate is recent: the object may not be visible yet.</summary>
        NotFoundRecentEnrollment = 6
    }

    public class IntuneDeviceBindingResult
    {
        public IntuneDeviceBindingOutcome Outcome { get; set; }

        /// <summary>True only for <see cref="IntuneDeviceBindingOutcome.Match"/>.</summary>
        public bool IsValid => Outcome == IntuneDeviceBindingOutcome.Match;

        /// <summary>
        /// True when the failure says nothing definitive about the device (Graph error, token
        /// issue, or a device object that may not be visible yet). Transient results are never cached.
        /// </summary>
        public bool IsTransient => Outcome == IntuneDeviceBindingOutcome.Transient
                                   || Outcome == IntuneDeviceBindingOutcome.NotFoundRecentEnrollment;

        /// <summary>Intune device id taken from the client certificate's Subject CN.</summary>
        public string? IntuneDeviceId { get; set; }

        /// <summary>Intune device name — diagnostic only, never a gate.</summary>
        public string? DeviceName { get; set; }

        /// <summary>Entra device id of the same device, if Intune knows one — diagnostic only.</summary>
        public string? AzureAdDeviceId { get; set; }

        /// <summary>Intune management state; a leaving state turns a match into <see cref="IntuneDeviceBindingOutcome.NotManaged"/>.</summary>
        public string? ManagementState { get; set; }

        /// <summary>Serial number Intune recorded for the device — compared against the agent's serial header.</summary>
        public string? SerialNumber { get; set; }

        /// <summary>Intune ownership (<c>company</c>, <c>personal</c>, <c>unknown</c>) — telemetry only.</summary>
        public string? OwnerType { get; set; }

        /// <summary>Intune deviceEnrollmentType (e.g. <c>windowsAzureADJoin</c>) — telemetry only.</summary>
        public string? EnrollmentType { get; set; }

        /// <summary>True for a workplace-joined (Entra registered) device — telemetry only.</summary>
        public bool? AzureAdRegistered { get; set; }

        /// <summary>
        /// When Intune recorded the enrollment. The age of this value at request time is what
        /// tells a genuine race (object created seconds ago) apart from a foreign certificate.
        /// </summary>
        public DateTimeOffset? EnrolledDateTime { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>
        /// True when this result came from the in-memory cache rather than a fresh Graph lookup.
        /// </summary>
        public bool ServedFromCache { get; set; }

        /// <summary>
        /// Copy of this result marked as a cache hit, so the shared cached instance is never
        /// mutated by a caller.
        /// </summary>
        internal IntuneDeviceBindingResult AsCacheHit() => new()
        {
            Outcome = Outcome,
            IntuneDeviceId = IntuneDeviceId,
            DeviceName = DeviceName,
            AzureAdDeviceId = AzureAdDeviceId,
            ManagementState = ManagementState,
            SerialNumber = SerialNumber,
            OwnerType = OwnerType,
            EnrollmentType = EnrollmentType,
            AzureAdRegistered = AzureAdRegistered,
            EnrolledDateTime = EnrolledDateTime,
            ErrorMessage = ErrorMessage,
            ServedFromCache = true
        };
    }
}
