using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Exposes the latest published agent/bootstrap versions as read from the
    /// public <c>version.json</c> blob. The TTL is deliberately short: this number is
    /// what the portal announces right after a release ("Latest agent version" in the
    /// What's new panel), so a long cache shows the previous release for hours — and
    /// because the cache is per Function instance, different instances would announce
    /// different versions at the same time. The origin is a 200-byte no-cache blob
    /// behind the download alias, so refetching it every few minutes costs nothing.
    /// On a fetch failure the last known good value is kept rather than blanking the
    /// display. Admin endpoint can force-refresh.
    /// </summary>
    public interface ILatestVersionsService
    {
        Task<LatestVersions?> GetAsync(bool forceRefresh, CancellationToken ct);
    }

    public sealed record LatestVersions(
        string? AgentVersion,
        string? BootstrapVersion,
        string? AgentSha256,
        DateTimeOffset FetchedAtUtc,
        bool FromCache);

    public sealed class LatestVersionsService : ILatestVersionsService
    {
        // Download alias (Front Door → current blob origin) — NOT the legacy blob
        // account. The legacy mirror in build-agent.yml is fail-soft, so reading from
        // it could silently serve a stale version while the release pipeline is green.
        public const string VersionJsonUrl = Constants.AgentDownloadBaseUrl + "/" + Constants.AgentVersionFileName;
        private const string CacheKey = "latest-versions";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(1);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<LatestVersionsService> _logger;

        /// <summary>
        /// Survives cache expiry: served when the blob read fails so a transient hiccup
        /// hides the version line for at most one request instead of a full failure TTL.
        /// </summary>
        private volatile LatestVersions? _lastGood;

        public LatestVersionsService(
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            ILogger<LatestVersionsService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task<LatestVersions?> GetAsync(bool forceRefresh, CancellationToken ct)
        {
            if (!forceRefresh && _cache.TryGetValue(CacheKey, out LatestVersions? cached))
            {
                if (cached != null)
                {
                    return cached with { FromCache = true };
                }
                // Cached "null" (transient failure marker) — return null without another blob call
                return null;
            }

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var response = await client.GetAsync(VersionJsonUrl, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string? bootstrapVersion = root.TryGetProperty("bootstrapVersion", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString() : null;
                string? sha256 = root.TryGetProperty("sha256", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;

                var result = new LatestVersions(
                    AgentVersion: version,
                    BootstrapVersion: bootstrapVersion,
                    AgentSha256: sha256,
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    FromCache: false);

                _cache.Set(CacheKey, result, CacheDuration);
                _lastGood = result;
                _logger.LogInformation("LatestVersions refreshed: agent={AgentVersion}, bootstrap={BootstrapVersion}", version, bootstrapVersion);
                return result;
            }
            catch (Exception ex)
            {
                var lastGood = _lastGood;
                _logger.LogWarning(
                    ex,
                    "Failed to fetch {Url}; serving {Fallback} for {Minutes}m",
                    VersionJsonUrl,
                    lastGood == null ? "nothing" : $"the value fetched at {lastGood.FetchedAtUtc:O}",
                    FailureCacheDuration.TotalMinutes);
                // Cache the outcome briefly to avoid hammering the blob on repeated calls.
                _cache.Set(CacheKey, lastGood, FailureCacheDuration);
                return lastGood == null ? null : lastGood with { FromCache = true };
            }
        }
    }
}
