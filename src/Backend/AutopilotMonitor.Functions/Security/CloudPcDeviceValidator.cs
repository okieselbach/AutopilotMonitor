using AutopilotMonitor.Shared;
using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates Windows 365 Cloud PCs via the service-side Cloud PC inventory
    /// (<c>virtualEndpoint/cloudPCs</c>). Cloud PCs are structurally never
    /// Autopilot-registered, so instead of a serial lookup this validator binds the
    /// device identity to the MDM client certificate: the caller passes the Intune
    /// device id extracted from the chain-validated cert's Subject CN, and the device
    /// is valid only when the tenant has a <c>cloudPC</c> object whose
    /// <c>managedDeviceId</c> equals that id. Only machines provisioned by the
    /// Windows 365 service have such an object — no other enrolled device can pass.
    ///
    /// Built as a near-sibling of <see cref="AutopilotDeviceValidator"/> — same DI
    /// dependencies, same retry/cache/transient contract:
    ///   - 30 min positive cache, 5 min negative cache (transient failures NOT cached)
    ///   - 2 attempts with 2s back-off between attempts
    ///   - <see cref="CloudPcValidationResult.IsTransient"/> on Graph 5xx, token failures, exceptions
    /// Requires the optional Graph permission CloudPC.Read.All (a 403 from Graph is
    /// treated as definitive "not granted", not transient — see TryValidateViaGraphAsync).
    /// </summary>
    public class CloudPcDeviceValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger<CloudPcDeviceValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;

        public CloudPcDeviceValidator(
            ILogger<CloudPcDeviceValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
        }

        public async Task<CloudPcValidationResult> ValidateCloudPcAsync(
            string tenantId,
            string? intuneDeviceId,
            string? sessionId = null)
        {
            // The id is interpolated into an OData filter — a strict GUID gate is the
            // injection defense (same rule as SecurityValidator.IsValidGuid everywhere else).
            if (!SecurityValidator.IsValidGuid(intuneDeviceId))
            {
                return new CloudPcValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Certificate subject does not carry a valid Intune device id"
                };
            }

            var normalizedId = intuneDeviceId!.Trim().ToLowerInvariant();
            var cacheKey = BuildCacheKey(tenantId, normalizedId);

            if (_cache.TryGetValue(cacheKey, out CloudPcValidationResult? cached) && cached != null)
            {
                return cached;
            }

            const int maxAttempts = 2;
            CloudPcValidationResult? lastTransient = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await TryValidateViaGraphAsync(tenantId, normalizedId, sessionId, cacheKey, attempt);

                if (result.IsValid || !result.IsTransient)
                    return result;

                lastTransient = result;
                if (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "Cloud PC validation transient failure for tenant {TenantId}, deviceId {IntuneDeviceId} (attempt {Attempt}/{MaxAttempts}). Retrying...",
                        tenantId, normalizedId, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            _logger.LogWarning(
                "Cloud PC validation failed after {MaxAttempts} attempts for tenant {TenantId}, deviceId {IntuneDeviceId}",
                maxAttempts, tenantId, normalizedId);

            return lastTransient!;
        }

        private async Task<CloudPcValidationResult> TryValidateViaGraphAsync(
            string tenantId, string normalizedId, string? sessionId, string cacheKey, int attempt)
        {
            try
            {
                var tokenResult = await _graphTokenService.GetAccessTokenAsync(tenantId);
                if (string.IsNullOrEmpty(tokenResult.AccessToken))
                {
                    return new CloudPcValidationResult
                    {
                        IsValid = false,
                        IsTransient = true,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = "Graph access token could not be acquired"
                    };
                }

                var graphClient = _httpClientFactory.CreateClient();
                graphClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);

                // eq on managedDeviceId is supported server-side (field-verified 2026-08-06 on a
                // real W365 tenant). normalizedId passed the strict GUID gate above, so the
                // filter literal cannot carry injection payloads.
                var filter = Uri.EscapeDataString($"managedDeviceId eq '{normalizedId}'");
                var graphUrl = $"{Constants.GraphBaseUrl}/v1.0/deviceManagement/virtualEndpoint/cloudPCs"
                               + $"?$select=id,displayName,managedDeviceId,managedDeviceName,servicePlanName&$filter={filter}";

                var response = await graphClient.GetAsync(graphUrl);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Cloud PC validation Graph query failed for tenant {TenantId} (attempt {Attempt}). Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId, attempt, (int)response.StatusCode, responseBody);

                    // 403 = CloudPC.Read.All not granted in this tenant. That is a configuration
                    // state, not an outage: retrying cannot fix it, and a 503 Retry-After would
                    // keep every W365 agent in a futile retry loop. Cache it as a definitive
                    // negative (5 min) so a fresh grant is picked up quickly.
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        return CacheAndReturn(cacheKey, new CloudPcValidationResult
                        {
                            IsValid = false,
                            IntuneDeviceId = normalizedId,
                            ErrorMessage = "Cloud PC lookup not permitted — grant the optional 'W365CloudPcValidation' Graph feature (CloudPC.Read.All)"
                        }, isPositive: false);
                    }

                    return new CloudPcValidationResult
                    {
                        IsValid = false,
                        IsTransient = true,
                        IntuneDeviceId = normalizedId,
                        ErrorMessage = $"Graph query failed with status {(int)response.StatusCode}"
                    };
                }

                var result = ParseCloudPcResponse(responseBody, normalizedId);
                if (result.IsValid)
                {
                    _logger.LogInformation(
                        "Cloud PC validation succeeded for tenant {TenantId}, session {SessionId}, deviceId {IntuneDeviceId}, cloudPcId {CloudPcId}, name {ManagedDeviceName}",
                        tenantId, sessionId ?? "<none>", normalizedId,
                        result.CloudPcId ?? "<none>", result.ManagedDeviceName ?? "<none>");
                }
                return CacheAndReturn(cacheKey, result, isPositive: result.IsValid);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during Cloud PC validation for tenant {TenantId}, session {SessionId}, deviceId {IntuneDeviceId} (attempt {Attempt})",
                    tenantId, sessionId ?? "<none>", normalizedId, attempt);

                return new CloudPcValidationResult
                {
                    IsValid = false,
                    IsTransient = true,
                    IntuneDeviceId = normalizedId,
                    ErrorMessage = $"Error during Cloud PC validation: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Pure-function: maps a <c>virtualEndpoint/cloudPCs</c> Graph response body to a
        /// <see cref="CloudPcValidationResult"/>. Performs an exact-match guard on
        /// <c>managedDeviceId</c> so a widened server-side filter can never produce a
        /// false positive.
        /// </summary>
        internal static CloudPcValidationResult ParseCloudPcResponse(string responseBody, string normalizedId)
        {
            var notFound = new CloudPcValidationResult
            {
                IsValid = false,
                IntuneDeviceId = normalizedId,
                ErrorMessage = $"Device '{normalizedId}' is not a Windows 365 Cloud PC in this tenant"
            };

            JObject? data;
            try
            {
                data = JsonConvert.DeserializeObject<JObject>(responseBody);
            }
            catch (JsonException)
            {
                return notFound;
            }

            var cloudPcs = data?["value"] as JArray;
            if (cloudPcs == null || cloudPcs.Count == 0)
                return notFound;

            var match = cloudPcs.FirstOrDefault(c => string.Equals(
                c?["managedDeviceId"]?.ToString()?.Trim(),
                normalizedId,
                StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return notFound;

            return new CloudPcValidationResult
            {
                IsValid = true,
                IntuneDeviceId = normalizedId,
                CloudPcId = match["id"]?.ToString(),
                ManagedDeviceName = match["managedDeviceName"]?.ToString(),
                ServicePlanName = match["servicePlanName"]?.ToString()
            };
        }

        internal static string BuildCacheKey(string tenantId, string intuneDeviceId)
            => $"cloudpc-device-validation:{tenantId}:{intuneDeviceId}";

        private CloudPcValidationResult CacheAndReturn(
            string cacheKey,
            CloudPcValidationResult result,
            bool isPositive)
        {
            var ttl = isPositive ? PositiveCacheTtl : NegativeCacheTtl;
            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            });
            return result;
        }
    }

    public class CloudPcValidationResult
    {
        public bool IsValid { get; set; }

        /// <summary>
        /// True when the failure is transient (Graph API error, token issue, network timeout).
        /// Transient failures are NOT cached and should trigger a 503 Retry-After to the agent.
        /// </summary>
        public bool IsTransient { get; set; }

        /// <summary>Intune device id extracted from the MDM client certificate's Subject CN.</summary>
        public string? IntuneDeviceId { get; set; }

        /// <summary>Cloud PC object id (virtualEndpoint/cloudPCs) when validation succeeded.</summary>
        public string? CloudPcId { get; set; }

        /// <summary>Intune device name of the Cloud PC (CPC-*) — diagnostic only, never a gate.</summary>
        public string? ManagedDeviceName { get; set; }

        /// <summary>Windows 365 service plan (e.g. "Cloud PC Enterprise 2vCPU/4GB/128GB") — diagnostic only.</summary>
        public string? ServicePlanName { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
