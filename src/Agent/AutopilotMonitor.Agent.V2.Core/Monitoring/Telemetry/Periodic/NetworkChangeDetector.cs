using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Detects network changes (SSID switches, wired↔wireless, IP changes) during provisioning
    /// and runs MDM endpoint connectivity checks after each change.
    /// Always-on collector — fires network_state_change and network_connectivity_check events.
    /// </summary>
    public class NetworkChangeDetector : IDisposable
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly string _apiBaseUrl;

        private NetworkStateSnapshot _lastKnownState;
        private Timer _debounceTimer;
        private HttpClient _connectivityCheckClient;
        private readonly object _stateLock = new object();
        private bool _disposed;

        private const int DebounceDelayMs = 3000;
        private const int ConnectivityCheckDelayMs = 5000;
        private const int ConnectivityCheckTimeoutMs = 8000;
        private const int WifiCollectTimeoutMs = 3000;
        private const string Source = "NetworkChangeDetector";

        /// <summary>
        /// Well-known MDM/Entra endpoints that must be reachable for enrollment to succeed.
        /// </summary>
        private static readonly (string Name, string Url)[] MdmEndpoints =
        {
            ("Entra ID", Constants.EntraLoginBaseUrl),
            ("Device Registration", "https://enterpriseregistration.windows.net"),
            ("Intune Portal", "https://portal.manage.microsoft.com"),
            ("Microsoft Graph", Constants.GraphBaseUrl),
        };

        public NetworkChangeDetector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            string apiBaseUrl = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiBaseUrl = apiBaseUrl;
        }

        public void Start()
        {
            _logger.Info("NetworkChangeDetector: starting (monitoring network changes)");

            _connectivityCheckClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(ConnectivityCheckTimeoutMs)
            };

            // Capture initial network state as baseline
            _lastKnownState = CaptureCurrentState();
            _logger.Info($"NetworkChangeDetector: initial state — {FormatSnapshotSummary(_lastKnownState)}");

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }

        public void Stop()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

            lock (_stateLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }

            _connectivityCheckClient?.Dispose();
            _connectivityCheckClient = null;

            _logger.Info("NetworkChangeDetector: stopped");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // -------------------------------------------------------------------
        // Event handling & debounce
        // -------------------------------------------------------------------

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            if (_disposed) return;

            lock (_stateLock)
            {
                // Reset debounce timer — collapses rapid OS events into one logical change
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(
                    OnDebounceElapsed,
                    null,
                    DebounceDelayMs,
                    Timeout.Infinite);
            }
        }

        private async void OnDebounceElapsed(object state)
        {
            if (_disposed) return;

            // async void: the timer callback returns to the pool the moment ProcessNetworkChangeAsync
            // hits its first await (the stabilization delay), instead of parking a pool thread for the
            // ~5-13s of Task.Delay + connectivity HTTP probes via sync-over-async .Wait().
            try
            {
                await ProcessNetworkChangeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning($"NetworkChangeDetector: error processing network change: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // Core logic
        // -------------------------------------------------------------------

        private async Task ProcessNetworkChangeAsync()
        {
            if (_disposed) return;

            var newState = CaptureCurrentState();

            NetworkStateSnapshot previousState;
            lock (_stateLock)
            {
                previousState = _lastKnownState;
                _lastKnownState = newState;
            }

            // Suppress events when network hasn't actually changed (DHCP renewals, OS chatter)
            if (previousState != null && previousState.IsSameNetwork(newState))
            {
                _logger.Debug("NetworkChangeDetector: network address changed but same logical network — suppressing");
                return;
            }

            _logger.Info($"NetworkChangeDetector: network change detected — {FormatSnapshotSummary(previousState)} → {FormatSnapshotSummary(newState)}");

            // Emit network_state_change event
            var changeType = ClassifyChange(previousState, newState);
            var message = BuildChangeMessage(previousState, newState);

            var data = new Dictionary<string, object>
            {
                // Before state
                { "before_connectionType", previousState?.ConnectionType ?? "None" },
                { "before_adapterName", previousState?.AdapterName ?? "None" },
                { "before_ipAddress", previousState?.IpAddress ?? "None" },
                { "before_gateway", previousState?.Gateway ?? "None" },
                { "before_wifiSsid", previousState?.WifiSsid ?? "n/a" },
                { "before_linkSpeedMbps", previousState?.LinkSpeedMbps ?? 0 },

                // After state
                { "after_connectionType", newState.ConnectionType ?? "None" },
                { "after_adapterName", newState.AdapterName ?? "None" },
                { "after_ipAddress", newState.IpAddress ?? "None" },
                { "after_gateway", newState.Gateway ?? "None" },
                { "after_wifiSsid", newState.WifiSsid ?? "n/a" },
                { "after_linkSpeedMbps", newState.LinkSpeedMbps },

                // Change classification
                { "changeType", changeType },
                { "hadNetwork", previousState?.HasNetwork ?? false },
                { "hasNetwork", newState.HasNetwork }
            };

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.NetworkStateChange,
                Severity = EventSeverity.Warning,
                Source = Source,
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data
            });

            // Wait for new connection to stabilize, then check MDM endpoint connectivity
            try
            {
                await Task.Delay(ConnectivityCheckDelayMs).ConfigureAwait(false);
                if (!_disposed)
                {
                    await RunConnectivityChecksAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"NetworkChangeDetector: connectivity check failed: {ex.Message}");
            }
        }

        private async Task RunConnectivityChecksAsync()
        {
            if (_disposed || _connectivityCheckClient == null) return;

            // Build endpoint list: static MDM endpoints + dynamic backend URL
            var endpoints = new List<(string Name, string Url)>(MdmEndpoints);
            // Optionally add the Autopilot Monitor API endpoint, but we don't want to cause false alarms if it's down or unreachable, 
            // since it's not critical for enrollment success and may be available only after certain network changes (e.g. if it's behind a captive portal). 
            // For now, we can omit it from the critical checks.
            // if (!string.IsNullOrEmpty(_apiBaseUrl))
            // {
            //     endpoints.Add(("Autopilot Monitor API", _apiBaseUrl));
            // }

            // Run all checks in parallel
            var tasks = endpoints.Select(ep => CheckEndpointAsync(ep.Name, ep.Url)).ToArray();

            Dictionary<string, object>[] results;
            try
            {
                results = await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.Debug($"NetworkChangeDetector: connectivity check tasks failed: {ex.Message}");
                return;
            }

            if (_disposed) return;

            var reachableCount = results.Count(r => r.ContainsKey("reachable") && (bool)r["reachable"]);
            var totalCount = results.Length;
            var allReachable = reachableCount == totalCount;

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.NetworkConnectivityCheck,
                Severity = allReachable ? EventSeverity.Info : EventSeverity.Warning,
                Source = Source,
                Phase = EnrollmentPhase.Unknown,
                Message = $"Connectivity check after network change: {reachableCount}/{totalCount} endpoints reachable",
                Data = new Dictionary<string, object>
                {
                    { "reachableCount", reachableCount },
                    { "totalCount", totalCount },
                    { "allReachable", allReachable },
                    { "checkDelayMs", ConnectivityCheckDelayMs },
                    { "timeoutMs", ConnectivityCheckTimeoutMs },
                    { "results", results.ToList() }
                }
            });

            if (allReachable)
                _logger.Info($"NetworkChangeDetector: connectivity check passed ({reachableCount}/{totalCount} reachable)");
            else
                _logger.Warning($"NetworkChangeDetector: connectivity check partial — {reachableCount}/{totalCount} endpoints reachable");
        }

        private async Task<Dictionary<string, object>> CheckEndpointAsync(string name, string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Use GET — any HTTP response (even 401/403) means the network path works
                var response = await _connectivityCheckClient.GetAsync(url);
                sw.Stop();
                return new Dictionary<string, object>
                {
                    { "endpoint", name },
                    { "url", url },
                    { "reachable", true },
                    { "httpStatus", (int)response.StatusCode },
                    { "latencyMs", sw.ElapsedMilliseconds },
                    { "error", (object)null }
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Unwrap AggregateException from async
                var innerEx = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
                return new Dictionary<string, object>
                {
                    { "endpoint", name },
                    { "url", url },
                    { "reachable", false },
                    { "httpStatus", (object)null },
                    { "latencyMs", sw.ElapsedMilliseconds },
                    { "error", $"{innerEx.GetType().Name}: {innerEx.Message}" }
                };
            }
        }

        // -------------------------------------------------------------------
        // State capture
        // -------------------------------------------------------------------

        private NetworkStateSnapshot CaptureCurrentState()
        {
            try
            {
                var activeNic = FindActiveNetworkInterface();
                if (activeNic == null)
                {
                    return new NetworkStateSnapshot { HasNetwork = false, CapturedAt = DateTime.UtcNow };
                }

                var isWifi = activeNic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                var ipProps = activeNic.GetIPProperties();

                // First IPv4 unicast address
                string ipAddress = null;
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ipAddress = addr.Address.ToString();
                        break;
                    }
                }

                // First non-0.0.0.0 gateway
                string gateway = null;
                foreach (var gw in ipProps.GatewayAddresses)
                {
                    if (gw.Address.ToString() != "0.0.0.0")
                    {
                        gateway = gw.Address.ToString();
                        break;
                    }
                }

                var snapshot = new NetworkStateSnapshot
                {
                    HasNetwork = true,
                    AdapterName = activeNic.Name,
                    AdapterDescription = activeNic.Description,
                    InterfaceType = activeNic.NetworkInterfaceType.ToString(),
                    ConnectionType = isWifi ? "WiFi" : "Ethernet",
                    MacAddress = activeNic.GetPhysicalAddress().ToString(),
                    InterfaceId = activeNic.Id,
                    LinkSpeedMbps = activeNic.Speed / 1_000_000,
                    IpAddress = ipAddress,
                    Gateway = gateway,
                    CapturedAt = DateTime.UtcNow
                };

                // Collect WiFi SSID synchronously (needed for before/after comparison)
                if (isWifi)
                {
                    var wifiInfo = CollectWiFiInfo();
                    snapshot.WifiSsid = wifiInfo.ssid;
                    snapshot.WifiSignalPercent = wifiInfo.signal;
                    snapshot.WifiRadioType = wifiInfo.radioType;
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.Debug($"NetworkChangeDetector: state capture failed: {ex.Message}");
                return new NetworkStateSnapshot { HasNetwork = false, CapturedAt = DateTime.UtcNow };
            }
        }

        /// <summary>
        /// Collects WiFi SSID, signal, and radio type via netsh wlan show interfaces.
        /// Synchronous with timeout — needed before emitting the event.
        /// </summary>
        private (string ssid, int? signal, string radioType) CollectWiFiInfo()
        {
            string ssid = null;
            int? signal = null;
            string radioType = null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SystemPaths.Netsh,
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                string output;
                using (var process = Process.Start(psi))
                {
                    output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(WifiCollectTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        _logger.Debug("NetworkChangeDetector: netsh timed out");
                        return (null, null, null);
                    }
                }

                if (string.IsNullOrEmpty(output))
                    return (null, null, null);

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    var colonIndex = trimmed.IndexOf(':');
                    if (colonIndex < 0) continue;

                    var key = trimmed.Substring(0, colonIndex).Trim();
                    var value = trimmed.Substring(colonIndex + 1).Trim();

                    if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase) &&
                        !key.Equals("BSSID", StringComparison.OrdinalIgnoreCase))
                    {
                        ssid = value;
                    }
                    else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value.TrimEnd('%'), out var sig))
                            signal = sig;
                    }
                    else if (key.Equals("Radio type", StringComparison.OrdinalIgnoreCase))
                    {
                        radioType = value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"NetworkChangeDetector: WiFi info collection failed: {ex.Message}");
            }

            return (ssid, signal, radioType);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Finds the active network interface: Up, not Loopback/Tunnel, has a non-0.0.0.0 gateway.
        /// </summary>
        private static NetworkInterface FindActiveNetworkInterface()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in interfaces)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var ipProps = nic.GetIPProperties();
                    foreach (var gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address.ToString() != "0.0.0.0")
                            return nic;
                    }
                }
            }
            catch
            {
                // Caller handles null
            }
            return null;
        }

        private static string BuildChangeMessage(NetworkStateSnapshot before, NetworkStateSnapshot after)
        {
            var beforeDesc = FormatSnapshotBrief(before);
            var afterDesc = FormatSnapshotBrief(after);

            if (before == null || !before.HasNetwork)
            {
                if (after.HasNetwork)
                    return $"Network connected: {afterDesc}";
                return "Network state changed (no connectivity)";
            }

            if (!after.HasNetwork)
                return $"Network lost (was {beforeDesc})";

            return $"Network changed: {beforeDesc} \u2192 {afterDesc}";
        }

        private static string FormatSnapshotBrief(NetworkStateSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.HasNetwork)
                return "None";

            if (snapshot.ConnectionType == "WiFi" && !string.IsNullOrEmpty(snapshot.WifiSsid))
                return $"WiFi '{snapshot.WifiSsid}'";

            return $"{snapshot.ConnectionType} ({snapshot.IpAddress ?? "no IP"})";
        }

        private static string FormatSnapshotSummary(NetworkStateSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.HasNetwork)
                return "no network";

            var summary = $"{snapshot.ConnectionType}";
            if (!string.IsNullOrEmpty(snapshot.WifiSsid))
                summary += $" SSID='{snapshot.WifiSsid}'";
            if (!string.IsNullOrEmpty(snapshot.IpAddress))
                summary += $" IP={snapshot.IpAddress}";
            return summary;
        }

        private static string ClassifyChange(NetworkStateSnapshot before, NetworkStateSnapshot after)
        {
            if (before == null || !before.HasNetwork)
                return after.HasNetwork ? "network_restored" : "no_change";

            if (!after.HasNetwork)
                return "network_lost";

            // Both have network — classify the change
            if (before.ConnectionType != after.ConnectionType)
                return "type_change";

            if (before.ConnectionType == "WiFi" && after.ConnectionType == "WiFi" &&
                before.WifiSsid != after.WifiSsid)
                return "ssid_change";

            if (before.InterfaceId != after.InterfaceId)
                return "adapter_change";

            return "ip_change";
        }

        // -------------------------------------------------------------------
        // Inner type: network state snapshot
        // -------------------------------------------------------------------

        private class NetworkStateSnapshot
        {
            public bool HasNetwork { get; set; }
            public string AdapterName { get; set; }
            public string AdapterDescription { get; set; }
            public string InterfaceType { get; set; }
            public string ConnectionType { get; set; }
            public string MacAddress { get; set; }
            public string InterfaceId { get; set; }
            public long LinkSpeedMbps { get; set; }
            public string IpAddress { get; set; }
            public string Gateway { get; set; }
            public string WifiSsid { get; set; }
            public int? WifiSignalPercent { get; set; }
            public string WifiRadioType { get; set; }
            public DateTime CapturedAt { get; set; }

            /// <summary>
            /// Returns true if both snapshots represent the same logical network connection.
            /// Suppresses events for DHCP renewals and other OS chatter.
            /// </summary>
            public bool IsSameNetwork(NetworkStateSnapshot other)
            {
                if (other == null) return false;
                if (HasNetwork != other.HasNetwork) return false;
                if (!HasNetwork && !other.HasNetwork) return true;

                // Same adapter + same IP + same SSID = same logical network
                return InterfaceId == other.InterfaceId &&
                       IpAddress == other.IpAddress &&
                       WifiSsid == other.WifiSsid;
            }
        }
    }
}
