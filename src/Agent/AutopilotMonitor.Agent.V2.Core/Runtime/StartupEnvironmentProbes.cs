#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Runtime
{
    /// <summary>
    /// Fire-and-forget startup probes ported from the Legacy <c>MonitoringService</c>.
    /// Plan §4.x M4.6.γ.
    /// <para>
    /// Emits three diagnostic events when the orchestrator is live:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>device_location</b> (or <c>agent_trace</c> on failure) — IP-based geo via
    ///     <see cref="GeoLocationService"/>. Skipped when <c>EnableGeoLocation=false</c>.</item>
    ///   <item><b>timezone_auto_set</b> — only when <c>EnableTimezoneAutoSet=true</c> AND the
    ///     geo lookup returned an IANA timezone. Uses <see cref="TimezoneService"/> (tzutil).</item>
    ///   <item><b>ntp_time_check</b> — NTP offset from <c>NtpServer</c> (default time.windows.com).
    ///     Always emitted — offset &gt;60s is Warning, smaller is Info, failure is Warning.</item>
    ///   <item><b>power_state_check</b> — AC vs. battery snapshot via Win32 <c>GetSystemPowerStatus</c>.
    ///     On battery with &lt;80% charge is Warning, otherwise Info, probe failure is Warning.</item>
    /// </list>
    /// <para>
    /// Each probe runs best-effort: failures are logged + optionally emitted as a Warning event,
    /// never thrown. Designed to run on a background task so they never block the critical path.
    /// </para>
    /// </summary>
    public static class StartupEnvironmentProbes
    {
        /// <summary>
        /// Runs all three probes sequentially and emits the resulting events via
        /// <paramref name="post"/>. Returns a <see cref="Task"/> that completes when all
        /// probes have finished (typical runtime &lt;10s; longer on slow/blocked networks).
        /// <para>
        /// Restart dedup (<paramref name="gate"/>, optional): geo / timezone / NTP follow
        /// retry-until-success semantics across agent restarts — a probe that already succeeded in
        /// a previous run of this enrollment is skipped entirely (no network call, no duplicate
        /// event), while a failed one retries on the next start (a later boot may have working
        /// network/DNS). The power-state check is deliberately NOT gated: battery drain across the
        /// enrollment is real per-boot information.
        /// </para>
        /// </summary>
        public static async Task RunAsync(
            AgentConfiguration configuration,
            AgentLogger logger,
            Orchestration.InformationalEventPost post,
            Persistence.StartupEventGate? gate = null)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (post == null) throw new ArgumentNullException(nameof(post));

            if (!configuration.EnableGeoLocation)
            {
                logger.Debug("StartupEnvironmentProbes: EnableGeoLocation=false — skipping geo + timezone probes.");
            }
            else if (GeoAndTimezoneAlreadySatisfied(configuration, gate))
            {
                logger.Debug("StartupEnvironmentProbes: geo + timezone already succeeded in a previous run — skipping (restart dedup).");
            }
            else
            {
                await RunGeoAndTimezone(configuration, logger, post, gate).ConfigureAwait(false);
            }

            if (gate != null && gate.AlreadySucceeded(Constants.EventTypes.NtpTimeCheck))
            {
                logger.Debug("StartupEnvironmentProbes: NTP check already succeeded in a previous run — skipping (restart dedup).");
            }
            else
            {
                await RunNtpCheck(configuration, logger, post, gate).ConfigureAwait(false);
            }

            RunPowerStateCheck(configuration, logger, post);
        }

        /// <summary>
        /// The geo block is satisfied only when the location succeeded AND the timezone auto-set is
        /// either disabled or has also succeeded — a pending timezone retry still needs a fresh geo
        /// lookup (the IANA timezone comes from the location result and is not persisted).
        /// </summary>
        private static bool GeoAndTimezoneAlreadySatisfied(AgentConfiguration configuration, Persistence.StartupEventGate? gate)
        {
            if (gate == null) return false;
            return gate.AlreadySucceeded(Constants.EventTypes.DeviceLocation)
                && (!configuration.EnableTimezoneAutoSet || gate.AlreadySucceeded(Constants.EventTypes.TimezoneAutoSet));
        }

        private static async Task RunGeoAndTimezone(
            AgentConfiguration configuration,
            AgentLogger logger,
            Orchestration.InformationalEventPost post,
            Persistence.StartupEventGate? gate)
        {
            GeoLocationAttemptResult? attempt = null;
            try
            {
                attempt = await GeoLocationService.GetLocationAsync(logger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warning($"StartupEnvironmentProbes: geo-location lookup threw: {ex.Message}");
            }

            if (attempt?.Location != null)
            {
                // When the location event already went out in a previous run, this lookup only ran
                // to feed a pending timezone retry — don't emit a duplicate device_location.
                if (gate == null || !gate.AlreadySucceeded(Constants.EventTypes.DeviceLocation))
                {
                    SafeEmit(post, logger, BuildGeoEvent(configuration, attempt.Location));
                    gate?.MarkSucceeded(Constants.EventTypes.DeviceLocation);
                }
                else
                {
                    logger.Debug("StartupEnvironmentProbes: location re-resolved for the pending timezone retry — device_location already emitted in a previous run.");
                }

                // Outbound (public egress) IP — Trace severity so it stays out of the timeline by
                // default; persisted/queryable for network correlation. Own latch (L17): the first
                // successful provider response can lack the ip field, so it must not ride the
                // device_location latch — any later lookup (e.g. a timezone retry) that does carry
                // it still emits the trace once.
                if (!string.IsNullOrEmpty(attempt.Location.Ip)
                    && (gate == null || !gate.AlreadySucceeded(Constants.EventTypes.OutboundIp)))
                {
                    SafeEmit(post, logger, BuildOutboundIpEvent(configuration, attempt.Location));
                    gate?.MarkSucceeded(Constants.EventTypes.OutboundIp);
                }
            }
            else
            {
                // Failure event each failing start is intentional (retry trail) — but only while
                // the location itself is still outstanding. When a prior run already emitted
                // device_location and this lookup ran solely to feed the pending timezone retry,
                // a failure event here would put a device_location failure AFTER its success on
                // the session timeline (L17) — misleading noise, suppressed.
                if (gate == null || !gate.AlreadySucceeded(Constants.EventTypes.DeviceLocation))
                    SafeEmit(post, logger, BuildGeoFailureEvent(configuration, attempt));
                else
                    logger.Debug("StartupEnvironmentProbes: geo re-lookup for the pending timezone retry failed — failure event suppressed (location already reported).");
            }

            if (configuration.EnableTimezoneAutoSet && !string.IsNullOrEmpty(attempt?.Location?.Timezone))
            {
                if (gate != null && gate.AlreadySucceeded(Constants.EventTypes.TimezoneAutoSet))
                {
                    logger.Debug("StartupEnvironmentProbes: timezone already set successfully in a previous run — skipping tzutil (restart dedup).");
                }
                else
                {
                    try
                    {
                        var tzResult = TimezoneService.TrySetTimezone(attempt!.Location!.Timezone, logger);
                        SafeEmit(post, logger, BuildTimezoneEvent(configuration, tzResult));
                        if (tzResult.Success)
                            gate?.MarkSucceeded(Constants.EventTypes.TimezoneAutoSet);
                    }
                    catch (Exception tzEx)
                    {
                        logger.Warning($"StartupEnvironmentProbes: timezone auto-set threw: {tzEx.Message}");
                    }
                }
            }
        }

        private static async Task RunNtpCheck(
            AgentConfiguration configuration,
            AgentLogger logger,
            Orchestration.InformationalEventPost post,
            Persistence.StartupEventGate? gate)
        {
            try
            {
                var ntpServer = string.IsNullOrEmpty(configuration.NtpServer) ? "time.windows.com" : configuration.NtpServer;

                // NtpTimeCheckService.CheckTime is synchronous — wrap on a background thread so the
                // probe runner stays async-friendly (avoids blocking the continuation thread when
                // Program.cs awaits RunAsync with ConfigureAwait(false)).
                var result = await Task.Run(() => NtpTimeCheckService.CheckTime(ntpServer, logger))
                    .ConfigureAwait(false);

                SafeEmit(post, logger, BuildNtpEvent(configuration, ntpServer, result));

                // A clean check (small offset) latches: clock skew matters at the start of the
                // enrollment; once Windows has synced there is nothing new to learn per reboot.
                // A Warning result (offset > tolerance, or failure) keeps the key unset so the
                // next run re-checks — and eventually emits the closing "offset OK" Info event.
                if (IsNtpWithinTolerance(result))
                    gate?.MarkSucceeded(Constants.EventTypes.NtpTimeCheck);
            }
            catch (Exception ex)
            {
                logger.Warning($"StartupEnvironmentProbes: NTP check threw: {ex.Message}");
            }
        }

        internal const int NtpOffsetToleranceSeconds = 60;

        internal static bool IsNtpWithinTolerance(NtpCheckResult result) =>
            result != null && result.Success && Math.Abs(result.OffsetSeconds) <= NtpOffsetToleranceSeconds;

        // --------------------------------------------------------------- Event builders (internal for tests)

        internal static EnrollmentEvent BuildGeoEvent(AgentConfiguration configuration, GeoLocationResult location) =>
            new EnrollmentEvent
            {
                SessionId = configuration.SessionId,
                TenantId = configuration.TenantId,
                EventType = Constants.EventTypes.DeviceLocation,
                Severity = EventSeverity.Info,
                Source = "StartupEnvironmentProbes",
                Phase = EnrollmentPhase.Unknown,
                Timestamp = DateTime.UtcNow,
                Message = $"Device location: {location.City}, {location.Region}, {location.Country} (via {location.Source})",
                Data = location.ToDictionary(),
                ImmediateUpload = true,
            };

        internal static EnrollmentEvent BuildOutboundIpEvent(AgentConfiguration configuration, GeoLocationResult location) =>
            new EnrollmentEvent
            {
                SessionId = configuration.SessionId,
                TenantId = configuration.TenantId,
                EventType = Constants.EventTypes.OutboundIp,
                Severity = EventSeverity.Trace,
                Source = "StartupEnvironmentProbes",
                Phase = EnrollmentPhase.Unknown,
                Timestamp = DateTime.UtcNow,
                Message = $"Outbound IP {location.Ip} (via {location.Source})",
                Data = new Dictionary<string, object>
                {
                    { "ip", location.Ip },
                    { "source", location.Source },
                },
            };

        internal static EnrollmentEvent BuildGeoFailureEvent(AgentConfiguration configuration, GeoLocationAttemptResult? attempt) =>
            new EnrollmentEvent
            {
                SessionId = configuration.SessionId,
                TenantId = configuration.TenantId,
                EventType = Constants.EventTypes.AgentTrace,
                Severity = EventSeverity.Warning,
                Source = "StartupEnvironmentProbes",
                Phase = EnrollmentPhase.Unknown,
                Timestamp = DateTime.UtcNow,
                Message = "Geo-location lookup failed: all providers unreachable",
                Data = new Dictionary<string, object>
                {
                    { "decision", "geo_location_failed" },
                    { "reason", "All geo-location providers failed after retry" },
                    { "primaryError", attempt?.PrimaryError ?? "unknown" },
                    { "primaryRetryError", attempt?.PrimaryRetryError ?? "unknown" },
                    { "fallbackError", attempt?.FallbackError ?? "unknown" },
                    { "primaryProvider", "ipinfo.io" },
                    { "fallbackProvider", "ifconfig.co" },
                },
            };

        internal static EnrollmentEvent BuildTimezoneEvent(AgentConfiguration configuration, TimezoneSetResult tz) =>
            new EnrollmentEvent
            {
                SessionId = configuration.SessionId,
                TenantId = configuration.TenantId,
                EventType = Constants.EventTypes.TimezoneAutoSet,
                Severity = tz.Success ? EventSeverity.Info : EventSeverity.Warning,
                Source = "StartupEnvironmentProbes",
                Phase = EnrollmentPhase.Unknown,
                Timestamp = DateTime.UtcNow,
                Message = tz.Success
                    ? $"Timezone set to {tz.WindowsTimezoneId} (from {tz.IanaTimezone})"
                    : $"Timezone auto-set failed: {tz.Error}",
                Data = new Dictionary<string, object>
                {
                    { "ianaTimezone", tz.IanaTimezone ?? "" },
                    { "windowsTimezoneId", tz.WindowsTimezoneId ?? "unknown" },
                    { "previousTimezone", tz.PreviousTimezone ?? "unknown" },
                    { "success", tz.Success },
                    { "error", tz.Error ?? "" },
                },
            };

        internal static EnrollmentEvent BuildNtpEvent(AgentConfiguration configuration, string ntpServer, NtpCheckResult result)
        {
            if (result.Success)
            {
                var severity = IsNtpWithinTolerance(result) ? EventSeverity.Info : EventSeverity.Warning;
                return new EnrollmentEvent
                {
                    SessionId = configuration.SessionId,
                    TenantId = configuration.TenantId,
                    EventType = Constants.EventTypes.NtpTimeCheck,
                    Severity = severity,
                    Source = "StartupEnvironmentProbes",
                    Phase = EnrollmentPhase.Unknown,
                    Timestamp = DateTime.UtcNow,
                    Message = $"NTP time check: offset {result.OffsetSeconds:F2}s from {ntpServer}",
                    Data = new Dictionary<string, object>
                    {
                        { "ntpServer", ntpServer },
                        { "offsetSeconds", result.OffsetSeconds },
                        { "ntpTimeUtc", result.NtpTime?.ToString("o") ?? "" },
                        { "localTimeUtc", result.LocalTime?.ToString("o") ?? "" },
                    },
                };
            }

            return new EnrollmentEvent
            {
                SessionId = configuration.SessionId,
                TenantId = configuration.TenantId,
                EventType = Constants.EventTypes.NtpTimeCheck,
                Severity = EventSeverity.Warning,
                Source = "StartupEnvironmentProbes",
                Phase = EnrollmentPhase.Unknown,
                Timestamp = DateTime.UtcNow,
                Message = $"NTP time check failed: {result.Error}",
                Data = new Dictionary<string, object>
                {
                    { "ntpServer", ntpServer },
                    { "error", result.Error ?? "unknown" },
                },
            };
        }

        private static void RunPowerStateCheck(
            AgentConfiguration configuration,
            AgentLogger logger,
            Orchestration.InformationalEventPost post)
        {
            try
            {
                var result = PowerStateProbe.Probe();
                SafeEmit(post, logger, BuildPowerStateEvent(configuration, result));
            }
            catch (Exception ex)
            {
                logger.Warning($"StartupEnvironmentProbes: power-state check threw: {ex.Message}");
            }
        }

        internal const int PowerStateBatteryWarningThresholdPercent = 80;

        internal static EnrollmentEvent BuildPowerStateEvent(AgentConfiguration configuration, PowerStateResult result)
        {
            EventSeverity severity;
            string message;

            if (!string.IsNullOrEmpty(result.ProbeError))
            {
                severity = EventSeverity.Warning;
                message = $"Power state probe failed: {result.ProbeError}";
            }
            else if (!result.HasBattery)
            {
                severity = EventSeverity.Info;
                message = "Power state: AC, no battery (desktop)";
            }
            else if (result.OnAcPower)
            {
                severity = EventSeverity.Info;
                message = $"Power state: AC, battery {FormatPercent(result.BatteryPercent)}";
            }
            else if (result.BatteryPercent.HasValue && result.BatteryPercent.Value < PowerStateBatteryWarningThresholdPercent)
            {
                severity = EventSeverity.Warning;
                message = $"Power state: on battery, {result.BatteryPercent.Value}% — low charge for enrollment";
            }
            else
            {
                severity = EventSeverity.Info;
                message = $"Power state: on battery, {FormatPercent(result.BatteryPercent)}";
            }

            var data = new Dictionary<string, object>
            {
                { "onAcPower", result.OnAcPower },
                { "hasBattery", result.HasBattery },
                { "batteryPercent", (object?)result.BatteryPercent ?? "unknown" },
                { "isCharging", result.IsCharging },
                { "batteryLifeMinutes", (object?)result.BatteryLifeMinutes ?? "unknown" },
            };

            if (!string.IsNullOrEmpty(result.ProbeError))
                data["error"] = result.ProbeError!;

            return new EnrollmentEvent
            {
                SessionId = configuration.SessionId,
                TenantId = configuration.TenantId,
                EventType = Constants.EventTypes.PowerStateCheck,
                Severity = severity,
                Source = "StartupEnvironmentProbes",
                Phase = EnrollmentPhase.Unknown,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Data = data,
            };
        }

        private static string FormatPercent(int? percent) =>
            percent.HasValue ? $"{percent.Value}%" : "unknown";

        private static void SafeEmit(Orchestration.InformationalEventPost post, AgentLogger logger, EnrollmentEvent evt)
        {
            try { post.Emit(evt); }
            catch (Exception ex) { logger.Warning($"StartupEnvironmentProbes: emit failed for '{evt.EventType}': {ex.Message}"); }
        }
    }
}
