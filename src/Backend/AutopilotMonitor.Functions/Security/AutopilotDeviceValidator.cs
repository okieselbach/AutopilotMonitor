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
    /// Validates devices against Intune Autopilot device registration via Microsoft Graph.
    /// Caches positive/negative lookups to reduce Graph traffic.
    /// </summary>
    public class AutopilotDeviceValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger<AutopilotDeviceValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;

        public AutopilotDeviceValidator(
            ILogger<AutopilotDeviceValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
        }

        public async Task<AutopilotDeviceValidationResult> ValidateAutopilotDeviceAsync(
            string tenantId,
            string? serialNumber,
            string? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                return new AutopilotDeviceValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Serial number header not provided"
                };
            }

            var normalizedSerial = serialNumber.Trim();
            var cacheKey = BuildCacheKey(tenantId, normalizedSerial);

            if (_cache.TryGetValue(cacheKey, out AutopilotDeviceValidationResult? cached) && cached != null)
            {
                return cached;
            }

            // Retry once on transient failures (token acquisition, Graph API errors)
            const int maxAttempts = 2;
            AutopilotDeviceValidationResult? lastTransientResult = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await TryValidateViaGraphAsync(tenantId, normalizedSerial, sessionId, cacheKey, attempt);

                if (result.IsValid || !result.IsTransient)
                {
                    // Definitive result (success or "device not found") — return immediately
                    return result;
                }

                // Transient failure — retry after short delay
                lastTransientResult = result;
                if (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "Autopilot device validation transient failure for tenant {TenantId}, serial {SerialNumber} (attempt {Attempt}/{MaxAttempts}). Retrying...",
                        tenantId, normalizedSerial, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            // All retries exhausted — return transient failure (NOT cached, so next request retries)
            _logger.LogWarning(
                "Autopilot device validation failed after {MaxAttempts} attempts for tenant {TenantId}, serial {SerialNumber}",
                maxAttempts, tenantId, normalizedSerial);

            return lastTransientResult!;
        }

        /// <summary>
        /// Single attempt to validate a device via Graph API.
        /// Returns IsTransient=true for failures that should be retried (Graph errors, token issues).
        /// Returns IsTransient=false for definitive results (device found or not found).
        /// </summary>
        private async Task<AutopilotDeviceValidationResult> TryValidateViaGraphAsync(
            string tenantId, string normalizedSerial, string? sessionId, string cacheKey, int attempt)
        {
            try
            {
                var tokenResult = await _graphTokenService.GetAccessTokenAsync(tenantId);
                if (string.IsNullOrEmpty(tokenResult.AccessToken))
                {
                    return new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        IsTransient = true,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = "Graph access token could not be acquired"
                    };
                }

                var graphClient = _httpClientFactory.CreateClient();
                graphClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);

                // For windowsAutopilotDeviceIdentities, eq on serialNumber is unreliable and often returns 400.
                // Use contains for server-side narrowing, then perform exact match client-side.
                var escapedSerial = normalizedSerial.Replace("'", "''");
                var filter = Uri.EscapeDataString($"contains(serialNumber,'{escapedSerial}')");
                var graphUrl = $"{Constants.GraphBaseUrl}/v1.0/deviceManagement/windowsAutopilotDeviceIdentities?$top=100&$filter={filter}";

                var response = await graphClient.GetAsync(graphUrl);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Autopilot device validation Graph query failed for tenant {TenantId} (attempt {Attempt}). Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId, attempt, (int)response.StatusCode, responseBody);

                    // Graph errors are transient — do NOT cache
                    return new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        IsTransient = true,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Graph query failed with status {(int)response.StatusCode}"
                    };
                }

                var data = JsonConvert.DeserializeObject<JObject>(responseBody);
                var devices = data?["value"] as JArray;
                if (devices == null || devices.Count == 0)
                {
                    // Definitive: device not found — cache negative result
                    return CacheAndReturn(cacheKey, new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Device with serial '{normalizedSerial}' is not registered in Autopilot"
                    }, isPositive: false);
                }

                // Exact-match guard to avoid false positives from contains(...)
                var exactDevice = devices
                    .FirstOrDefault(d => string.Equals(
                        d?["serialNumber"]?.ToString()?.Trim(),
                        normalizedSerial,
                        StringComparison.OrdinalIgnoreCase));

                if (exactDevice == null)
                {
                    // Definitive: device not found — cache negative result
                    return CacheAndReturn(cacheKey, new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Device with serial '{normalizedSerial}' is not registered in Autopilot"
                    }, isPositive: false);
                }

                var result = new AutopilotDeviceValidationResult
                {
                    IsValid = true,
                    SerialNumber = normalizedSerial,
                    AutopilotDeviceId = exactDevice["id"]?.ToString()
                };

                _logger.LogInformation(
                    "Autopilot device validation succeeded for tenant {TenantId}, session {SessionId}, serial {SerialNumber}, autopilotId {AutopilotDeviceId}",
                    tenantId,
                    sessionId ?? "<none>",
                    normalizedSerial,
                    result.AutopilotDeviceId ?? "<none>");

                return CacheAndReturn(cacheKey, result, isPositive: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during Autopilot device validation for tenant {TenantId}, session {SessionId}, serial {SerialNumber} (attempt {Attempt})",
                    tenantId,
                    sessionId ?? "<none>",
                    normalizedSerial,
                    attempt);

                // Exceptions are transient — do NOT cache
                return new AutopilotDeviceValidationResult
                {
                    IsValid = false,
                    IsTransient = true,
                    SerialNumber = normalizedSerial,
                    ErrorMessage = $"Error during Autopilot device validation: {ex.Message}"
                };
            }
        }

        private static string BuildCacheKey(string tenantId, string serialNumber)
        {
            return $"autopilot-device-validation:{tenantId}:{serialNumber}";
        }

        private AutopilotDeviceValidationResult CacheAndReturn(
            string cacheKey,
            AutopilotDeviceValidationResult result,
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

    public class AutopilotDeviceValidationResult
    {
        public bool IsValid { get; set; }

        /// <summary>
        /// True when the failure is transient (Graph API error, token issue, network timeout).
        /// Transient failures are NOT cached and should trigger a 503 Retry-After to the agent.
        /// </summary>
        public bool IsTransient { get; set; }

        public string? SerialNumber { get; set; }
        public string? AutopilotDeviceId { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
