#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Transport
{
    /// <summary>
    /// Makes the agent log say which way uploads go and what happened to it. Something on the device
    /// can change the proxy settings of the agent's account; .NET follows at once, and from then on
    /// no channel may reach the backend, so the log on the device is the only witness.
    /// <para>
    /// Compares with what held at the start instead of judging a value: "no proxy" is the right
    /// state on one network and the broken one on the next. Reads on failures and on registry
    /// changes, never by polling. Nothing here throws into the upload path.
    /// </para>
    /// </summary>
    public sealed class ProxyObserver
    {
        internal const string RouteUnknown = "unknown";

        private static readonly TimeSpan DefaultRouteBound = TimeSpan.FromSeconds(3);

        private readonly Func<ProxySettings> _readSettings;
        private readonly Func<string> _readRoute;
        private readonly IClock _clock;
        private readonly AgentLogger _logger;
        private readonly TimeSpan _routeBound;
        private readonly object _sync = new object();

        private ProxySettings? _current;
        private string _lastRoute = RouteUnknown;
        private Task<string>? _routeRead;
        private DateTime? _uploadChangeAtUtc;
        private string? _uploadChange;
        private DateTime? _failingSinceUtc;
        private int _failedAttempts;
        private DateTime? _lastSuccessUtc;

        /// <summary>Settings now, what changed, route now. Raised outside the lock, on the caller's thread.</summary>
        internal event Action<ProxySettings, string, string>? SettingsChanged;

        public ProxyObserver(string baseUrl, AgentLogger logger, IClock clock)
            : this(
                ProxySettingsReader.Read,
                RouteReaderFor(baseUrl),
                logger,
                clock,
                DefaultRouteBound)
        {
        }

        internal ProxyObserver(
            Func<ProxySettings> readSettings,
            Func<string> readRoute,
            AgentLogger logger,
            IClock clock,
            TimeSpan routeBound)
        {
            _readSettings = readSettings ?? throw new ArgumentNullException(nameof(readSettings));
            _readRoute = readRoute ?? throw new ArgumentNullException(nameof(readRoute));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _routeBound = routeBound;
        }

        private static Func<string> RouteReaderFor(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var target)) return () => RouteUnknown;
            return () => ProxySettingsReader.ReadRoute(target);
        }

        /// <summary>Logs the state to compare against. Runs before the first request of the agent.</summary>
        public void CaptureBaseline()
        {
            var settings = ReadSettingsSafe();
            var route = ReadRouteBounded();
            lock (_sync)
            {
                if (_current == null && !settings.IsUnreadable) _current = settings;
                _lastRoute = route;
            }
            // Same text on a rebuild of the clients after an endpoint migration: the repeat goes to Trace.
            _logger.Info($"ProxyObserver: start - {settings.DescribeAll()}; route to backend: {route}", repeatKey: "ProxyObserver.baseline");
        }

        /// <summary>Re-reads the settings and reports a change of content once, whoever notices it first.</summary>
        public void CheckForChange()
        {
            var settings = ReadSettingsSafe();
            if (settings.IsUnreadable) return;

            ProxySettings previous;
            var affectsUploads = false;
            lock (_sync)
            {
                if (_current == null)
                {
                    _current = settings;
                    return;
                }
                if (_current.Equals(settings)) return;

                previous = _current;
                _current = settings;
                if (settings.DiffersForUploadsFrom(previous))
                {
                    affectsUploads = true;
                    _uploadChangeAtUtc = _clock.UtcNow;
                    _uploadChange = settings.DescribeUploadChangeFrom(previous);
                }
            }

            var route = ReadRouteBounded();
            string routeBefore;
            lock (_sync)
            {
                routeBefore = _lastRoute;
                _lastRoute = route;
            }

            var what = settings.DescribeChangeFrom(previous);
            _logger.Warning(affectsUploads
                ? $"ProxyObserver: proxy settings changed - {what}; route to backend: {(routeBefore == route ? route : $"{routeBefore} -> {route}")}. " +
                  "The agent's uploads follow these settings. Check with: bitsadmin /util /getieproxy localsystem"
                : $"ProxyObserver: proxy settings changed - {what}. The agent's uploads do not use WinHTTP, services that rely on its default do.");

            try
            {
                SettingsChanged?.Invoke(settings, what, route);
            }
            catch (Exception ex)
            {
                _logger.Debug($"ProxyObserver: change handler threw: {ex.Message}");
            }
        }

        /// <summary>The next hop to the backend: <c>direct</c>, <c>host:port</c> or <c>unknown</c>.</summary>
        public string RouteText() => ReadRouteBounded();

        /// <summary>
        /// An upload did not reach the backend. The first of a series says what the reader needs to
        /// know: when it began and whether the proxy settings moved before that.
        /// </summary>
        public void OnSendFailed(TimeSpan attemptDuration)
        {
            // Also catches a change the registry watcher did not report.
            CheckForChange();

            DateTime since;
            DateTime? lastSuccess;
            DateTime? changedAt;
            string? change;
            string? current;
            lock (_sync)
            {
                _failedAttempts++;
                if (_failingSinceUtc != null) return;

                since = _clock.UtcNow - attemptDuration;
                _failingSinceUtc = since;
                lastSuccess = _lastSuccessUtc;
                changedAt = _uploadChangeAtUtc;
                change = _uploadChange;
                current = _current?.Describe();
            }

            var route = ReadRouteBounded();
            var lastUpload = lastSuccess == null ? "No successful upload yet." : $"Last successful upload {Time(lastSuccess.Value)}.";

            if (changedAt != null && (lastSuccess == null || changedAt > lastSuccess))
            {
                _logger.Warning(
                    $"ProxyObserver: uploads are failing since {Time(since)}, {Span(since - changedAt.Value)} after the proxy settings changed at {Time(changedAt.Value)} ({change}). " +
                    $"Route to backend: {route}. {lastUpload}");
            }
            else
            {
                var unchangedSince = changedAt == null ? "start" : "the last successful upload";
                _logger.Warning(
                    $"ProxyObserver: uploads are failing since {Time(since)}. Proxy settings of this account unchanged since {unchangedSince}: {current ?? RouteUnknown}. " +
                    $"Route to backend: {route}. {lastUpload}");
            }
        }

        public void OnSendSucceeded()
        {
            DateTime since;
            int attempts;
            lock (_sync)
            {
                _lastSuccessUtc = _clock.UtcNow;
                if (_failingSinceUtc == null) return;

                since = _failingSinceUtc.Value;
                attempts = _failedAttempts;
                _failingSinceUtc = null;
                _failedAttempts = 0;
            }

            CheckForChange();
            _logger.Info($"ProxyObserver: uploads work again after {attempts} failed attempt(s) over {Span(_clock.UtcNow - since)}; route to backend: {ReadRouteBounded()}.");
        }

        /// <summary>The end of the log is read first, so a run that ends while failing says so there.</summary>
        public void LogOpenFailures()
        {
            DateTime since;
            int attempts;
            DateTime? lastSuccess;
            DateTime? changedAt;
            string? change;
            string? current;
            lock (_sync)
            {
                if (_failingSinceUtc == null) return;

                since = _failingSinceUtc.Value;
                attempts = _failedAttempts;
                lastSuccess = _lastSuccessUtc;
                changedAt = _uploadChangeAtUtc;
                change = _uploadChange;
                current = _current?.Describe();
            }

            var lastUpload = lastSuccess == null ? "no successful upload" : $"last success {Time(lastSuccess.Value)}";
            var settings = changedAt == null
                ? $"Proxy settings of this account unchanged since start: {current ?? RouteUnknown}."
                : $"Proxy settings changed at {Time(changedAt.Value)} ({change}).";
            _logger.Warning(
                $"ProxyObserver: stopping with uploads still failing: {attempts} failed attempt(s) since {Time(since)}, {lastUpload}, route to backend: {ReadRouteBounded()}. {settings}");
        }

        private ProxySettings ReadSettingsSafe()
        {
            try
            {
                var settings = _readSettings();
                if (settings.IsUnreadable) _logger.Debug($"ProxyObserver: proxy settings {settings.Describe()}", repeatKey: "ProxyObserver.unreadable");
                return settings;
            }
            catch (Exception ex)
            {
                _logger.Debug($"ProxyObserver: reading the proxy settings threw: {ex.Message}", repeatKey: "ProxyObserver.readThrew");
                return ProxySettings.Unreadable(-1);
            }
        }

        /// <summary>
        /// Auto-detect can hang and must hold up neither the start nor an upload. A read still under
        /// way is joined, so a hanging one does not pile up pool threads.
        /// </summary>
        private string ReadRouteBounded()
        {
            Task<string> read;
            lock (_sync)
            {
                if (_routeRead == null || _routeRead.IsCompleted) _routeRead = Task.Run(ReadRouteSafe);
                read = _routeRead;
            }
            return read.Wait(_routeBound) ? read.Result : RouteUnknown;
        }

        private string ReadRouteSafe()
        {
            try
            {
                return _readRoute();
            }
            catch (Exception ex)
            {
                _logger.Debug($"ProxyObserver: reading the route threw: {ex.Message}", repeatKey: "ProxyObserver.routeThrew");
                return RouteUnknown;
            }
        }

        private static string Time(DateTime utc) => utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        private static string Span(TimeSpan span)
        {
            var seconds = Math.Max(0, (long)span.TotalSeconds);
            return seconds < 120 ? $"{seconds} s" : $"{seconds / 60} min";
        }
    }
}
