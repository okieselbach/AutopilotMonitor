using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Fetches and caches remote configuration from the backend API
    /// Provides collector toggles and gather rules to the agent
    /// </summary>
    public class RemoteConfigService : IDisposable
    {
        private readonly BackendApiClient _apiClient;
        private readonly string _tenantId;
        private readonly AgentLogger _logger;
        private readonly string _cacheFilePath;
        private readonly EmergencyReporter _emergencyReporter;
        private readonly DistressReporter _distressReporter;
        private readonly AuthFailureTracker _authFailureTracker;

        private AgentConfigResponse _currentConfig;
        private readonly object _configLock = new object();

        /// <summary>
        /// Linear-backoff schedule (seconds) for the initial <see cref="FetchConfigAsync"/> call
        /// when retries are enabled. Sized for the worst real-world cold-start pattern: deploy
        /// triggers ~30-60s Function App spin-up, the first agent fetch hits it, times out, then
        /// the second attempt 10s later usually succeeds. The 60s third attempt covers double
        /// cold-starts (Function App + downstream Table/Blob warm-up). Exposed internal so the
        /// xUnit suite can assert the cadence without hard-coding a copy of the array.
        /// </summary>
        internal static readonly TimeSpan[] InitialFetchRetryBackoff =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
        };

        /// <summary>
        /// Gets the current configuration (thread-safe)
        /// </summary>
        public AgentConfigResponse CurrentConfig
        {
            get
            {
                lock (_configLock)
                {
                    return _currentConfig;
                }
            }
        }

        /// <summary>
        /// Outcome of the most recent <see cref="FetchConfigAsync"/> call. Observability
        /// surface so the agent-startup pipeline can emit a wire-visible event when the
        /// fetch fell back — closes the historical blind spot where a cold-start-induced
        /// timeout silently degraded the agent to built-in defaults with no backend trace.
        /// </summary>
        public RemoteConfigFetchOutcome LastFetchOutcome { get; private set; } = RemoteConfigFetchOutcome.NotAttempted;

        /// <summary>Number of HTTP attempts the last fetch consumed (1 on first-shot success, up to <see cref="InitialFetchRetryBackoff"/>.Length+1 with retries).</summary>
        public int LastFetchAttempts { get; private set; }

        /// <summary>Type name of the last terminal exception (e.g. <c>TaskCanceledException</c>, <c>BackendAuthException</c>); null when the fetch succeeded.</summary>
        public string LastFetchFailureType { get; private set; }

        /// <summary>One-line human-readable failure description (Exception.Message, truncated to 512 chars).</summary>
        public string LastFetchFailureMessage { get; private set; }

        /// <summary>HTTP status for auth failures (401/403), null otherwise.</summary>
        public int? LastFetchAuthStatusCode { get; private set; }

        public RemoteConfigService(BackendApiClient apiClient, string tenantId, AgentLogger logger, EmergencyReporter emergencyReporter = null, DistressReporter distressReporter = null, AuthFailureTracker authFailureTracker = null)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _emergencyReporter = emergencyReporter;
            _distressReporter = distressReporter;
            _authFailureTracker = authFailureTracker;

            var cacheDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Config");
            _cacheFilePath = Path.Combine(cacheDir, "remote-config.json");
        }

        /// <summary>
        /// Fetches the initial configuration from the backend. On transient failures
        /// (network/timeout/5xx — anything that isn't <see cref="BackendAuthException"/>)
        /// retries per <see cref="InitialFetchRetryBackoff"/> when <paramref name="retryOnTransientErrors"/>
        /// is true. Falls back to cached config, then to defaults. Auth failures are
        /// NEVER retried (a 401/403 won't change without an operator action).
        /// <para>
        /// Records the terminal outcome in <see cref="LastFetchOutcome"/>/<see cref="LastFetchFailureType"/>/
        /// <see cref="LastFetchFailureMessage"/>/<see cref="LastFetchAttempts"/> so the startup pipeline can
        /// emit a wire-visible <c>remote_config_fetch_failed</c> event when the fallback path
        /// is taken — closes the blind spot where a cold-start-induced timeout silently
        /// degraded the agent to defaults with no backend trace.
        /// </para>
        /// </summary>
        /// <param name="retryOnTransientErrors">
        /// True for the initial agent-startup fetch (cold-start tolerance is essential).
        /// False for the live ServerAction <c>rotate_config</c> path (a single-attempt
        /// best-effort suffices; retries would block the action handler for ~100 s).
        /// </param>
        public async Task<AgentConfigResponse> FetchConfigAsync(bool retryOnTransientErrors = false)
        {
            LastFetchOutcome = RemoteConfigFetchOutcome.NotAttempted;
            LastFetchAttempts = 0;
            LastFetchFailureType = null;
            LastFetchFailureMessage = null;
            LastFetchAuthStatusCode = null;

            var maxAttempts = retryOnTransientErrors ? InitialFetchRetryBackoff.Length + 1 : 1;
            Exception lastException = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                LastFetchAttempts = attempt;
                try
                {
                    if (attempt == 1)
                        _logger.Info("Fetching remote configuration from backend...");
                    else
                        _logger.Info($"Fetching remote configuration from backend (attempt {attempt}/{maxAttempts})...");

                    var config = await CallBackendAsync();

                    if (config != null)
                    {
                        _logger.Info($"Remote config fetched: ConfigVersion={config.ConfigVersion}, " +
                                     $"GatherRules={config.GatherRules?.Count ?? 0}");

                        LogCollectorSettings(config.Collectors);
                        SetConfig(config);
                        CacheConfig(config);
                        _authFailureTracker?.RecordSuccess();
                        LastFetchOutcome = RemoteConfigFetchOutcome.Succeeded;
                        return config;
                    }

                    _logger.Warning("Remote config fetch returned empty response");
                    lastException = new InvalidOperationException("Remote config fetch returned null");
                    // Empty response is treated as a transient error — fall through to retry.
                }
                catch (BackendAuthException ex)
                {
                    // Auth failures are NOT retryable — 401/403 won't go away with another
                    // attempt; record + bail straight to cache/defaults so an operator can
                    // see the AuthCertificateRejected/DeviceNotRegistered distress signal
                    // without 100 s of needless retry delay.
                    _logger.Warning($"Config fetch authentication failed: {ex.Message}");
                    _authFailureTracker?.RecordFailure(ex.StatusCode, "agent/config");
                    lastException = ex;
                    LastFetchAuthStatusCode = ex.StatusCode;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to fetch remote config (attempt {attempt}/{maxAttempts}): {ex.Message}");
                    lastException = ex;

                    // Non-auth failure on FINAL attempt → use authenticated emergency channel.
                    // Earlier attempts are still in the retry budget; don't spam.
                    if (attempt == maxAttempts && _emergencyReporter != null)
                    {
                        _ = _emergencyReporter.TrySendAsync(
                            AgentErrorType.ConfigFetchFailed,
                            ex.Message);
                    }
                }

                // Sleep before the next retry — only if there IS a next attempt.
                if (attempt < maxAttempts)
                {
                    var delay = InitialFetchRetryBackoff[attempt - 1];
                    _logger.Info($"Retrying remote config fetch in {delay.TotalSeconds:0}s...");
                    try { await DelayBetweenAttemptsAsync(delay); }
                    catch (TaskCanceledException) { break; }
                }
            }

            // Capture failure context for the observability event.
            if (lastException != null)
            {
                LastFetchFailureType = lastException.GetType().Name;
                var msg = lastException.Message ?? string.Empty;
                LastFetchFailureMessage = msg.Length > 512 ? msg.Substring(0, 512) : msg;
            }

            // Fall back to cached config
            var cached = LoadCachedConfig();
            if (cached != null)
            {
                _logger.Info("Using cached remote configuration");
                SetConfig(cached);
                LastFetchOutcome = RemoteConfigFetchOutcome.FromCache;
                return cached;
            }

            // Fall back to defaults
            _logger.Info("No cached config available, using defaults (all optional collectors disabled)");
            var defaults = CreateDefaultConfig();
            SetConfig(defaults);
            LastFetchOutcome = RemoteConfigFetchOutcome.UsedDefaults;
            return defaults;
        }

        private void SetConfig(AgentConfigResponse config)
        {
            lock (_configLock)
            {
                _currentConfig = config;
            }
        }

        private void LogCollectorSettings(CollectorConfiguration collectors)
        {
            if (collectors == null) return;

            _logger.Info("  Collector settings:");
            _logger.Info($"    Performance: {(collectors.EnablePerformanceCollector ? "ON" : "OFF")} (interval: {collectors.PerformanceIntervalSeconds}s)");
        }

        // Marked protected virtual so xUnit can swap to in-memory caching and avoid
        // %ProgramData%\AutopilotMonitor\Config\remote-config.json bleeding state
        // between parallel tests (a successful test would otherwise plant a cache that
        // makes subsequent "fail + UsedDefaults" tests resolve to FromCache instead).
        protected virtual void CacheConfig(AgentConfigResponse config)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // SECURITY: Never persist security-sensitive fields to disk — they must come
                // from a live backend fetch each cold boot. A one-time MITM during OOBE could
                // otherwise plant attacker-controlled values that survive forever.
                //   - UnrestrictedMode: gate for agent privileged paths
                //   - AllowAgentDowngrade: gate for installing a lower agent version
                //   - LatestAgentExeSha256: backend-advertised EXE hash for runtime integrity
                //     check; a bad cached value would trigger a force-update to attacker bins
                //   - DeviceBlocked/DeviceKillSignal/UnblockAt: kill is only honoured from a
                //     live fetch — a planted cached kill would self-destruct every future
                //     session, a stale cached kill would outlive the admin removing the rule
                var liveUnrestricted = config.UnrestrictedMode;
                var liveAllowDowngrade = config.AllowAgentDowngrade;
                var liveExeHash = config.LatestAgentExeSha256;
                var liveBlocked = config.DeviceBlocked;
                var liveKill = config.DeviceKillSignal;
                var liveUnblockAt = config.UnblockAt;
                config.UnrestrictedMode = false;
                config.AllowAgentDowngrade = false;
                config.LatestAgentExeSha256 = null;
                config.DeviceBlocked = false;
                config.DeviceKillSignal = false;
                config.UnblockAt = null;
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                config.UnrestrictedMode = liveUnrestricted;
                config.AllowAgentDowngrade = liveAllowDowngrade;
                config.LatestAgentExeSha256 = liveExeHash;
                config.DeviceBlocked = liveBlocked;
                config.DeviceKillSignal = liveKill;
                config.UnblockAt = liveUnblockAt;

                File.WriteAllText(_cacheFilePath, json);
                _logger.Debug("Remote config cached to disk");
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to cache remote config: {ex.Message}");
            }
        }

        // protected virtual — see CacheConfig for rationale.
        protected virtual AgentConfigResponse LoadCachedConfig()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    var config = JsonConvert.DeserializeObject<AgentConfigResponse>(json);
                    if (config != null)
                    {
                        // SECURITY: Never trust cached security-sensitive fields — always require
                        // a live backend fetch. Defence-in-depth on top of the write-side strip
                        // in CacheConfig so older cache files (or attacker-planted files written
                        // out-of-band) cannot leak attacker-controlled values into runtime.
                        config.UnrestrictedMode = false;
                        config.AllowAgentDowngrade = false;
                        config.LatestAgentExeSha256 = null;
                        config.DeviceBlocked = false;
                        config.DeviceKillSignal = false;
                        config.UnblockAt = null;
                    }
                    return config;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to load cached config: {ex.Message}");
            }
            return null;
        }

        private AgentConfigResponse CreateDefaultConfig()
        {
            return new AgentConfigResponse
            {
                ConfigVersion = 0,
                UploadIntervalSeconds = 30,
                SelfDestructOnComplete = true,
                KeepLogFile = false,
                EnableGeoLocation = true,
                EnableImeMatchLog = false,
                Collectors = CollectorConfiguration.CreateDefault(),
                GatherRules = new System.Collections.Generic.List<GatherRule>(),
                ImeLogPatterns = new System.Collections.Generic.List<ImeLogPattern>()
            };
        }

        // ============================================================ Test seams ====
        // Two minimal hooks the xUnit suite overrides so it can pin the retry contract
        // (success-on-N-th-attempt + auth-fail-bails-immediately + outcome state) without
        // standing up a real HttpClient OR waiting the 10/30/60 s of the live backoff.

        /// <summary>
        /// Performs the actual <c>GetAgentConfig</c> HTTP call. Production hits the
        /// injected <see cref="BackendApiClient"/>; tests subclass to return canned
        /// responses / throw canned exceptions and assert the retry+outcome machinery
        /// around it.
        /// </summary>
        protected virtual Task<AgentConfigResponse> CallBackendAsync()
            => _apiClient.GetAgentConfigAsync(_tenantId);

        /// <summary>
        /// Awaits the per-attempt backoff. Production sleeps for the supplied
        /// <see cref="InitialFetchRetryBackoff"/> entry; tests subclass to return
        /// immediately so a 3-attempt retry suite finishes in milliseconds instead of
        /// 100 seconds.
        /// </summary>
        protected virtual Task DelayBetweenAttemptsAsync(TimeSpan delay)
            => Task.Delay(delay);

        public void Dispose()
        {
            // Nothing to dispose
        }
    }

    /// <summary>
    /// Terminal classification of the most recent <see cref="RemoteConfigService.FetchConfigAsync"/>
    /// call. Surfaces what config the agent ended up running with — successful live fetch,
    /// stale cache fall-back, or built-in defaults — so the startup pipeline can emit a
    /// wire-visible event when the fallback path was taken.
    /// </summary>
    public enum RemoteConfigFetchOutcome
    {
        /// <summary>FetchConfigAsync has not been called yet on this instance.</summary>
        NotAttempted = 0,

        /// <summary>Live backend fetch returned a non-null config; agent runs with fresh tenant settings.</summary>
        Succeeded = 1,

        /// <summary>Live fetch failed; previously-cached config was loaded from disk and applied.</summary>
        FromCache = 2,

        /// <summary>Live fetch failed AND no cache available; agent runs with built-in defaults (ConfigVersion=0).</summary>
        UsedDefaults = 3,
    }
}
