#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers
{
    /// <summary>
    /// Reads the Winlogon AutoLogon configuration and emits a single
    /// <see cref="Constants.EventTypes.AutoLogonAnalysis"/> event carrying the raw facts.
    /// <para>
    /// AutoLogon (<c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon</c>) is a classic
    /// way to automate sign-in. It can be legitimate (kiosk / shared-device deployment) OR a
    /// manipulation vector to automate enrollment / OOBE.
    /// </para>
    /// <para>
    /// Note on grading: a normal user-driven Autopilot enrollment leaves <c>AutoAdminLogon=1</c>
    /// with a <c>DefaultUserName</c>, an <c>AutoLogonSID</c> and NO <c>DefaultPassword</c> — Windows'
    /// own ESP auto-logon for the enrolling user. That state is registry-indistinguishable from a
    /// Sysinternals-Autologon / kiosk setup (both leave AutoLogon enabled + a user but no plaintext
    /// password), so AutoLogon-enabled alone is NOT a reliable signal during Autopilot. The only
    /// unambiguous risk is a plaintext <c>DefaultPassword</c> on disk — graded by ANALYZE-SEC-003.
    /// </para>
    /// <para>
    /// This analyzer reports facts ONLY, always at <see cref="EventSeverity.Info"/>. It never reads
    /// or emits the <c>DefaultPassword</c> value — only its presence. Whether an AutoLogon is a
    /// problem is judged by backend analyze-rules (ANALYZE-SEC-003 = plaintext DefaultPassword on
    /// disk → escalated; ANALYZE-SEC-004 = optional kiosk allow-list template).
    /// </para>
    /// <para>
    /// <b>Trigger</b>: NOT at agent start — the Winlogon keys are written later by a provisioning
    /// script / app / kiosk config, so an at-start read inspects an empty key. Runs instead at
    /// DeviceSetup-phase completion (<c>device_setup_complete</c>) and again at final shutdown
    /// (<c>shutdown</c>), giving the backend a device-phase vs user-phase delta.
    /// </para>
    /// </summary>
    public sealed class AutoLogonAnalyzer : IAgentAnalyzer
    {
        internal const string SourceName = "AutoLogonAnalyzer";

        private const string WinlogonSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;

        // Test seam: when set, the analyzer uses this snapshot instead of reading the registry.
        // Production never assigns it. Surfaced to the test assembly via InternalsVisibleTo.
        internal AutoLogonSnapshot? SnapshotOverride { get; set; }

        public AutoLogonAnalyzer(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Name => SourceName;

        /// <summary>
        /// Startup is a deliberate no-op: the AutoLogon keys are written later during enrollment,
        /// so a read here would only ever inspect an empty Winlogon key.
        /// </summary>
        public void AnalyzeAtStartup()
        {
            _logger.Debug($"{Name}: Startup no-op (AutoLogon keys are written later during enrollment).");
        }

        /// <summary>Final-shutdown scan — captures any AutoLogon written during the user phase.</summary>
        public void AnalyzeAtShutdown()
        {
            _logger.Debug($"{Name}: Running shutdown AutoLogon scan");
            RunScan("shutdown");
        }

        /// <summary>
        /// DeviceSetup-phase-completion scan — captures any AutoLogon injected by a device-targeted
        /// provisioning script / app before the user phase. Invoked by the device-setup trigger.
        /// </summary>
        public void AnalyzeAtDeviceSetupComplete()
        {
            _logger.Debug($"{Name}: Running device-setup-complete AutoLogon scan");
            RunScan("device_setup_complete");
        }

        // -----------------------------------------------------------------------
        // Core scan
        // -----------------------------------------------------------------------

        private void RunScan(string trigger)
        {
            try
            {
                var snapshot = SnapshotOverride ?? ReadWinlogon();
                var data = BuildPayload(snapshot, trigger);

                var autologonEnabled = data.TryGetValue("checks", out var c)
                    && c is Dictionary<string, object> checks
                    && checks.TryGetValue("autologon_enabled", out var ae)
                    && ae is bool b && b;

                var passwordPresent = snapshot.DefaultPasswordPresent;
                var message = autologonEnabled || passwordPresent
                    ? $"{Name}: AutoLogon indicators detected ({string.Join(",", (List<string>)data["findings"])})"
                    : $"{Name}: No AutoLogon configured";

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.AutoLogonAnalysis,
                    // Raw facts only — severity is always Info; backend analyze-rules grade it.
                    Severity = EventSeverity.Info,
                    Source = Name,
                    Phase = EnrollmentPhase.Unknown,
                    Message = message,
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: AutoLogon scan failed unexpectedly", ex);
            }
        }

        /// <summary>
        /// Reads the Winlogon AutoLogon values. Forced Registry64 view — AnyCPU net48 may resolve
        /// to 32-bit and silently read the stale WOW6432Node mirror (same rationale as the PPKG
        /// collector and TenantIdResolver). Fail-soft: probe errors are captured, never thrown.
        /// </summary>
        internal AutoLogonSnapshot ReadWinlogon()
        {
            var snap = new AutoLogonSnapshot();
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey(WinlogonSubKey, writable: false))
                {
                    if (key == null)
                    {
                        snap.Errors.Add("winlogon_key_missing");
                        return snap;
                    }

                    snap.WinlogonKeyPresent = true;
                    snap.AutoAdminLogon = ReadString(key, "AutoAdminLogon");
                    snap.ForceAutoLogon = ReadString(key, "ForceAutoLogon");
                    snap.DefaultUserName = ReadString(key, "DefaultUserName");
                    snap.DefaultDomainName = ReadString(key, "DefaultDomainName");
                    snap.AltDefaultUserName = ReadString(key, "AltDefaultUserName");
                    snap.AltDefaultDomainName = ReadString(key, "AltDefaultDomainName");
                    snap.AutoLogonSid = ReadString(key, "AutoLogonSID");
                    snap.AutoLogonCount = ReadInt(key, "AutoLogonCount");
                    // Presence only — detected via value-NAME enumeration so the credential value
                    // is never pulled into process memory (GetValue would retrieve it). The value
                    // itself is never read or emitted; only the fact that it exists.
                    snap.DefaultPasswordPresent = key.GetValueNames()
                        .Any(n => string.Equals(n, "DefaultPassword", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"registry:{ex.GetType().Name}: {ex.Message}");
                _logger.Warning($"{Name}: Failed to read Winlogon registry: {ex.Message}");
            }
            return snap;
        }

        // -----------------------------------------------------------------------
        // Pure aggregation — no IO, unit-tested directly.
        // -----------------------------------------------------------------------

        internal static Dictionary<string, object> BuildPayload(AutoLogonSnapshot snap, string trigger)
        {
            bool autoAdminLogonEnabled = IsTruthy(snap.AutoAdminLogon);
            bool forceAutoLogonEnabled = IsTruthy(snap.ForceAutoLogon);
            bool autologonEnabled = autoAdminLogonEnabled || forceAutoLogonEnabled;
            bool defaultIdentityPresent =
                !string.IsNullOrEmpty(snap.DefaultUserName) || !string.IsNullOrEmpty(snap.DefaultDomainName);

            // Factual indicator labels (no judgement — severity is the rule's job).
            // Note: there is deliberately NO "sysinternals suspected" label. The heuristic it used
            // (AutoLogon enabled + a default user + no registry password) is the exact fingerprint
            // of Windows' own ESP auto-logon during a normal Autopilot enrollment, so it fired on
            // every device and graded benign enrollments as suspicious. A plaintext DefaultPassword
            // (below) is the only unambiguous signal we surface.
            var findings = new List<string>();
            if (autoAdminLogonEnabled) findings.Add("auto_admin_logon_enabled");
            if (forceAutoLogonEnabled) findings.Add("force_auto_logon_enabled");
            if (snap.DefaultPasswordPresent) findings.Add("default_password_present");
            if (defaultIdentityPresent) findings.Add("default_identity_present");

            // Primary label, most-notable-first (still factual).
            string finding =
                snap.DefaultPasswordPresent ? "plaintext_password_present"
                : autologonEnabled ? "autologon_active"
                : defaultIdentityPresent ? "default_identity_present"
                : "no_autologon";

            var checks = new Dictionary<string, object>
            {
                { "winlogon_key_present", snap.WinlogonKeyPresent },
                { "auto_admin_logon", snap.AutoAdminLogon ?? string.Empty },
                { "auto_admin_logon_enabled", autoAdminLogonEnabled },
                { "force_auto_logon", snap.ForceAutoLogon ?? string.Empty },
                { "force_auto_logon_enabled", forceAutoLogonEnabled },
                { "autologon_enabled", autologonEnabled },
                { "default_user_name", snap.DefaultUserName ?? string.Empty },
                { "default_domain_name", snap.DefaultDomainName ?? string.Empty },
                { "alt_default_user_name", snap.AltDefaultUserName ?? string.Empty },
                { "alt_default_domain_name", snap.AltDefaultDomainName ?? string.Empty },
                { "auto_logon_sid", snap.AutoLogonSid ?? string.Empty },
                { "auto_logon_count", snap.AutoLogonCount.HasValue ? (object)snap.AutoLogonCount.Value : string.Empty },
                { "default_password_present", snap.DefaultPasswordPresent },
                { "default_identity_present", defaultIdentityPresent },
            };

            return new Dictionary<string, object>
            {
                { "severity", "info" },
                { "finding", finding },
                { "findings", findings },
                { "triggered_at", trigger },
                { "enrollment_phase_at_check", EnrollmentPhase.Unknown.ToString() },
                { "checks", checks },
                { "scan_errors", snap.Errors.ToList() },
            };
        }

        // -----------------------------------------------------------------------
        // Small fail-soft helpers.
        // -----------------------------------------------------------------------

        /// <summary>AutoLogon flags are REG_SZ "0"/"1"; treat "1"/"true" (any case) as enabled.</summary>
        private static bool IsTruthy(string? value)
        {
            if (value is not string raw || string.IsNullOrWhiteSpace(raw)) return false;
            var v = raw.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ReadString(RegistryKey key, string valueName)
        {
            try
            {
                var v = key.GetValue(valueName);
                if (v == null) return null;
                var s = v.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        private static int? ReadInt(RegistryKey key, string valueName)
        {
            try
            {
                var v = key.GetValue(valueName);
                if (v == null) return null;
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }
    }

    // --------------------------------------------------------------------------------------
    // Raw scan model (internal; surfaced to the test assembly via InternalsVisibleTo).
    // --------------------------------------------------------------------------------------

    internal sealed class AutoLogonSnapshot
    {
        public bool WinlogonKeyPresent { get; set; }
        public string? AutoAdminLogon { get; set; }
        public string? ForceAutoLogon { get; set; }
        public string? DefaultUserName { get; set; }
        public string? DefaultDomainName { get; set; }
        public string? AltDefaultUserName { get; set; }
        public string? AltDefaultDomainName { get; set; }
        public string? AutoLogonSid { get; set; }
        public int? AutoLogonCount { get; set; }
        public bool DefaultPasswordPresent { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }
}
