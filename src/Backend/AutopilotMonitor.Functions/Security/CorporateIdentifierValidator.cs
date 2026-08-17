using AutopilotMonitor.Shared;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates devices against Intune Corporate Device Identifiers via Microsoft Graph beta API.
    /// Reads /deviceManagement/importedDeviceIdentities (GET, DeviceManagementServiceConfig.Read.All
    /// suffices) instead of the searchExistingIdentities action, which would require the heavy
    /// ReadWrite.All scope the app deliberately does not hold (field case 2026-08-17: the action
    /// 403'd in every tenant). Only the manufacturerModelSerial type counts — the sole
    /// corporate-identifier type Windows supports; serial-only entries are an Intune-side admin
    /// misconfiguration and must not authorize. Matching happens in Intune's NORMALIZED space
    /// (uppercase, non-alphanumerics stripped per component — see <see cref="NormalizeComponent"/>),
    /// because that is the form the portal stores and matches at enrollment. A Graph 401/403
    /// means missing admin consent and is classified as a definitive failure, not transient.
    /// Caches positive/negative lookups to reduce Graph traffic.
    /// </summary>
    public class CorporateIdentifierValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger<CorporateIdentifierValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;

        public CorporateIdentifierValidator(
            ILogger<CorporateIdentifierValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
        }

        public async Task<CorporateIdentifierValidationResult> ValidateAsync(
            string tenantId,
            string? manufacturer,
            string? model,
            string? serialNumber,
            string? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serialNumber))
            {
                return new CorporateIdentifierValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Manufacturer, model, or serial number header not provided"
                };
            }

            var normalizedManufacturer = manufacturer.Trim();
            var normalizedModel = model.Trim();
            var normalizedSerial = serialNumber.Trim();
            var cacheKey = BuildCacheKey(tenantId, normalizedManufacturer, normalizedModel, normalizedSerial);

            if (_cache.TryGetValue(cacheKey, out CorporateIdentifierValidationResult? cached) && cached != null)
            {
                return cached;
            }

            // Retry once on transient failures (token acquisition, Graph API errors)
            const int maxAttempts = 2;
            CorporateIdentifierValidationResult? lastTransientResult = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await TryValidateViaGraphAsync(tenantId, normalizedManufacturer, normalizedModel, normalizedSerial, sessionId, cacheKey, attempt);

                if (result.IsValid || !result.IsTransient)
                {
                    return result;
                }

                lastTransientResult = result;
                if (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "Corporate identifier validation transient failure for tenant {TenantId}, serial {SerialNumber} (attempt {Attempt}/{MaxAttempts}). Retrying...",
                        tenantId, normalizedSerial, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            _logger.LogWarning(
                "Corporate identifier validation failed after {MaxAttempts} attempts for tenant {TenantId}, serial {SerialNumber}",
                maxAttempts, tenantId, normalizedSerial);

            return lastTransientResult!;
        }

        private async Task<CorporateIdentifierValidationResult> TryValidateViaGraphAsync(
            string tenantId, string normalizedManufacturer, string normalizedModel, string normalizedSerial,
            string? sessionId, string cacheKey, int attempt)
        {
            try
            {
                var tokenResult = await _graphTokenService.GetAccessTokenAsync(tenantId);
                if (string.IsNullOrEmpty(tokenResult.AccessToken))
                {
                    return new CorporateIdentifierValidationResult
                    {
                        IsValid = false,
                        IsTransient = true,
                        ErrorMessage = "Graph access token could not be acquired"
                    };
                }

                var graphClient = _httpClientFactory.CreateClient();
                graphClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);

                // GET /beta/deviceManagement/importedDeviceIdentities — deliberately NOT the
                // searchExistingIdentities action: the action requires the heavy
                // DeviceManagementServiceConfig.ReadWrite.All scope, while listing works with
                // Read.All, which the app already holds for windowsAutopilotDeviceIdentities.
                // Mirrors AutopilotDeviceValidator: contains() for server-side narrowing (eq is
                // unreliable on Intune endpoints), exact match client-side.
                //
                // Intune NORMALIZES corporate identifiers on upload: uppercased, every
                // non-alphanumeric character stripped per component — "Microsoft Corporation,
                // Virtual Machine,7801-5131-…" is stored as "MICROSOFTCORPORATION,
                // VIRTUALMACHINE,78015131…" (field-verified 2026-08-17: the portal list shows
                // the normalized form and the device matched + enrolled against it). All
                // matching therefore happens in normalized space — including the contains()
                // narrowing, which with the raw dashed serial would never hit the stored value.
                var rawIdentifier = $"{normalizedManufacturer},{normalizedModel},{normalizedSerial}";
                var normalizedIdentifier = BuildNormalizedIdentifier(normalizedManufacturer, normalizedModel, normalizedSerial);
                // Normalized serial is alphanumeric-only, so no OData quote-escaping is needed.
                var filter = Uri.EscapeDataString($"contains(importedDeviceIdentifier,'{NormalizeComponent(normalizedSerial)}')");
                var filteredUrl = $"{Constants.GraphBaseUrl}/beta/deviceManagement/importedDeviceIdentities?$top=100&$filter={filter}";

                var scan = await ScanPagesForIdentifierAsync(graphClient, filteredUrl, normalizedIdentifier, tenantId, attempt, maxPages: 5);

                if (scan.Outcome == ScanOutcome.FilterRejected)
                {
                    // Fallback: the endpoint rejected the filter (400) — page the unfiltered list
                    // and match client-side. 60 pages x 1000 covers the portal maximum of
                    // 10 CSV files x 5000 identifiers.
                    _logger.LogWarning(
                        "Corporate identifier validation: contains() filter rejected by Graph for tenant {TenantId}; falling back to unfiltered scan.",
                        tenantId);
                    var unfilteredUrl = $"{Constants.GraphBaseUrl}/beta/deviceManagement/importedDeviceIdentities?$top=1000";
                    scan = await ScanPagesForIdentifierAsync(graphClient, unfilteredUrl, normalizedIdentifier, tenantId, attempt, maxPages: 60);
                }

                switch (scan.Outcome)
                {
                    case ScanOutcome.Found:
                        _logger.LogInformation(
                            "Corporate identifier validation succeeded for tenant {TenantId}, session {SessionId}, identifier {Identifier} (normalized {NormalizedIdentifier})",
                            tenantId, sessionId ?? "<none>", rawIdentifier, normalizedIdentifier);
                        return CacheAndReturn(cacheKey, new CorporateIdentifierValidationResult
                        {
                            IsValid = true,
                            Identifier = rawIdentifier
                        }, isPositive: true);

                    case ScanOutcome.NotFound:
                        // Definitive: device not found — cache negative result
                        return CacheAndReturn(cacheKey, new CorporateIdentifierValidationResult
                        {
                            IsValid = false,
                            ErrorMessage = $"Device '{rawIdentifier}' (normalized '{normalizedIdentifier}') is not registered as a Corporate Identifier (manufacturerModelSerial)"
                        }, isPositive: false);

                    case ScanOutcome.PermissionDenied:
                        // 401/403 = missing application permission / admin consent. Retries can
                        // never heal that, so it is a DEFINITIVE failure — a transient
                        // classification would trap every agent in an endless 503 Retry-After
                        // loop (field case 2026-08-17). Cached like a negative lookup (5 min) so
                        // a fleet of agents does not hammer Graph while consent is being fixed.
                        return CacheAndReturn(cacheKey, new CorporateIdentifierValidationResult
                        {
                            IsValid = false,
                            ErrorMessage = "Corporate identifier lookup not permitted: the app registration lacks the "
                                + "DeviceManagementServiceConfig.Read.All (or ReadWrite.All) Graph permission or admin "
                                + "consent. Grant consent or disable ValidateCorporateIdentifier."
                        }, isPositive: false);

                    default:
                        // Transient (throttling, 5xx, page-cap exceeded) — do NOT cache
                        return new CorporateIdentifierValidationResult
                        {
                            IsValid = false,
                            IsTransient = true,
                            ErrorMessage = scan.Error ?? "Graph query failed"
                        };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during corporate identifier validation for tenant {TenantId}, session {SessionId}, identifier {Manufacturer},{Model},{SerialNumber} (attempt {Attempt})",
                    tenantId,
                    sessionId ?? "<none>",
                    normalizedManufacturer,
                    normalizedModel,
                    normalizedSerial,
                    attempt);

                // Exceptions are transient — do NOT cache
                return new CorporateIdentifierValidationResult
                {
                    IsValid = false,
                    IsTransient = true,
                    ErrorMessage = $"Error during corporate identifier validation: {ex.Message}"
                };
            }
        }

        private enum ScanOutcome { Found, NotFound, PermissionDenied, FilterRejected, Transient }

        private readonly record struct ScanResult(ScanOutcome Outcome, string? Error = null);

        /// <summary>
        /// Pages through an importedDeviceIdentities GET url (following @odata.nextLink) and
        /// looks for a manufacturerModelSerial identity whose importedDeviceIdentifier equals
        /// <paramref name="normalizedIdentifier"/> after normalizing the stored value the same
        /// way (both sides pass through <see cref="NormalizeStoredIdentifier"/>, so raw legacy
        /// entries match too). Serial-only identities never match: they are an Intune-side
        /// misconfiguration for Windows, not authorization.
        /// </summary>
        private async Task<ScanResult> ScanPagesForIdentifierAsync(
            HttpClient graphClient, string url, string normalizedIdentifier, string tenantId, int attempt, int maxPages)
        {
            for (var page = 0; page < maxPages && !string.IsNullOrEmpty(url); page++)
            {
                var response = await graphClient.GetAsync(url);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Corporate identifier validation Graph query failed for tenant {TenantId} (attempt {Attempt}, page {Page}). Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId, attempt, page + 1, (int)response.StatusCode, responseBody);

                    return response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                            => new ScanResult(ScanOutcome.PermissionDenied),
                        // 400 on the first page = the $filter was rejected; mid-pagination a 400
                        // is unexpected and treated as transient like any other Graph error.
                        System.Net.HttpStatusCode.BadRequest when page == 0
                            => new ScanResult(ScanOutcome.FilterRejected),
                        _ => new ScanResult(ScanOutcome.Transient, $"Graph query failed with status {(int)response.StatusCode}"),
                    };
                }

                var data = JsonConvert.DeserializeObject<JObject>(responseBody);
                var identities = data?["value"] as JArray;
                if (identities != null)
                {
                    foreach (var identity in identities)
                    {
                        var type = identity?["importedDeviceIdentityType"]?.ToString();
                        var value = identity?["importedDeviceIdentifier"]?.ToString();
                        if (string.Equals(type, "manufacturerModelSerial", StringComparison.OrdinalIgnoreCase)
                            && value != null
                            && string.Equals(NormalizeStoredIdentifier(value), normalizedIdentifier, StringComparison.Ordinal))
                        {
                            return new ScanResult(ScanOutcome.Found);
                        }
                    }
                }

                url = data?["@odata.nextLink"]?.ToString() ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(url))
            {
                // Page cap exceeded with more data pending — treating this as "not found" could
                // cache a false negative, so it is transient instead.
                _logger.LogWarning(
                    "Corporate identifier validation exceeded {MaxPages} pages for tenant {TenantId} without exhausting the list.",
                    maxPages, tenantId);
                return new ScanResult(ScanOutcome.Transient, "Corporate identifier list scan exceeded the page budget");
            }

            return new ScanResult(ScanOutcome.NotFound);
        }

        /// <summary>
        /// Normalizes one identifier component the way the Intune portal does on upload:
        /// uppercase, every non-alphanumeric character removed ("Microsoft Corporation" →
        /// "MICROSOFTCORPORATION", "7801-5131-…-18" → "78015131…18"). Field-verified 2026-08-17
        /// against the portal's stored corporate identifier list.
        /// </summary>
        internal static string NormalizeComponent(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        internal static string BuildNormalizedIdentifier(string manufacturer, string model, string serial) =>
            $"{NormalizeComponent(manufacturer)},{NormalizeComponent(model)},{NormalizeComponent(serial)}";

        /// <summary>
        /// Normalizes a stored importedDeviceIdentifier for comparison: same per-character rule
        /// as <see cref="NormalizeComponent"/> but keeps the comma separators. Stored values are
        /// already normalized by the portal; running them through again is a no-op there and
        /// makes raw-form entries (e.g. created via Graph directly) match as well.
        /// </summary>
        internal static string NormalizeStoredIdentifier(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c == ',')
                    sb.Append(c);
                else if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        private static string BuildCacheKey(string tenantId, string manufacturer, string model, string serialNumber)
        {
            return $"corporate-id-validation:{tenantId}:{manufacturer}:{model}:{serialNumber}";
        }

        private CorporateIdentifierValidationResult CacheAndReturn(
            string cacheKey,
            CorporateIdentifierValidationResult result,
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

    public class CorporateIdentifierValidationResult
    {
        public bool IsValid { get; set; }

        /// <summary>
        /// True when the failure is transient (Graph API error, token issue, network timeout).
        /// Transient failures are NOT cached and should trigger a 503 Retry-After to the agent.
        /// </summary>
        public bool IsTransient { get; set; }

        public string? Identifier { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
