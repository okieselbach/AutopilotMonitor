using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>The region pair Microsoft Entra publishes for a tenant, as raw codes.</summary>
    public sealed record EntraTenantRegion(string Scope, string? SubScope)
    {
        /// <summary>
        /// The one mutation every writer applies: false (declined) when the row already holds this pair.
        /// </summary>
        public bool ApplyTo(TenantConfiguration row)
        {
            if (string.Equals(row.TenantRegionScope, Scope, StringComparison.Ordinal)
                && string.Equals(row.TenantRegionSubScope, SubScope, StringComparison.Ordinal))
                return false;
            row.TenantRegionScope = Scope;
            row.TenantRegionSubScope = SubScope;
            return true;
        }
    }

    /// <summary>A lookup's answer: the region, or why there is none.</summary>
    public sealed record EntraTenantRegionLookupResult(EntraTenantRegion? Region, string? Failure)
    {
        public static EntraTenantRegionLookupResult Found(EntraTenantRegion region) => new(region, null);

        public static EntraTenantRegionLookupResult Failed(string reason) => new(null, reason);
    }

    public interface IEntraTenantRegionLookup
    {
        /// <summary>Never throws: a timeout, an error status or an unusable body come back as a failure.</summary>
        Task<EntraTenantRegionLookupResult> LookupAsync(string tenantId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Reads a tenant's region from the sign-in metadata Microsoft Entra publishes without authentication
    /// (<c>tenant_region_scope</c> and <c>tenant_region_sub_scope</c> of the OpenID discovery document).
    /// Microsoft documents no list of values, so only the shape of a code is checked.
    /// </summary>
    public sealed class EntraTenantRegionLookup : IEntraTenantRegionLookup
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public EntraTenantRegionLookup(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        internal static string MetadataUrl(Guid tenantId)
            => $"{Constants.EntraLoginBaseUrl}/{tenantId:D}/v2.0/.well-known/openid-configuration";

        public async Task<EntraTenantRegionLookupResult> LookupAsync(string tenantId, CancellationToken cancellationToken)
        {
            // The id lands in the URL path: anything but a GUID never leaves the process.
            if (!Guid.TryParse(tenantId, out var id))
                return EntraTenantRegionLookupResult.Failed("tenant id is no GUID");

            try
            {
                using var response = await _httpClientFactory.CreateClient()
                    .GetAsync(MetadataUrl(id), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return EntraTenantRegionLookupResult.Failed($"HTTP {(int)response.StatusCode}");

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ReadRegion(body) is { } region
                    ? EntraTenantRegionLookupResult.Found(region)
                    : EntraTenantRegionLookupResult.Failed("the metadata carries no usable tenant_region_scope");
            }
            catch (OperationCanceledException)
            {
                return EntraTenantRegionLookupResult.Failed("timed out");
            }
            catch (JsonException)
            {
                return EntraTenantRegionLookupResult.Failed("the metadata is no readable JSON object");
            }
            catch (Exception ex)
            {
                return EntraTenantRegionLookupResult.Failed(ex.GetType().Name);
            }
        }

        /// <summary>
        /// The region pair of a discovery document; null when it carries no usable scope. A sub-scope of
        /// another shape is dropped. Throws <see cref="JsonException"/> for a body that is no readable JSON
        /// object.
        /// </summary>
        internal static EntraTenantRegion? ReadRegion(string? body)
        {
            if (UpstreamJson.ParseObjectOrNull(body) is not { } root)
                return null;

            var scope = UpstreamJson.GetString(root, "tenant_region_scope");
            if (!IsCode(scope))
                return null;

            var subScope = UpstreamJson.GetString(root, "tenant_region_sub_scope");
            return new EntraTenantRegion(scope!, IsCode(subScope) ? subScope : null);
        }

        // Two to eight ASCII letters (EU, NA, WW, JPR, USGov, GCC, DODCON): the value is rendered and
        // counted, so a code of any other shape is no region.
        private static bool IsCode(string? value)
            => value is { Length: >= 2 and <= 8 } && value.All(char.IsAsciiLetter);
    }
}
