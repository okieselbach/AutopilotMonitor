using AutopilotMonitor.Shared;
using System;
using System.Net.Http.Headers;
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
    /// devices, so a certificate lifted from a decommissioned or wiped machine no longer resolves.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Built as a near-sibling of <see cref="CloudPcDeviceValidator"/> — same DI dependencies and
    /// the same cache/retry/transient contract (30 min positive, 5 min negative, 2 attempts with a
    /// 2s back-off, transient on Graph 5xx / token failure / exception). Two deliberate differences:
    /// <list type="bullet">
    /// <item><description>A point-GET on <c>managedDevices/{id}</c> rather than an OData
    /// <c>$filter</c>. There is no filter literal to inject into, and a 404 is an unambiguous
    /// "not this tenant's device".</description></item>
    /// <item><description><see cref="IntuneDeviceBindingResult.EnrolledDateTime"/> is carried out
    /// so callers can record how old the device object was at request time. That is the open
    /// question this check must answer before it could ever gate enrollment: a device whose object
    /// appears only after the agent's first call is a race, not an attack.</description></item>
    /// </list>
    /// SHADOW ONLY (stage 3): nothing consumes the result for an authorization decision. Grep
    /// marker for the enforcement change: <c>CERT-DEVICE-BINDING-SHADOW</c>.
    /// </remarks>
    public class IntuneDeviceBindingValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger<IntuneDeviceBindingValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;

        public IntuneDeviceBindingValidator(
            ILogger<IntuneDeviceBindingValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
        }

        /// <summary>
        /// Resolves the Intune device id (from the client certificate's Subject CN) against the
        /// tenant's managedDevices inventory. Never throws.
        /// </summary>
        public async Task<IntuneDeviceBindingResult> ValidateAsync(
            string tenantId,
            string? intuneDeviceId,
            string? sessionId = null)
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
                return cached;

            const int maxAttempts = 2;
            IntuneDeviceBindingResult? lastTransient = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await TryValidateViaGraphAsync(tenantId, normalizedId, sessionId, cacheKey, attempt);

                if (!result.IsTransient)
                    return result;

                lastTransient = result;
                if (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "Intune device binding transient failure for tenant {TenantId}, deviceId {IntuneDeviceId} (attempt {Attempt}/{MaxAttempts}). Retrying...",
                        tenantId, normalizedId, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            return lastTransient!;
        }

        private async Task<IntuneDeviceBindingResult> TryValidateViaGraphAsync(
            string tenantId, string normalizedId, string? sessionId, string cacheKey, int attempt)
        {
            try
            {
                var tokenResult = await _graphTokenService.GetAccessTokenAsync(tenantId);
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
                               + "?$select=id,deviceName,enrolledDateTime,azureADDeviceId,managementState";

                var response = await graphClient.GetAsync(graphUrl);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Definitive: this tenant has no managedDevice with that id. Either the device
                    // belongs elsewhere, or its object has not appeared yet - the race this shadow
                    // pass exists to measure.
                    return CacheAndReturn(cacheKey, new IntuneDeviceBindingResult
                    {
                        Outcome = IntuneDeviceBindingOutcome.NotFound,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = $"Device '{normalizedId}' is not an enrolled Intune device in this tenant"
                    }, isPositive: false);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
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
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during Intune device binding for tenant {TenantId}, session {SessionId}, deviceId {IntuneDeviceId} (attempt {Attempt})",
                    tenantId, sessionId ?? "<none>", normalizedId, attempt);

                return new IntuneDeviceBindingResult
                {
                    Outcome = IntuneDeviceBindingOutcome.Transient,
                    IntuneDeviceId = normalizedId,
                    ErrorMessage = $"Error during Intune device binding: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Pure-function: maps a <c>managedDevices/{id}</c> Graph response body to a result.
        /// Re-checks the returned <c>id</c> so a redirected or widened lookup can never produce a
        /// false positive.
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

            return new IntuneDeviceBindingResult
            {
                Outcome = IntuneDeviceBindingOutcome.Match,
                IntuneDeviceId = normalizedId,
                DeviceName = device?["deviceName"]?.ToString(),
                AzureAdDeviceId = device?["azureADDeviceId"]?.ToString(),
                ManagementState = device?["managementState"]?.ToString(),
                EnrolledDateTime = ParseEnrolledDateTime(device?["enrolledDateTime"]?.ToString())
            };
        }

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

        internal static string BuildCacheKey(string tenantId, string intuneDeviceId)
            => $"intune-device-binding:{tenantId}:{intuneDeviceId}";

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

    /// <summary>
    /// Stable outcome codes for the device-binding check. Emitted verbatim in the
    /// <c>AgentCertDeviceBinding</c> log and as the <c>CertDeviceBinding</c> request dimension —
    /// operators match on them in KQL, so keep the names stable.
    /// </summary>
    public enum IntuneDeviceBindingOutcome
    {
        /// <summary>Device id from the certificate resolves to a managedDevice in this tenant.</summary>
        Match = 0,

        /// <summary>No managedDevice with that id in this tenant (foreign device, or not yet created).</summary>
        NotFound = 1,

        /// <summary>Certificate subject carried no usable Intune device id.</summary>
        NoDeviceIdInCert = 2,

        /// <summary>DeviceManagementManagedDevices.Read.All not granted in this tenant.</summary>
        PermissionMissing = 3,

        /// <summary>Graph outage, token failure or exception — says nothing about the device.</summary>
        Transient = 4
    }

    public class IntuneDeviceBindingResult
    {
        public IntuneDeviceBindingOutcome Outcome { get; set; }

        /// <summary>True only for <see cref="IntuneDeviceBindingOutcome.Match"/>.</summary>
        public bool IsValid => Outcome == IntuneDeviceBindingOutcome.Match;

        /// <summary>
        /// True when the failure says nothing about the device (Graph error, token issue).
        /// Transient results are never cached.
        /// </summary>
        public bool IsTransient => Outcome == IntuneDeviceBindingOutcome.Transient;

        /// <summary>Intune device id taken from the client certificate's Subject CN.</summary>
        public string? IntuneDeviceId { get; set; }

        /// <summary>Intune device name — diagnostic only, never a gate.</summary>
        public string? DeviceName { get; set; }

        /// <summary>Entra device id of the same device, if Intune knows one — diagnostic only.</summary>
        public string? AzureAdDeviceId { get; set; }

        /// <summary>Intune management state — diagnostic only.</summary>
        public string? ManagementState { get; set; }

        /// <summary>
        /// When Intune recorded the enrollment. The age of this value at request time is what
        /// tells a genuine race (object created seconds ago) apart from a foreign certificate.
        /// </summary>
        public DateTimeOffset? EnrolledDateTime { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
