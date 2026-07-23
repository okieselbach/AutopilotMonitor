using AutopilotMonitor.Shared;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates devices against the Windows Autopilot Device Preparation (WDP)
    /// "Device association" Graph API (<c>tenantAssociatedDevices</c>).
    ///
    /// Built as a near-sibling of <see cref="AutopilotDeviceValidator"/> — same DI dependencies,
    /// same retry/cache/transient contract — so the resilience guarantees are identical:
    ///   - 30 min positive cache, 5 min negative cache (transient failures NOT cached)
    ///   - 2 attempts with 2s back-off between attempts
    ///   - <see cref="DeviceAssociationResult.IsTransient"/> on Graph 5xx, token failures, exceptions
    ///   - Serial-trim normalisation, single-quote escape for OData filter, exact-match guard
    /// </summary>
    public class DeviceAssociationValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger<DeviceAssociationValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;

        public DeviceAssociationValidator(
            ILogger<DeviceAssociationValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
        }

        public async Task<DeviceAssociationResult> LookupAsync(
            string tenantId,
            string? serialNumber,
            string? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                return new DeviceAssociationResult
                {
                    IsValid = false,
                    ErrorMessage = "Serial number header not provided"
                };
            }

            var normalizedSerial = serialNumber.Trim();
            var cacheKey = BuildCacheKey(tenantId, normalizedSerial);

            if (_cache.TryGetValue(cacheKey, out DeviceAssociationResult? cached) && cached != null)
            {
                return cached;
            }

            const int maxAttempts = 2;
            DeviceAssociationResult? lastTransient = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await TryLookupViaGraphAsync(tenantId, normalizedSerial, sessionId, cacheKey, attempt);

                if (result.IsValid || !result.IsTransient)
                    return result;

                lastTransient = result;
                if (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "Device association lookup transient failure for tenant {TenantId}, serial {SerialNumber} (attempt {Attempt}/{MaxAttempts}). Retrying...",
                        tenantId, normalizedSerial, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            _logger.LogWarning(
                "Device association lookup failed after {MaxAttempts} attempts for tenant {TenantId}, serial {SerialNumber}",
                maxAttempts, tenantId, normalizedSerial);

            return lastTransient!;
        }

        private async Task<DeviceAssociationResult> TryLookupViaGraphAsync(
            string tenantId, string normalizedSerial, string? sessionId, string cacheKey, int attempt)
        {
            try
            {
                var tokenResult = await _graphTokenService.GetAccessTokenAsync(tenantId);
                if (string.IsNullOrEmpty(tokenResult.AccessToken))
                {
                    return new DeviceAssociationResult
                    {
                        IsValid = false,
                        IsTransient = true,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = "Graph access token could not be acquired"
                    };
                }

                var graphClient = _httpClientFactory.CreateClient();
                graphClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);

                // Mirror the Intune portal's tenantAssociatedDevices search: server-side narrowing via
                // `contains(serialNumber,'…')` (eq is not supported on this endpoint), then enforce
                // exact match in ParseTenantAssociatedDevicesResponse to reject substring false-positives.
                var escapedSerial = normalizedSerial.Replace("'", "''");
                var filter = Uri.EscapeDataString($"contains(serialNumber,'{escapedSerial}')");
                var orderby = Uri.EscapeDataString("preAssociationDateTime desc");
                var graphUrl = Constants.GraphBaseUrl + "/beta/deviceManagement/tenantAssociatedDevices"
                               + $"?$top=25&$filter={filter}&$orderby={orderby}";

                var response = await graphClient.GetAsync(graphUrl);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Device association Graph query failed for tenant {TenantId} (attempt {Attempt}). Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId, attempt, (int)response.StatusCode, responseBody);

                    return new DeviceAssociationResult
                    {
                        IsValid = false,
                        IsTransient = true,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Graph query failed with status {(int)response.StatusCode}"
                    };
                }

                var result = ParseTenantAssociatedDevicesResponse(responseBody, normalizedSerial);
                if (result.IsValid)
                {
                    _logger.LogInformation(
                        "Device association lookup succeeded for tenant {TenantId}, session {SessionId}, serial {SerialNumber}, state {State}, policy {PolicyId}",
                        tenantId, sessionId ?? "<none>", normalizedSerial,
                        result.AssociationState ?? "<none>", result.DevicePreparationPolicyId ?? "<none>");
                }
                return CacheAndReturn(cacheKey, result, isPositive: result.IsValid);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during device association lookup for tenant {TenantId}, session {SessionId}, serial {SerialNumber} (attempt {Attempt})",
                    tenantId, sessionId ?? "<none>", normalizedSerial, attempt);

                return new DeviceAssociationResult
                {
                    IsValid = false,
                    IsTransient = true,
                    SerialNumber = normalizedSerial,
                    ErrorMessage = $"Error during device association lookup: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Pure-function: maps a tenantAssociatedDevices Graph response body to a
        /// <see cref="DeviceAssociationResult"/>. Performs an exact-match guard on
        /// <c>serialNumber</c> to avoid false positives if the Graph endpoint widens
        /// its filter semantics later.
        /// </summary>
        internal static DeviceAssociationResult ParseTenantAssociatedDevicesResponse(string responseBody, string normalizedSerial)
        {
            var notFound = new DeviceAssociationResult
            {
                IsValid = false,
                SerialNumber = normalizedSerial,
                ErrorMessage = $"Device with serial '{normalizedSerial}' is not associated for DevPrep"
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

            var devices = data?["value"] as JArray;
            if (devices == null || devices.Count == 0)
                return notFound;

            var match = devices.FirstOrDefault(d => string.Equals(
                d?["serialNumber"]?.ToString()?.Trim(),
                normalizedSerial,
                StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return notFound;

            return new DeviceAssociationResult
            {
                IsValid = true,
                SerialNumber = normalizedSerial,
                AssociationState = match["associationState"]?.ToString(),
                DevicePreparationPolicyId = match["devicePreparationPolicyId"]?.ToString(),
                PreAssociatedByUserPrincipalName = match["preassociatedByUserPrincipalName"]?.ToString(),
                AssignedToUserPrincipalName = match["assignedToUserPrincipalName"]?.ToString(),
                PreAssociationDateTime = ParseDateTime(match["preassociationDateTime"]),
                AssociationDateTime = ParseDateTime(match["associationDateTime"]),
                ManagedDeviceId = match["managedDeviceId"]?.ToString()
            };
        }

        private static DateTime? ParseDateTime(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var s = token.ToString();
            if (string.IsNullOrEmpty(s)) return null;
            // Graph returns "0001-01-01T00:00:00Z" for unset DateTimeOffset fields — treat as null.
            if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                if (dt.Year <= 1) return null;
                return dt;
            }
            return null;
        }

        internal static string BuildCacheKey(string tenantId, string serialNumber)
            => $"device-association:{tenantId}:{serialNumber}";

        private DeviceAssociationResult CacheAndReturn(
            string cacheKey,
            DeviceAssociationResult result,
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
}
