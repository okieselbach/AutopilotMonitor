using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo
{
    /// <summary>
    /// Evidence collectors for the "no Autopilot profile" edge case: turns the
    /// autopilot_profile_missing guess ("likely causes") into a verdict backed by what the
    /// ZTD client actually logged during OOBE. Three independent sources:
    /// <list type="number">
    ///   <item>The ModernDeployment-Diagnostics-Provider/Autopilot event log — Windows logs a
    ///         precise reason (807 not registered, 809 assigned profile deleted, 815 no profile
    ///         assigned, 164 internet available, 161 download succeeded, ...).</item>
    ///   <item>The HKLM\SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot registry key —
    ///         Microsoft's own diagnostic surface (IsAutopilotDisabled, TenantMatched,
    ///         CloudAssignedTenantDomain/Id).</item>
    ///   <item>A one-shot reachability probe of the Autopilot Deployment Service endpoint
    ///         (https://ztd.dds.microsoft.com) — corroborates/rules out the firewall cause.
    ///         Note the probe runs minutes AFTER the OOBE profile check; event 164 is the
    ///         authoritative witness for reachability at check time.</item>
    /// </list>
    /// Event-ID meanings, registry values, and endpoint list are documented (with sources and
    /// a re-check RSS feed) in docs/agent/autopilot-ztd-diagnostics.md — keep both in sync.
    /// </summary>
    internal static class ZtdEvidence
    {
        /// <summary>Autopilot Deployment Service profile endpoint (Microsoft Learn: autopilot/requirements, Networking tab).</summary>
        internal const string ZtdEndpointUrl = "https://ztd.dds.microsoft.com";

        /// <summary>
        /// ZTD event IDs queried from the ModernDeployment Autopilot channel. Errors 807/809/815/908
        /// carry the definitive failure reason; Info 153/160/161/163/164 trace the download flow
        /// (the continuous ModernDeploymentTracker watcher filters Info out, so a targeted one-shot
        /// query is needed); Warning 100 is the "waiting for profile" heartbeat.
        /// </summary>
        internal static readonly int[] QueriedEventIds = { 100, 153, 160, 161, 163, 164, 171, 172, 807, 809, 815, 908 };

        internal const int DefaultLookbackHours = 48;
        internal const int MaxRecordsRead = 2000;
        internal const int DefaultProbeTimeoutMs = 5000;

        /// <summary>Result of the one-shot ZTD event log query.</summary>
        internal sealed class EventEvidence
        {
            public Dictionary<int, int> EventIdCounts { get; } = new Dictionary<int, int>();
            public bool Truncated { get; set; }
        }

        /// <summary>Result of the endpoint reachability probe.</summary>
        internal sealed class EndpointProbeResult
        {
            public bool Reachable { get; set; }
            public long LatencyMs { get; set; }
            public string Detail { get; set; }
        }

        /// <summary>
        /// Maps the observed ZTD event IDs to a single verdict string, most-specific first.
        /// Error verdicts (807/908/809/815) are authoritative; the Info-flow fallbacks
        /// reconstruct how far the download got. Pure — unit-tested.
        /// </summary>
        internal static string ComputeZtdVerdict(IReadOnlyDictionary<int, int> eventIdCounts)
        {
            if (eventIdCounts == null || eventIdCounts.Count == 0) return "no_ztd_events_found";

            bool Has(int id) => eventIdCounts.TryGetValue(id, out var count) && count > 0;

            if (Has(807)) return "device_not_registered";                        // ZtdDeviceIsNotRegistered
            if (Has(908)) return "serial_or_product_key_mismatch";               // SerialNumberMismatch / ProductKeyIdMismatch
            if (Has(809)) return "assigned_profile_deleted";                     // profile deleted without cleanup
            if (Has(815)) return "no_profile_assigned";                          // no profile assigned + no tenant default
            if (Has(161) || Has(153)) return "profile_downloaded";               // a profile DID arrive at some point
            if (Has(163)) return "already_provisioned";                          // download skipped — device already provisioned
            if (Has(164)) return "download_attempted_no_profile_returned";       // internet confirmed, no profile came back
            if (Has(100)) return "waiting_for_profile_no_internet_confirmation"; // still waiting, 164 never logged
            return "inconclusive";
        }

        /// <summary>
        /// One-shot targeted query of the ModernDeployment Autopilot channel for the ZTD event IDs
        /// (all levels — unlike the continuous watcher, which drops Info). Returns null when the
        /// channel is unavailable (non-Win10/11 test environments, access denied).
        /// </summary>
        internal static EventEvidence QueryEventEvidence(AgentLogger logger, int lookbackHours = DefaultLookbackHours)
        {
            try
            {
                var idClauses = string.Join(" or ", Array.ConvertAll(QueriedEventIds, id => $"EventID={id}"));
                long lookbackMs = (long)lookbackHours * 3600_000L;
                var xpath = $"*[System[({idClauses}) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";

                var query = new EventLogQuery(ModernDeploymentTracker.AutopilotChannel, PathType.LogName, xpath);
                var evidence = new EventEvidence();
                int read = 0;

                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            if (++read > MaxRecordsRead)
                            {
                                evidence.Truncated = true;
                                break;
                            }

                            evidence.EventIdCounts.TryGetValue(record.Id, out var count);
                            evidence.EventIdCounts[record.Id] = count + 1;
                        }
                    }
                }

                logger.Info($"ZtdEvidence: event query found {evidence.EventIdCounts.Count} distinct ID(s) in {read} record(s) (lookback={lookbackHours}h, truncated={evidence.Truncated})");
                return evidence;
            }
            catch (EventLogNotFoundException)
            {
                logger.Warning($"ZtdEvidence: channel not found: {ModernDeploymentTracker.AutopilotChannel} (normal on non-Windows 10/11 test environments)");
                return null;
            }
            catch (Exception ex)
            {
                logger.Warning($"ZtdEvidence: event query failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Microsoft's own Autopilot diagnostic surface (troubleshooting FAQ): documented values
        /// include IsAutopilotDisabled (1 = not registered OR profile download blocked),
        /// TenantMatched (0 = user tenant ≠ registration tenant), CloudAssignedTenantDomain/Id
        /// (blank = not registered), AadTenantId, CloudAssignedOobeConfig.
        /// </summary>
        internal const string DiagnosticsRegistryPath = @"SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot";

        /// <summary>
        /// Dumps all values of the Diagnostics\Autopilot key verbatim (same philosophy as the
        /// AutopilotPolicyCache read: faithful dump, interpretation happens downstream).
        /// Returns null when the key does not exist or cannot be read.
        /// </summary>
        internal static Dictionary<string, object> ReadDiagnosticsRegistry(AgentLogger logger)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(DiagnosticsRegistryPath))
                {
                    if (key == null)
                    {
                        logger.Debug("ZtdEvidence: Diagnostics\\Autopilot registry key not found");
                        return null;
                    }

                    var values = new Dictionary<string, object>();
                    foreach (var valueName in key.GetValueNames())
                    {
                        var value = key.GetValue(valueName);
                        if (value != null)
                            values[valueName] = value.ToString();
                    }

                    logger.Info($"ZtdEvidence: read {values.Count} values from Diagnostics\\Autopilot");
                    return values.Count > 0 ? values : null;
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"ZtdEvidence: failed to read Diagnostics\\Autopilot registry: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// One-shot HTTPS reachability probe. Any HTTP response — including error statuses like
        /// 403/404 — counts as reachable: the question is whether the network path to the
        /// deployment service is open, not whether an anonymous GET is meaningful.
        /// </summary>
        internal static EndpointProbeResult ProbeZtdEndpoint(AgentLogger logger, int timeoutMs = DefaultProbeTimeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(ZtdEndpointUrl);
                request.Method = "GET";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.AllowAutoRedirect = true;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    stopwatch.Stop();
                    return new EndpointProbeResult
                    {
                        Reachable = true,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                        Detail = $"http_{(int)response.StatusCode}"
                    };
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse errorResponse)
            {
                stopwatch.Stop();
                using (errorResponse)
                {
                    return new EndpointProbeResult
                    {
                        Reachable = true,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                        Detail = $"http_{(int)errorResponse.StatusCode}"
                    };
                }
            }
            catch (WebException ex)
            {
                stopwatch.Stop();
                logger.Warning($"ZtdEvidence: endpoint probe failed ({ex.Status}): {ex.Message}");
                return new EndpointProbeResult
                {
                    Reachable = false,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Detail = ex.Status.ToString()
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.Warning($"ZtdEvidence: endpoint probe failed: {ex.Message}");
                return new EndpointProbeResult
                {
                    Reachable = false,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Detail = ex.GetType().Name
                };
            }
        }
    }
}
