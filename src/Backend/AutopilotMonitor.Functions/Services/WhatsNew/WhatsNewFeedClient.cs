using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.WhatsNew;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.WhatsNew
{
    /// <summary>Reads the live What's new payload the portal serves.</summary>
    public interface IWhatsNewFeedClient
    {
        /// <summary>The parsed payload, or null when it could not be fetched or is off-shape.</summary>
        Task<WhatsNewFeed?> GetAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Fetches <c>{PortalBaseUrl}/whats-new.json</c> — the static file the web deploy generates from
    /// the docs changelogs. Reading the portal's own copy (rather than the docs repo) keeps the
    /// notifier in lockstep with what the panel shows: an entry is announced when it becomes visible
    /// to users, not when it is committed to the docs.
    /// </summary>
    public sealed class WhatsNewFeedClient : IWhatsNewFeedClient
    {
        public const string FeedUrl = Constants.PortalBaseUrl + "/whats-new.json";

        private readonly HttpClient _httpClient;
        private readonly ILogger<WhatsNewFeedClient> _logger;

        public WhatsNewFeedClient(HttpClient httpClient, ILogger<WhatsNewFeedClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<WhatsNewFeed?> GetAsync(CancellationToken ct = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("What's new feed returned HTTP {Status}", (int)response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var feed = WhatsNewFeed.Parse(json);
                if (feed == null)
                    _logger.LogWarning("What's new feed at {Url} has an unexpected shape — skipping", FeedUrl);
                return feed;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogWarning(ex, "Failed to fetch What's new feed from {Url}", FeedUrl);
                return null;
            }
        }
    }
}
