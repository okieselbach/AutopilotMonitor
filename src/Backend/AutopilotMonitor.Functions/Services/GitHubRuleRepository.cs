using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Fetches rule definitions from GitHub (raw.githubusercontent.com).
    /// Uses the CI-generated combined files from rules/dist/.
    /// Retry and circuit breaker resilience is handled by the Polly policy on the injected HttpClient.
    /// </summary>
    public class GitHubRuleRepository
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GitHubRuleRepository> _logger;
        private readonly string _baseUrl;

        public GitHubRuleRepository(HttpClient httpClient, IConfiguration config, ILogger<GitHubRuleRepository> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _baseUrl = config["GitHub:RulesBaseUrl"]
                ?? "https://raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/rules";
        }

        public async Task<List<GatherRule>> FetchGatherRulesAsync()
        {
            var url = $"{_baseUrl}/dist/gather-rules.json";
            _logger.LogInformation("Fetching gather rules from {Url}", url);
            var json = await FetchAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<GatherRule>>(json);
            var rules = wrapper?.Rules ?? new List<GatherRule>();
            _logger.LogInformation("Fetched {Count} gather rules from GitHub", rules.Count);
            return rules;
        }

        public async Task<List<AnalyzeRule>> FetchAnalyzeRulesAsync()
        {
            var url = $"{_baseUrl}/dist/analyze-rules.json";
            _logger.LogInformation("Fetching analyze rules from {Url}", url);
            var json = await FetchAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<AnalyzeRule>>(json);
            var rules = wrapper?.Rules ?? new List<AnalyzeRule>();
            _logger.LogInformation("Fetched {Count} analyze rules from GitHub", rules.Count);
            return rules;
        }

        public async Task<List<ImeLogPattern>> FetchImeLogPatternsAsync()
        {
            var url = $"{_baseUrl}/dist/ime-log-patterns.json";
            _logger.LogInformation("Fetching IME log patterns from {Url}", url);
            var json = await FetchAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<ImeLogPattern>>(json);
            var patterns = wrapper?.Rules ?? new List<ImeLogPattern>();
            _logger.LogInformation("Fetched {Count} IME log patterns from GitHub", patterns.Count);
            return patterns;
        }

        public async Task<string> FetchCpeCommunityMappingsAsync()
        {
            var url = $"{_baseUrl}/dist/cpe-community-mappings.json";
            _logger.LogInformation("Fetching community CPE mappings from {Url}", url);
            var json = await FetchAsync(url);
            _logger.LogInformation("Fetched community CPE mappings JSON from GitHub ({Length} bytes)", json.Length);
            return json;
        }

        public async Task<string> FetchCpeSeedMappingsAsync()
        {
            var url = $"{_baseUrl}/dist/cpe-mappings.json";
            _logger.LogInformation("Fetching CPE seed mappings from {Url}", url);
            var json = await FetchAsync(url);
            _logger.LogInformation("Fetched CPE seed mappings JSON from GitHub ({Length} bytes)", json.Length);
            return json;
        }

        private async Task<string> FetchAsync(string url)
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
    }
}
